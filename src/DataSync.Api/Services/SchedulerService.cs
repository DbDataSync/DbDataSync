using DataSync.Core.Config;
using DataSync.State;

namespace DataSync.Api.Services;

/// <summary>Evaluates every enabled replication's schedule on a tick and triggers due ones
/// (architecture/detailed-design.md §3.1).</summary>
public sealed class SchedulerService(
    ConfigRepository configRepository,
    TaskRunStore taskRunStore,
    ProcessSupervisor supervisor,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await TickAsync();
    }

    private async Task TickAsync()
    {
        foreach (var name in configRepository.ListReplications())
        {
            ReplicationTaskConfig task;
            try
            {
                task = configRepository.LoadReplicationTask(name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping replication '{Name}': failed to load config.", name);
                continue;
            }

            if (!task.Enabled)
                continue;

            var lastRun = taskRunStore.GetRunHistory(name, limit: 1);
            var lastStartedAtUtc = lastRun.Count > 0 ? lastRun[0].StartedAtUtc : (DateTimeOffset?)null;

            if (SchedulingEvaluator.IsDue(task.Scheduling, lastStartedAtUtc, DateTimeOffset.UtcNow))
            {
                var result = await supervisor.TriggerRunAsync(name);
                if (result.Outcome is not TriggerOutcome.Started and not TriggerOutcome.AlreadyRunning)
                    logger.LogWarning("Scheduled trigger for '{Name}' did not start: {Reason}", name, result.Reason);
            }
        }
    }
}
