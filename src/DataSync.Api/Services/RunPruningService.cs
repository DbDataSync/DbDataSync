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
    ApiOptions options,
    ILogger<RunPruningService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxAge = options.RunRetentionDays is { } days ? TimeSpan.FromDays(days) : (TimeSpan?)null;
        var maxPerMapping = options.RunRetentionMaxPerMapping;

        if (maxAge is null && maxPerMapping is null)
        {
            // Said out loud once at startup rather than silently doing nothing forever. An operator who
            // turned both caps off should see that reflected somewhere, and an operator who thinks they
            // configured retention and did not should find out here rather than from a disk.
            logger.LogInformation(
                "Run history pruning is off: neither DataSync:RunRetentionDays nor " +
                "DataSync:RunRetentionMaxPerMapping is set to a positive value.");
            return;
        }

        logger.LogInformation(
            "Pruning run history every {Interval}: keeping {Days} and at most {Max} run(s) per mapping.",
            options.RunPruningInterval,
            maxAge is { } age ? $"{age.TotalDays:0} day(s)" : "runs of any age",
            maxPerMapping?.ToString() ?? "unlimited");

        // Immediately, then on the interval. An API that has just started after being down for a week
        // has a week of runs to catch up on, and waiting an hour to begin is an hour of holding rows
        // that were already past their retention when the process launched.
        await PruneAsync(maxAge, maxPerMapping);

        using var timer = new PeriodicTimer(options.RunPruningInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PruneAsync(maxAge, maxPerMapping);
    }

    private Task PruneAsync(TimeSpan? maxAge, int? maxPerMapping)
    {
        try
        {
            var pruned = taskRunStore.PruneRuns(maxAge, maxPerMapping);
            if (pruned > 0)
                logger.LogInformation("Pruned {Count} run(s) and their log lines from run history.", pruned);
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
