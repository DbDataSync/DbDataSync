using DataSync.Core.Config;
using DataSync.State;

namespace DataSync.Api.Services;

/// <summary>
/// Evaluates every enabled replication's schedule on a tick and enqueues due Primary work
/// (architecture/detailed-design.md §3.1). Due-ness is evaluated per table mapping for Continuous
/// replications — a replication can have hundreds of mappings, and one shared due-ness check would
/// mean one slow/behind mapping resets the clock for every other mapping's schedule. Periodic (cron)
/// due-ness stays replication-scoped (one occurrence fires for the whole replication, anchored to the
/// most recent of all its mappings' last Primary starts), then batch-enqueues every mapping at once —
/// see architecture/implementation/phase-9-work-queue-schema.md.
/// </summary>
public sealed class SchedulerService(
    ConfigRepository configRepository,
    TaskRunStore taskRunStore,
    WorkQueueStore workQueueStore,
    ProcessSupervisor supervisor,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            Tick();
    }

    private void Tick()
    {
        foreach (var name in configRepository.ListReplications())
        {
            ReplicationTaskConfig task;
            List<string> mappingNames;
            try
            {
                task = configRepository.LoadReplicationTask(name);
                mappingNames = configRepository.ListTableMappings(name).ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping replication '{Name}': failed to load config.", name);
                continue;
            }

            if (!task.Enabled || mappingNames.Count == 0)
                continue;

            var enqueuedAny = task.Scheduling.Mode == ScheduleMode.Periodic
                ? TickPeriodic(name, task, mappingNames)
                : TickContinuous(name, task, mappingNames);

            // Idempotent and cheap to skip when nothing changed — only worth spawning a worker to
            // check an empty queue when this tick actually put something new into it.
            if (!enqueuedAny)
                continue;

            var result = supervisor.EnsureWorkerRunning(name);
            if (result.Outcome == TriggerOutcome.FailedToStart)
                logger.LogWarning("Scheduled worker for '{Name}' failed to start: {Reason}", name, result.Reason);
        }
    }

    private bool TickContinuous(string name, ReplicationTaskConfig task, List<string> mappingNames)
    {
        var lastStarts = taskRunStore.GetLastPrimaryStartByMapping(name);
        var now = DateTimeOffset.UtcNow;
        var enqueuedAny = false;

        foreach (var mappingName in mappingNames)
        {
            var lastStart = lastStarts.TryGetValue(mappingName, out var value) ? value : (DateTimeOffset?)null;
            if (!SchedulingEvaluator.IsDue(task.Scheduling, lastStart, now))
                continue;

            workQueueStore.Enqueue(name, RunKind.Primary, mappingName);
            enqueuedAny = true;
        }

        return enqueuedAny;
    }

    private bool TickPeriodic(string name, ReplicationTaskConfig task, List<string> mappingNames)
    {
        var lastStarts = taskRunStore.GetLastPrimaryStartByMapping(name);
        var lastAny = lastStarts.Count > 0 ? lastStarts.Values.Max() : (DateTimeOffset?)null;

        if (!SchedulingEvaluator.IsDue(task.Scheduling, lastAny, DateTimeOffset.UtcNow))
            return false;

        foreach (var mappingName in mappingNames)
            workQueueStore.Enqueue(name, RunKind.Primary, mappingName);

        return true;
    }
}
