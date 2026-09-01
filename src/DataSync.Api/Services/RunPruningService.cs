using DataSync.Api.Configuration;
using DataSync.State;

namespace DataSync.Api.Services;

/// <summary>
/// Keeps run history inside its configured retention — see phase 60.
/// <para>
/// **In the API process**, alongside <see cref="SchedulerService"/> and <see cref="RunMonitorService"/>
/// and for the same reason: phase 39 made this process the state store's single writer, and a pruner
/// anywhere else would be a second one. That would need its own answer to every question phase 39
/// already answered, in exchange for nothing.
/// </para>
/// <para>
/// **Coarse on purpose.** Nothing about retention is time-sensitive — a run that should have gone an
/// hour ago costs a few kilobytes — and a frequent sweep would be contention with the writers that do
/// matter, for a benefit nobody could observe.
/// </para>
/// </summary>
public sealed class RunPruningService(
    TaskRunStore taskRunStore,
    ChangeCheckStore changeCheckStore,
    NotificationStore notificationStore,
    ApiOptions options,
    ILogger<RunPruningService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxAge = options.RunRetentionDays is { } days ? TimeSpan.FromDays(days) : (TimeSpan?)null;
        var maxPerMapping = options.RunRetentionMaxPerMapping;

        // Phase 75's change-check history sweeps on this same tick rather than in a service of its
        // own — a second background service would need its own answer to every question phase 39 and
        // phase 60 already answered, for one more DELETE. Its window is its own, though: see
        // ApiOptions.ChangeCheckRetentionDays for the arithmetic that made sharing RunRetentionDays
        // the wrong call.
        var checkMaxAge = options.ChangeCheckRetentionDays is { } checkDays
            ? TimeSpan.FromDays(checkDays)
            : (TimeSpan?)null;

        // Phase 77's notifications sweep on this same tick, as a third table, and share
        // RunRetentionDays rather than taking a knob of their own. The arithmetic phase 75 did for
        // ChangeCheckHistory is what settles it: that table writes one row per scheduler tick per
        // source group — 17,280 a day whether or not anything happens — which is why it needed its
        // own, much shorter window. This one writes one row per notable event, and every kind of
        // event it announces is bounded by something RunRetentionDays already governs: a run failure
        // cannot outnumber runs. A feed that outlived the run history explaining it would be a feed
        // full of sentences about rows nobody can look up.
        if (maxAge is null && maxPerMapping is null && checkMaxAge is null)
        {
            // Said out loud once at startup rather than silently doing nothing forever. An operator who
            // turned both caps off should see that reflected somewhere, and an operator who thinks they
            // configured retention and did not should find out here rather than from a disk.
            logger.LogInformation(
                "History pruning is off: none of DataSync:RunRetentionDays, " +
                "DataSync:RunRetentionMaxPerMapping or DataSync:ChangeCheckRetentionDays is set to a " +
                "positive value.");
            return;
        }

        logger.LogInformation(
            "Pruning history every {Interval}: keeping {Days} and at most {Max} run(s) per mapping, " +
            "and {CheckDays} of change checks and notifications.",
            options.RunPruningInterval,
            maxAge is { } age ? $"{age.TotalDays:0} day(s)" : "runs of any age",
            maxPerMapping?.ToString() ?? "unlimited",
            checkMaxAge is { } checkAge ? $"{checkAge.TotalDays:0} day(s)" : "checks of any age");

        // Immediately, then on the interval. An API that has just started after being down for a week
        // has a week of runs to catch up on, and waiting an hour to begin is an hour of holding rows
        // that were already past their retention when the process launched.
        await PruneAsync(maxAge, maxPerMapping, checkMaxAge);

        using var timer = new PeriodicTimer(options.RunPruningInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PruneAsync(maxAge, maxPerMapping, checkMaxAge);
    }

    /// <summary>
    /// One sweep. Public so a test can prove that all three histories go in the same pass — the
    /// reason none of these tables has a background service of its own, and the thing that would
    /// silently stop being true if one of the deletes were dropped.
    /// </summary>
    public Task PruneAsync(TimeSpan? maxAge, int? maxPerMapping, TimeSpan? checkMaxAge)
    {
        try
        {
            var pruned = taskRunStore.PruneRuns(maxAge, maxPerMapping);
            if (pruned > 0)
                logger.LogInformation("Pruned {Count} run(s) and their log lines from run history.", pruned);

            var prunedChecks = changeCheckStore.PruneChecks(checkMaxAge);
            if (prunedChecks > 0)
                logger.LogInformation("Pruned {Count} change check(s) from the polling history.", prunedChecks);

            var prunedNotifications = notificationStore.PruneNotifications(maxAge);
            if (prunedNotifications > 0)
                logger.LogInformation("Pruned {Count} notification(s) from the feed.", prunedNotifications);
        }
        catch (Exception ex)
        {
            // Logged and swallowed, deliberately: a locked or briefly unavailable state database is a
            // reason to try again next hour, not a reason to take down a background service the
            // process never restarts. Retention is the least urgent thing this process does.
            logger.LogWarning(ex, "Pruning run history failed; it will be retried on the next sweep.");
        }

        return Task.CompletedTask;
    }
}
