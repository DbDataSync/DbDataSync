using System.Collections.Concurrent;
using System.Diagnostics;
using DataSync.Api.Configuration;
using DataSync.Api.State;
using DataSync.State.Remote;
using DataSync.Core.Config;
using DataSync.State;

namespace DataSync.Api.Services;

/// <summary>
/// Spawns DataSync.TaskRunner as a genuine child process per replication (architecture/detailed-design.md
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
    JournalRecovery journalRecovery)
{
    private readonly ConcurrentDictionary<string, Process> _workers = new();

    public IReadOnlyCollection<string> ActiveTaskNames => _workers.Keys.ToList();

    /// <summary>Idempotent: no-op if a live worker process is already tracked for this replication.
    /// Spawning a process is comparatively slow and never has to be on a request's critical path —
    /// callers enqueue work first (cheap, a few SQLite writes) and call this after.</summary>
    public TriggerResult EnsureWorkerRunning(string taskName)
    {
        if (_workers.TryGetValue(taskName, out var existing) && !existing.HasExited)
            return TriggerResult.Started([]);

        var startInfo = BuildStartInfo(options, taskName, stateHost.BaseAddress, runnerToken.Value);

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
    public static ProcessStartInfo BuildStartInfo(ApiOptions options, string taskName, string stateEndpoint, string token)
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
        foreach (var run in taskRunStore.GetRunningRuns())
        {
            if (run.Pid is int pid && IsProcessAlive(pid))
                continue;

            taskRunStore.CompleteRun(run.RunId, RunStatus.Failed, 0, 0, "Orphaned: no live process found after API restart.");
            runLockStore.Release(run.TaskName, run.RunKind, run.MappingName);
        }
    }

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
