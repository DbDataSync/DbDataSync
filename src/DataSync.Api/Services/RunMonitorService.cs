using DataSync.Api.Hubs;
using DataSync.State;
using Microsoft.AspNetCore.SignalR;

namespace DataSync.Api.Services;

/// <summary>
/// Polls the central SQLite Logs/TaskRuns tables for each run ProcessSupervisor is currently tracking
/// and republishes over SignalR (architecture/detailed-design.md §3.1) — the API never receives a
/// direct callback from a TaskRunner child process, since state flows through DataSync.State, not an
/// IPC channel.
/// </summary>
public sealed class RunMonitorService(
    ProcessSupervisor supervisor,
    TaskRunStore taskRunStore,
    LogWriter logWriter,
    IHubContext<RunHub> hub) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<Guid, long> _lastLogId = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        supervisor.ReconcileOrphanedRuns();

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var runId in supervisor.ActiveRunIds)
                await PollRunAsync(runId, stoppingToken);
        }
    }

    private async Task PollRunAsync(Guid runId, CancellationToken cancellationToken)
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

        if (supervisor.TryGetProcess(runId, out var process) && process!.HasExited)
        {
            var run = taskRunStore.GetRun(runId);
            await group.SendAsync(
                "runCompleted",
                new { runId, status = run?.Status.ToString(), run?.RowsRead, run?.RowsWritten, run?.ErrorSummary },
                cancellationToken);

            supervisor.RemoveActiveRun(runId);
            _lastLogId.Remove(runId);
        }
    }
}
