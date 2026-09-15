using DbDataSync.Core.Config;

namespace DbDataSync.State;

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
    LogWriter logs,
    /// <summary>Phase 134: the batch rollup <see cref="CompleteRun"/> checks after a BulkLoad segment
    /// finishes, to decide whether it just completed the batch a mapping's pending initial load is
    /// waiting on.</summary>
    BulkLoadBatchStore bulkLoadBatches,
    /// <summary>Phase 134: <see cref="RequestInitialLoad"/>'s own segment-expansion/enqueue core — the
    /// same one the operator-facing bulk-load endpoint uses (via <c>BulkLoadService</c>, reached
    /// through this interface — see its own doc for why), so there is one place that turns "start a
    /// bulk load" into work-queue rows rather than two.</summary>
    IInitialLoadEnqueuer initialLoadEnqueuer) : IRunnerState
{
    public void UpsertTask(string taskName, bool enabled) => taskRuns.UpsertTask(taskName, enabled);

    public bool HasOutstandingWork(string taskName, RunLane lane) => workQueue.HasOutstandingWork(taskName, lane);

    public WorkItem? TryClaimNext(string taskName, string workerId, RunLane lane) =>
        workQueue.TryClaimNext(taskName, workerId, lane);

    public bool TryAcquireLock(string taskName, RunKind runKind, string mappingName, Guid runId) =>
        runLocks.TryAcquire(taskName, runKind, mappingName, runId);

    public string? GetWatermark(string taskName, string mappingName, string sourceTable) =>
        watermarks.GetWatermark(taskName, mappingName, sourceTable);

    public MappingReadState? GetReadState(string taskName, string mappingName, string sourceTable) =>
        watermarks.GetReadState(taskName, mappingName, sourceTable);

    public void BeginRun(Guid runId, int? pid) => taskRuns.BeginRun(runId, pid);

    /// <summary>
    /// Phase 134. Fail closed, in this order: the pending position and the <c>Loading</c> hold are
    /// durable before any work exists to do it, never the other way around — a crash between the two
    /// steps must leave "held, nothing queued yet", not "queued, with nothing stopping a concurrent
    /// Primary pass against the same mapping".
    /// </summary>
    public void RequestInitialLoad(
        string taskName, string mappingName, string sourceTable,
        string capturedPosition, DateTimeOffset? capturedPositionTimeUtc)
    {
        var batchId = Guid.NewGuid().ToString("N");

        watermarks.SetPendingLoad(taskName, mappingName, sourceTable, capturedPosition, capturedPositionTimeUtc, batchId);

        // IRunnerState is synchronous end to end (RemoteRunnerState's own HTTP calls block too); the
        // segment expansion this reuses is async only for the Auto/Custom segments an initial load's
        // DefaultSegmenting will rarely use, and this process — the API, under ASP.NET Core's
        // synchronization-context-free server — has nothing for a blocking wait here to deadlock
        // against.
        initialLoadEnqueuer.EnqueueForInitialLoadAsync(taskName, mappingName, batchId, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public void MarkRunning(long workItemId) => workQueue.MarkRunning(workItemId);

    public void MarkDone(long workItemId) => workQueue.MarkDone(workItemId);

    public void MarkFailed(long workItemId) => workQueue.MarkFailed(workItemId);

    public void ReleaseClaim(long workItemId) => workQueue.ReleaseClaim(workItemId);

    public void ReleaseLock(string taskName, RunKind runKind, string mappingName) =>
        runLocks.Release(taskName, runKind, mappingName);

    public void CompleteRun(
        Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary,
        string? failureKind = null,
        RunTiming? timing = null,
        string? previousWatermark = null,
        string? newWatermark = null,
        string? errorDetail = null)
    {
        taskRuns.CompleteRun(
            runId, status, rowsRead, rowsWritten, errorSummary, failureKind, timing,
            previousWatermark, newWatermark, errorDetail);

        // Phase 134: a completed BulkLoad segment may be the last one a batch was waiting on, and that
        // batch may be what a mapping's pending initial load is waiting on. Checked after every BulkLoad
        // segment completes — not only the mapping's own — because a batch reaches Completed only on
        // its *last* segment's completion, which segment that is is not knowable in advance.
        if (taskRuns.GetRunKindAndBatch(runId) is (RunKind.BulkLoad, { } batchId, _))
        {
            var batch = bulkLoadBatches.GetBatch(batchId);
            // CompletedWithFailures does nothing — no flip, no clear, no promote. The mapping stays
            // Loading; an operator can force a fresh attempt through the existing read-state recovery
            // endpoint, the same mechanism PositionExpired recovery already uses.
            if (batch?.State == BulkLoadState.Completed)
                watermarks.PromotePendingLoad(batchId);
        }
    }

    public void SetWatermark(
        string taskName,
        string mappingName,
        string sourceTable,
        string watermark,
        DateTimeOffset? watermarkTimeUtc = null) =>
        watermarks.SetWatermark(taskName, mappingName, sourceTable, watermark, watermarkTimeUtc);

    public void SetReadIntent(string taskName, string mappingName, string sourceTable, ReadIntent intent) =>
        watermarks.SetReadIntent(taskName, mappingName, sourceTable, intent);

    public void SetReadHold(string taskName, string mappingName, string sourceTable, ReadHold hold) =>
        watermarks.SetReadHold(taskName, mappingName, sourceTable, hold);

    public void RecordVerificationResult(VerificationResultRecord result) => verificationResults.Record(result);

    public void Log(Guid runId, LogSeverity level, string message) => logs.Log(runId, level, message);

    public void Flush() => logs.Flush();
}
