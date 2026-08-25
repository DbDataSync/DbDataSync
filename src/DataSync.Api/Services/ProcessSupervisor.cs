using System.Collections.Concurrent;
using System.Diagnostics;
using DataSync.Api.Configuration;
using DataSync.Core.Config;
using DataSync.State;

namespace DataSync.Api.Services;

/// <summary>
/// Spawns DataSync.TaskRunner as a genuine child process per run (architecture/detailed-design.md
/// §3.1) via `dotnet exec &lt;TaskRunnerDllPath&gt;` — the same host running the API, so this works
/// regardless of how TaskRunner itself was published. Tracks active runs for RunMonitorService to
/// poll, and reconciles TaskRuns rows left "Running" by a previous API process on startup.
/// </summary>
public sealed class ProcessSupervisor(
    ApiOptions options,
    ConfigRepository configRepository,
    TaskRunStore taskRunStore,
    RunLockStore runLockStore)
{
    private sealed record ActiveRun(Process Process, string ReplicationName);

    private readonly ConcurrentDictionary<Guid, ActiveRun> _activeRuns = new();

    public IReadOnlyCollection<Guid> ActiveRunIds => _activeRuns.Keys.ToList();

    public Task<TriggerResult> TriggerRunAsync(string replicationName)
    {
        try
        {
            configRepository.LoadReplicationTask(replicationName);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(TriggerResult.NotFound());
        }

        // Fast path for a quick REST response; DataSync.TaskRunner's own atomic RunLocks acquisition
        // (Phase 4) is what actually prevents a race from double-executing.
        if (runLockStore.IsLocked(replicationName))
            return Task.FromResult(TriggerResult.AlreadyRunning());

        var runId = Guid.NewGuid();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(options.TaskRunnerDllPath);
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(options.RepoRoot);
        startInfo.ArgumentList.Add("--state-db");
        startInfo.ArgumentList.Add(options.StateDbPath);
        startInfo.ArgumentList.Add("--replication");
        startInfo.ArgumentList.Add(replicationName);
        startInfo.ArgumentList.Add("--run-id");
        startInfo.ArgumentList.Add(runId.ToString());

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return Task.FromResult(TriggerResult.FailedToStart("Process.Start returned false."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(TriggerResult.FailedToStart(ex.Message));
        }

        _activeRuns[runId] = new ActiveRun(process, replicationName);
        return Task.FromResult(TriggerResult.Started(runId));
    }

    /// <summary>Best-effort process kill. TaskRunner's own RunExecutor writes the final TaskRuns row
    /// on graceful completion; here the Supervisor writes it directly since killing the process skips
    /// that entirely. A run that finishes naturally at almost exactly the same moment this is called
    /// could have its real terminal status overwritten with Cancelled — an accepted, rare race for v1.</summary>
    public bool CancelRun(Guid runId)
    {
        if (!_activeRuns.TryGetValue(runId, out var active))
            return false;

        try
        {
            if (!active.Process.HasExited)
                active.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — fine, fall through to record the outcome.
        }

        taskRunStore.CompleteRun(runId, RunStatus.Cancelled, 0, 0, "Cancelled by user request.");
        runLockStore.Release(active.ReplicationName);
        _activeRuns.TryRemove(runId, out _);
        return true;
    }

    public bool TryGetProcess(Guid runId, out Process? process)
    {
        if (_activeRuns.TryGetValue(runId, out var active))
        {
            process = active.Process;
            return true;
        }

        process = null;
        return false;
    }

    public void RemoveActiveRun(Guid runId) => _activeRuns.TryRemove(runId, out _);

    /// <summary>Runs marked "Running" in the state store but with no live OS process behind them —
    /// left over from a previous API process (crash, restart, deploy) — are marked Failed and their
    /// RunLocks released, so a task doesn't stay permanently un-schedulable. A run that's still
    /// genuinely alive keeps running to completion and writes its own final TaskRuns row when done;
    /// it just isn't monitorable/cancellable via this API instance until then (v1 limitation — see
    /// architecture/implementation/phase-5-api-orchestrator.md).</summary>
    public void ReconcileOrphanedRuns()
    {
        foreach (var run in taskRunStore.GetRunningRuns())
        {
            if (run.Pid is int pid && IsProcessAlive(pid))
                continue;

            taskRunStore.CompleteRun(run.RunId, RunStatus.Failed, 0, 0, "Orphaned: no live process found after API restart.");
            runLockStore.Release(run.TaskName);
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
