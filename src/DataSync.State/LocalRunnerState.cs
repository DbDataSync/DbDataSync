namespace DataSync.State;

/// <summary>
/// <see cref="IRunnerState"/> against the SQLite stores directly, for the process that owns the file.
/// <para>
/// Used by the API — which is the owner — and by tests, which want the real behaviour without a
/// loopback server in the way. A TaskRunner never constructs this: phase 39's whole point is that
/// exactly one process opens the state file.
/// </para>
/// </summary>
public sealed class LocalRunnerState(
    TaskRunStore taskRuns,
    WorkQueueStore workQueue,
    RunLockStore runLocks,
    ChangeWatermarkStore watermarks,
    VerificationResultStore verificationResults,
    LogWriter logs) : IRunnerState
{
    public void UpsertTask(string taskName, bool enabled) => taskRuns.UpsertTask(taskName, enabled);

    public bool HasOutstandingWork(string taskName) => workQueue.HasOutstandingWork(taskName);

    public WorkItem? TryClaimNext(string taskName, string workerId) => workQueue.TryClaimNext(taskName, workerId);

    public bool TryAcquireLock(string taskName, RunKind runKind, string mappingName, Guid runId) =>
        runLocks.TryAcquire(taskName, runKind, mappingName, runId);

    public string? GetWatermark(string taskName, string sourceTable) => watermarks.GetWatermark(taskName, sourceTable);

    public void BeginRun(Guid runId, int? pid) => taskRuns.BeginRun(runId, pid);

    public void MarkRunning(long workItemId) => workQueue.MarkRunning(workItemId);

    public void MarkDone(long workItemId) => workQueue.MarkDone(workItemId);

    public void MarkFailed(long workItemId) => workQueue.MarkFailed(workItemId);

    public void ReleaseClaim(long workItemId) => workQueue.ReleaseClaim(workItemId);

    public void ReleaseLock(string taskName, RunKind runKind, string mappingName) =>
        runLocks.Release(taskName, runKind, mappingName);

    public void CompleteRun(
        Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary,
        string? failureKind = null) =>
        taskRuns.CompleteRun(runId, status, rowsRead, rowsWritten, errorSummary, failureKind);

    public void SetWatermark(string taskName, string sourceTable, string watermark) =>
        watermarks.SetWatermark(taskName, sourceTable, watermark);

    public void RecordVerificationResult(VerificationResultRecord result) => verificationResults.Record(result);

    public void Log(Guid runId, LogSeverity level, string message) => logs.Log(runId, level, message);

    public void Flush() => logs.Flush();
}
