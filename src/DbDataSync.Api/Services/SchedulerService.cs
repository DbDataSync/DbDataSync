using DbDataSync.Core.Config;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Evaluates every enabled replication's schedule on a tick and enqueues due Primary work
/// (architecture/detailed-design.md §3.1). Due-ness is evaluated per table mapping for Continuous
/// replications — a replication can have hundreds of mappings, and one shared due-ness check would
/// mean one slow/behind mapping resets the clock for every other mapping's schedule. Periodic (cron)
/// due-ness stays replication-scoped (one occurrence fires for the whole replication, anchored to the
/// most recent of all its mappings' last Primary starts), then batch-enqueues every mapping at once —
/// see architecture/implementation/done/phase-008-work-queue-schema.md.
/// <para>
/// **Since phase 75, a tick can talk to a source system.** Due-ness is still decided entirely from
/// local config and state; what changed is that a mapping found due on a CDC or Change-Tracking
/// reader is then offered to <see cref="ChangePollingGate"/>, which asks the source database once per
/// tick whether anything is there. That is this service's first I/O against anything it does not
/// own, and it means a tick can now be slowed by a source that is far away or stopped by nothing at
/// all — the gate fails open per source group precisely so an unreachable database costs a wasted
/// dispatch rather than a suppressed schedule.
/// </para>
/// </summary>
public sealed class SchedulerService(
    ConfigRepository configRepository,
    TaskRunStore taskRunStore,
    WorkQueueStore workQueueStore,
    ProcessSupervisor supervisor,
    ChangePollingGate gate,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await TickAsync(stoppingToken);
    }

    private async Task TickAsync(CancellationToken cancellationToken)
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

            // Paused is read from state, Enabled from the config just loaded — both gates, one
            // definition. A pause stops the *next* thing being scheduled; anything already claimed by
            // a worker runs to completion, exactly as disabling has always behaved.
            if (!TaskScheduling.ShouldRun(task.Enabled, taskRunStore.IsPaused(name)) || mappingNames.Count == 0)
                continue;

            var enqueuedAny = task.Scheduling.Mode == ScheduleMode.Periodic
                ? await TickPeriodicAsync(name, task, mappingNames, cancellationToken)
                : await TickContinuousAsync(name, task, mappingNames, cancellationToken);

            // Idempotent and cheap to skip when nothing changed — only worth spawning a worker to
            // check an empty queue when this tick actually put something new into it.
            if (!enqueuedAny)
                continue;

            var result = supervisor.EnsureWorkerRunning(name);
            if (result.Outcome == TriggerOutcome.FailedToStart)
                logger.LogWarning("Scheduled worker for '{Name}' failed to start: {Reason}", name, result.Reason);
        }
    }

    private async Task<bool> TickContinuousAsync(
        string name, ReplicationTaskConfig task, List<string> mappingNames, CancellationToken cancellationToken)
    {
        var lastStarts = taskRunStore.GetLastPrimaryEnqueueByMapping(name);
        var now = DateTimeOffset.UtcNow;
        var due = new List<string>();

        foreach (var mappingName in mappingNames)
        {
            var lastStart = lastStarts.TryGetValue(mappingName, out var value) ? value : (DateTimeOffset?)null;
            if (SchedulingEvaluator.IsDue(task.Scheduling, lastStart, now))
                due.Add(mappingName);
        }

        return await EnqueueAsync(name, task, due, cancellationToken);
    }

    private async Task<bool> TickPeriodicAsync(
        string name, ReplicationTaskConfig task, List<string> mappingNames, CancellationToken cancellationToken)
    {
        var lastStarts = taskRunStore.GetLastPrimaryEnqueueByMapping(name);
        var lastAny = lastStarts.Count > 0 ? lastStarts.Values.Max() : (DateTimeOffset?)null;

        if (!SchedulingEvaluator.IsDue(task.Scheduling, lastAny, DateTimeOffset.UtcNow))
            return false;

        return await EnqueueAsync(name, task, mappingNames, cancellationToken);
    }

    /// <summary>
    /// Enqueues the mappings this tick found due, less any the gate can show have nothing waiting.
    /// <para>
    /// One place for both schedule modes, because the gate's question is about a mapping's source and
    /// not about why it came to be due. A cron occurrence that fires over a source nothing has
    /// written to since the last one is the same wasted pass as a continuous interval doing it, and
    /// the periodic path is if anything the one where a hundred mappings arrive at once.
    /// </para>
    /// <para>
    /// The gate never adds, only removes, and a due-ness clock unchanged by it: a mapping the gate
    /// skips is not enqueued and so does not record a Primary enqueue, which leaves it due again next
    /// tick. That is the intent — being caught up is not progress to be timed from.
    /// </para>
    /// </summary>
    private async Task<bool> EnqueueAsync(
        string name, ReplicationTaskConfig task, IReadOnlyList<string> due, CancellationToken cancellationToken)
    {
        if (due.Count == 0)
            return false;

        var admitted = await gate.AdmitAsync(task, due, cancellationToken);

        foreach (var mappingName in admitted)
            workQueueStore.Enqueue(name, RunKind.Primary, mappingName);

        return admitted.Count > 0;
    }
}
