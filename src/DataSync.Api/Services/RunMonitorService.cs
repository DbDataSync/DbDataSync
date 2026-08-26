using DataSync.Api.Hubs;
using DataSync.State;
using Microsoft.AspNetCore.SignalR;

namespace DataSync.Api.Services;

/// <summary>
/// Polls the central SQLite Logs/TaskRuns tables and republishes over SignalR (architecture/detailed-design.md
/// §3.1) — the API never receives a direct callback from a TaskRunner child process, since state flows
/// through DataSync.State, not an IPC channel.
/// <para>
/// Completion is detected two ways: via TaskRuns.Status leaving Running (a run caught as active on
/// some earlier tick), and via a "recently ended" query covering runs that completed their entire
/// lifecycle between two polling ticks and so were never once observed as active — a real
/// possibility now that a claimed unit of work can complete in well under this service's 1-second
/// tick (no per-run process spawn overhead standing in the way). Process.HasExited is not used at
/// all: one worker process now backs many concurrently-active RunIds. See
/// architecture/implementation/done/phase-008-work-queue-schema.md.
/// </para>
/// </summary>
public sealed class RunMonitorService(
    ProcessSupervisor supervisor,
    TaskRunStore taskRunStore,
    LogWriter logWriter,
    IHubContext<RunHub> hub) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    // Wider than TickInterval so a "recently ended" run is never missed due to ordinary tick jitter —
    // GetRecentlyEndedRuns is filtered by EndedAtUtc, and _notified below de-dupes regardless of how
    // many ticks a given run's completion happens to be visible across.
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(5);

    private readonly Dictionary<Guid, long> _lastLogId = new();
    private readonly HashSet<Guid> _watching = new();
    // Unbounded for the lifetime of the API process — acceptable for now (matches other
    // not-yet-productionized simplifications in this phase); a long-lived API instance processing a
    // very large number of runs would eventually want this pruned or replaced with a persisted
    // "notified" flag on TaskRuns itself.
    private readonly HashSet<Guid> _notified = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        supervisor.ReconcileOrphanedRuns();

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var active = taskRunStore.GetActiveRuns();
            var activeIds = active.Select(r => r.RunId).ToHashSet();

            foreach (var run in active)
            {
                _watching.Add(run.RunId);
                await StreamLogsAsync(run.RunId, stoppingToken);
            }

            // Runs previously caught as active that have since left that set...
            var justCompleted = _watching.Except(activeIds).ToList();
            // ...unioned with runs that completed their whole lifecycle between ticks and so were
            // never in _watching at all (see class doc).
            var recentlyEnded = taskRunStore.GetRecentlyEndedRuns(DateTimeOffset.UtcNow - RecentWindow);
            foreach (var run in recentlyEnded)
            {
                if (_notified.Contains(run.RunId) || justCompleted.Contains(run.RunId))
                    continue;
                justCompleted.Add(run.RunId);
            }

            foreach (var runId in justCompleted)
            {
                if (!_notified.Add(runId))
                    continue;

                await StreamLogsAsync(runId, stoppingToken); // catch any trailing log lines first
                var run = taskRunStore.GetRun(runId);
                await hub.Clients.Group(runId.ToString()).SendAsync(
                    "runCompleted",
                    new { runId, status = run?.Status.ToString(), run?.RowsRead, run?.RowsWritten, run?.ErrorSummary },
                    stoppingToken);

                _watching.Remove(runId);
                _lastLogId.Remove(runId);
            }
        }
    }

    private async Task StreamLogsAsync(Guid runId, CancellationToken cancellationToken)
    {
        var sinceId = _lastLogId.GetValueOrDefault(runId);
        var newLogs = logWriter.GetLogs(runId, sinceId == 0 ? null : sinceId);
        var group = hub.Clients.Group(runId.ToString());

        foreach (var entry in newLogs)
        {
            await group.SendAsync(
                "logLine",
                new { entry.Id, entry.TimestampUtc, Level = entry.Level.ToString(), entry.Message },
                cancellationToken);
            _lastLogId[runId] = entry.Id;
        }
    }
}
