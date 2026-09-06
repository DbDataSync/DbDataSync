using System.Collections.Concurrent;
using System.Diagnostics;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.State;
using DbDataSync.State.Remote;
using DbDataSync.Core.Config;
using DbDataSync.State;

namespace DbDataSync.Api.Services;

/// <summary>
/// Spawns DbDataSync.TaskRunner as a genuine child process per replication (architecture/detailed-design.md
/// §3.1) via `dotnet exec &lt;TaskRunnerDllPath&gt;` — the same host running the API, so this works
/// regardless of how TaskRunner itself was published. Tracked per replication name, not per run: one
/// worker process claims and drains a replication's pending WorkQueue items with internal bounded
/// concurrency across however many table mappings it has, rather than one process per triggered run
/// (see architecture/implementation/done/phase-008-work-queue-schema.md — a replication can have hundreds of
/// mappings, and spawning per mapping/trigger would be far too heavy at that scale).
/// </summary>
public sealed class ProcessSupervisor(
    ApiOptions options,
    ConfigRepository configRepository,
    TaskRunStore taskRunStore,
    RunLockStore runLockStore,
    WorkQueueStore workQueueStore,
    RunnerToken runnerToken,
    StateHost stateHost,
    JournalRecovery journalRecovery,
    ILogger<ProcessSupervisor> logger)
{
    private readonly ConcurrentDictionary<string, Process> _workers = new();

    public IReadOnlyCollection<string> ActiveTaskNames => _workers.Keys.ToList();

    /// <summary>
    /// The worker process for this replication, as it stands right now.
    /// <para>
    /// **Not running is normal, not a fault.** A worker drains its queue and exits, so a replication
    /// that is caught up has no process between cycles — which is most of the time for most of them.
    /// The card that shows this has to say so, or an operator reads a healthy idle replication as a
    /// broken one.
    /// </para>
    /// <para>
    /// Zero or one, matching the one-worker-per-replication model. Deliberately not shaped as a list:
    /// the architecture does not produce one, and a shape that could would be a promise nothing keeps.
    /// </para>
    /// </summary>
    public ReplicationStatus DescribeStatus(string taskName)
    {
        if (!_workers.TryGetValue(taskName, out var process))
            return ReplicationStatus.NotRunning;

        try
        {
            if (process.HasExited)
                return ReplicationStatus.NotRunning;

            // Refreshed, because a Process caches these the first time they are read and would
            // otherwise report the same memory figure for the life of the worker.
            process.Refresh();
            return new ReplicationStatus(
                true, process.Id, process.WorkingSet64, process.TotalProcessorTime.TotalMilliseconds,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // The process ended between the liveness check and the read, or the OS refused the
            // counters. Either way there is nothing to report, and reporting a fault would be
            // reporting one about this endpoint rather than about the replication.
            return ReplicationStatus.NotRunning;
        }
    }

    /// <summary>Idempotent: no-op if a live worker process is already tracked for this replication.
    /// Spawning a process is comparatively slow and never has to be on a request's critical path —
    /// callers enqueue work first (cheap, a few SQLite writes) and call this after.</summary>
    public TriggerResult EnsureWorkerRunning(string taskName)
    {
        if (_workers.TryGetValue(taskName, out var existing) && !existing.HasExited)
            return TriggerResult.Started([]);

        var startInfo = BuildStartInfo(
            options, taskName, stateHost.BaseAddress, runnerToken.Value, ResolveDegreeOfParallelism(taskName));

        // Anything this runner spilled while a previous incarnation could not reach us is applied
        // before it starts working again — so a re-run never races the record of the run before it.
        journalRecovery.Recover(taskName);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return TriggerResult.FailedToStart("Process.Start returned false.");
        }
        catch (Exception ex)
        {
            return TriggerResult.FailedToStart(ex.Message);
        }

        _workers[taskName] = process;
        return TriggerResult.Started([]);
    }

    /// <summary>
    /// How a runner is launched, as a value rather than a side effect — so the one property that
    /// cannot be checked by reading the code later (that no secret is on the command line) can be
    /// asserted directly.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(
        ApiOptions options, string taskName, string stateEndpoint, string token, int degreeOfParallelism)
    {
        var startInfo = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(options.TaskRunnerDllPath);
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(options.RepoRoot);
        startInfo.ArgumentList.Add("--state-db");
        startInfo.ArgumentList.Add(options.StateDbPath);
        startInfo.ArgumentList.Add("--replication");
        startInfo.ArgumentList.Add(taskName);
        // Passed explicitly on every spawn, from the replication's own ChangeProcessing config, rather
        // than left to the runner's built-in default — the whole point of the setting is that a large
        // replication can raise it. Not a secret, so unlike the endpoint/token below it belongs on the
        // command line where it is visible to an operator debugging a worker.
        startInfo.ArgumentList.Add("--degree-of-parallelism");
        startInfo.ArgumentList.Add(degreeOfParallelism.ToString());

        // The endpoint and the token go in the *environment*, not in ArgumentList. On Linux a
        // process's command line is world-readable (/proc/<pid>/cmdline) and its environment is not
        // (/proc/<pid>/environ, mode 0400) — so a token in an argument would be visible to every local
        // user through `ps`, which is precisely the threat it exists to answer. The same asymmetry
        // holds on Windows, where Win32_Process exposes command lines and not environment blocks.
        //
        // UseShellExecute is already false above, which is what makes Environment usable at all.
        startInfo.Environment[StateProtocol.EndpointEnvironmentVariable] = stateEndpoint;
        startInfo.Environment[StateProtocol.TokenEnvironmentVariable] = token;

        return startInfo;
    }

    /// <summary>
    /// The replication's configured degree of parallelism, or the built-in default when its config
    /// cannot be read. A missing task file is not this method's problem to report — the worker it is
    /// about to spawn exits <c>ConfigError</c> for exactly that, and callers that care
    /// (<see cref="TriggerReplication"/>) already load the config first — so this just falls back and
    /// lets that path run.
    /// </summary>
    private int ResolveDegreeOfParallelism(string taskName)
    {
        try
        {
            return configRepository.LoadReplicationTask(taskName).ChangeProcessing.DegreeOfParallelism;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ChangeProcessingConfig.DefaultDegreeOfParallelism;
        }
    }

    /// <summary>The "Run Now" convenience: enqueues a Primary pass for every table mapping of a
    /// replication, then ensures a worker is running to pick them up. Enqueueing a mapping that
    /// already has a Primary pass queued/in-flight is a no-op (see WorkQueueStore.Enqueue) — this
    /// never fails with "already running" the way a single whole-replication lock used to.</summary>
    public TriggerResult TriggerReplication(string replicationName)
    {
        List<string> mappingNames;
        try
        {
            configRepository.LoadReplicationTask(replicationName);
            mappingNames = configRepository.ListTableMappings(replicationName).ToList();
        }
        catch (FileNotFoundException)
        {
            return TriggerResult.NotFound();
        }

        var runIds = mappingNames
            .Select(mappingName => workQueueStore.Enqueue(replicationName, RunKind.Primary, mappingName))
            .ToList();

        var ensureResult = EnsureWorkerRunning(replicationName);
        return ensureResult.Outcome == TriggerOutcome.FailedToStart ? ensureResult : TriggerResult.Started(runIds);
    }

    /// <summary>A Pending (not yet claimed) item is cancelled directly — cheap, no process
    /// interaction. A Claimed/Running item has no per-item cancellation lever yet (see
    /// architecture/implementation/done/phase-008-work-queue-schema.md); the only available action is
    /// stopping the whole worker process for that replication, which also affects any other item that
    /// same process happens to be concurrently processing right now — an accepted v1 limitation,
    /// same spirit as this method's previous single-run version.</summary>
    public bool CancelRun(Guid runId)
    {
        var run = taskRunStore.GetRun(runId);
        if (run is null)
            return false;

        if (workQueueStore.TryCancelPending(runId))
        {
            taskRunStore.CompleteRun(runId, RunStatus.Cancelled, 0, 0, "Cancelled by user request.");
            return true;
        }

        if (!_workers.TryGetValue(run.TaskName, out var process))
            return false;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — fine, fall through to reconcile.
        }

        ReconcileDeadWorker(run.TaskName);
        return true;
    }

    /// <summary>Runs left Running in the store but with no live OS process behind them — left over
    /// from a previous API process (crash, restart, deploy) — are marked Failed and their locks
    /// released, so a mapping doesn't stay permanently un-schedulable. Queued (not yet claimed) rows
    /// need no special handling: they're simply still Pending in WorkQueue, picked up by the next
    /// EnsureWorkerRunning. A run that's still genuinely alive keeps running to completion and writes
    /// its own final TaskRuns row when done — it just isn't tracked by this API instance until then
    /// (v1 limitation — see architecture/implementation/done/phase-005-api-orchestrator.md).</summary>
    public void ReconcileOrphanedRuns()
    {
        var liveTasks = new HashSet<string>();

        foreach (var run in taskRunStore.GetRunningRuns())
        {
            if (run.Pid is int pid && IsProcessAlive(pid))
            {
                liveTasks.Add(run.TaskName);
                continue;
            }

            taskRunStore.CompleteRun(run.RunId, RunStatus.Failed, 0, 0, "Orphaned: no live process found after API restart.");
            runLockStore.Release(run.TaskName, run.RunKind, run.MappingName);
        }

        // Driven by what the queue holds, not by which runs started. A worker claims ahead of its
        // consumers, so it can die holding items it never began — those have no run to be found by,
        // and asking only about runs leaves them in-flight forever, which makes their mapping
        // permanently un-enqueueable and stops the replication silently.
        //
        // Safe only where nothing is alive working on the replication. A worker this API spawned is
        // in _workers; one still executing a run is caught by the pid check above. The gap is a worker
        // left by a *previous* API instance that has claimed work and not yet started it — narrow, and
        // a far better failure than a replication that never runs again.
        foreach (var taskName in workQueueStore.GetTasksWithInFlightWork())
        {
            if (liveTasks.Contains(taskName) || HasLiveWorker(taskName))
                continue;

            var released = workQueueStore.ReleaseClaimsForTask(taskName);
            if (released > 0)
            {
                logger.LogWarning(
                    "Returned {Count} in-flight work item(s) of '{Task}' to the queue: they were claimed by a " +
                    "worker process that is no longer running.", released, taskName);
            }
        }
    }

    private bool HasLiveWorker(string taskName) =>
        _workers.TryGetValue(taskName, out var worker) && !worker.HasExited;

    /// <summary>A worker process backs many concurrently-active RunIds, so its death (whether via
    /// CancelRun's kill or discovered later by ReconcileOrphanedRuns) invalidates its whole in-flight
    /// set at once, not just the one RunId a caller happened to ask about.</summary>
    private void ReconcileDeadWorker(string taskName)
    {
        _workers.TryRemove(taskName, out _);
        foreach (var run in taskRunStore.GetRunningRuns().Where(r => r.TaskName == taskName))
        {
            taskRunStore.CompleteRun(run.RunId, RunStatus.Failed, 0, 0, "Worker process was stopped.");
            runLockStore.Release(run.TaskName, run.RunKind, run.MappingName);
        }

        // The worker is definitively gone — every item it held goes back, including any it claimed
        // ahead and never started.
        workQueueStore.ReleaseClaimsForTask(taskName);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// What a replication's worker process is doing, right now. Live telemetry only — no history, no
/// trend: this answers "is something happening", which is a different question from phase 36's "what
/// has been happening", and that one already has an answer.
/// </summary>
/// <param name="MemoryBytes">Resident set. Null when nothing is running.</param>
/// <param name="CpuMilliseconds">Processor time this worker has used since it started.</param>
/// <param name="ShouldRun">
/// Whether the scheduler is allowed to enqueue anything for this replication — <c>Enabled</c> (config)
/// and not <c>Paused</c> (state), from <see cref="TaskScheduling.ShouldRun"/>. The answer rather than
/// the inputs, so the SPA never re-derives the rule; the inputs come too, because the UI has to say
/// *which* gate is closed and they are different controls with different consequences.
/// </param>
/// <param name="PauseNote">Why it is held, if whoever held it said. Null when not paused, and also
/// when they cleared it — the popup allows both.</param>
public sealed record ReplicationStatus(
    bool Running,
    int? Pid = null,
    long? MemoryBytes = null,
    double? CpuMilliseconds = null,
    DateTimeOffset? StartedAtUtc = null,
    bool ShouldRun = true,
    bool Enabled = true,
    bool Paused = false,
    string? PauseNote = null)
{
    /// <summary>A replication with no worker. The common state, and not a problem — a worker drains
    /// its queue and exits, so an idle replication has no process by design.</summary>
    public static ReplicationStatus NotRunning { get; } = new(false);
}
