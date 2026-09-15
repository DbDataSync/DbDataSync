using DbDataSync.Core.Config;

namespace DbDataSync.State.Tests;

/// <summary>
/// Phase 134's completion hook: <see cref="LocalRunnerState.CompleteRun"/> — the same call every run's
/// outcome goes through — also checks, after recording it, whether this was a <see cref="RunKind.BulkLoad"/>
/// segment and whether the batch it belongs to has just reached <see cref="BulkLoadState.Completed"/>.
/// If so, it promotes whichever mapping's pending initial load names that batch
/// (<see cref="ChangeWatermarkStore.PromotePendingLoad"/>) — the "one act" phase 134's design calls for.
/// <para>
/// Also covers phase 143's own change to <see cref="LocalRunnerState.RequestInitialLoad"/> itself —
/// see the tests at the bottom of this file.
/// </para>
/// </summary>
public sealed class LocalRunnerStateInitialLoadTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-state-tests-").FullName;
    private readonly WorkQueueStore _workQueue;
    private readonly TaskRunStore _taskRuns;
    private readonly ChangeWatermarkStore _watermarks;
    private readonly BulkLoadBatchStore _bulkLoadBatches;
    private readonly LogWriter _logs;
    private readonly LocalRunnerState _state;

    private const string TaskName = "crm-sync";
    private const string MappingName = "orders";
    private const string SourceTable = "orders-db/App/dbo.Orders";

    public LocalRunnerStateInitialLoadTests()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _taskRuns = new TaskRunStore(database);
        _workQueue = new WorkQueueStore(database);
        _watermarks = new ChangeWatermarkStore(database);
        _bulkLoadBatches = new BulkLoadBatchStore(database);
        _logs = new LogWriter(database);

        _state = new LocalRunnerState(
            _taskRuns, _workQueue, new RunLockStore(database), _watermarks,
            new VerificationResultStore(database), _logs, _bulkLoadBatches,
            new Lazy<IInitialLoadEnqueuer>(() => new SingleFullSegmentEnqueuer(_workQueue, _bulkLoadBatches)));
    }

    public void Dispose()
    {
        _logs.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>A real (not stubbed) <see cref="IInitialLoadEnqueuer"/> for
    /// <see cref="RequestInitialLoad_WhenNothingElseIsInFlight_SetsThePendingLoad"/> and its neighbours
    /// below — always a single <see cref="FullSegment"/>, since this file has no
    /// <c>ConfigRepository</c>/mapping to resolve a real <c>DefaultSegmenting</c> from. Mirrors
    /// <c>RealInitialLoadEnqueuer</c>'s own default for an unconfigured mapping.</summary>
    private sealed class SingleFullSegmentEnqueuer(WorkQueueStore workQueue, BulkLoadBatchStore batches)
        : IInitialLoadEnqueuer
    {
        public Task EnqueueForInitialLoadAsync(
            string replicationName, string mappingName, string batchId, CancellationToken cancellationToken)
        {
            batches.CreateBatch(batchId, replicationName, mappingName, segmentCount: 1, estimatedRows: null, estimateCaveat: null);
            workQueue.EnqueueOrThrow(replicationName, RunKind.BulkLoad, mappingName, "full", segmentJson: null, kinds: null, batchId);
            return Task.CompletedTask;
        }
    }

    /// <summary><c>Completed</c> does all three, once: the pending position becomes live, the hold
    /// clears, the intent flips to <c>Changes</c>.</summary>
    [Fact]
    public void CompleteRun_WhenTheLastSegmentCompletesTheBatch_PromotesThePendingLoad()
    {
        const string batchId = "batch-1";
        _bulkLoadBatches.CreateBatch(batchId, TaskName, MappingName, segmentCount: 1, estimatedRows: null, estimateCaveat: null);
        var runId = _workQueue.Enqueue(
            TaskName, RunKind.BulkLoad, MappingName, "full", segmentJson: null, kinds: null, bulkLoadBatchId: batchId);
        _watermarks.SetPendingLoad(TaskName, MappingName, SourceTable, "999", null, batchId);

        _state.CompleteRun(runId, RunStatus.Succeeded, 10, 10, errorSummary: null);

        var read = _watermarks.GetReadState(TaskName, MappingName, SourceTable)!;
        Assert.Equal(ReadHold.None, read.Hold);
        Assert.Equal(ReadIntent.Changes, read.Intent);
        Assert.Equal("999", read.Watermark);
    }

    /// <summary><c>CompletedWithFailures</c> does not flip the intent, clear the hold, or promote the
    /// pending position — the mapping stays <c>Loading</c> for an operator to retry.</summary>
    [Fact]
    public void CompleteRun_WhenTheBatchCompletesWithFailures_DoesNotPromote()
    {
        const string batchId = "batch-2";
        _bulkLoadBatches.CreateBatch(batchId, TaskName, MappingName, segmentCount: 2, estimatedRows: null, estimateCaveat: null);
        var runId1 = _workQueue.Enqueue(
            TaskName, RunKind.BulkLoad, MappingName, "seg-1", segmentJson: null, kinds: null, bulkLoadBatchId: batchId);
        var runId2 = _workQueue.Enqueue(
            TaskName, RunKind.BulkLoad, MappingName, "seg-2", segmentJson: null, kinds: null, bulkLoadBatchId: batchId);
        _watermarks.SetPendingLoad(TaskName, MappingName, SourceTable, "999", null, batchId);

        _state.CompleteRun(runId1, RunStatus.Succeeded, 10, 10, errorSummary: null);
        _state.CompleteRun(runId2, RunStatus.Failed, 0, 0, errorSummary: "boom");

        var read = _watermarks.GetReadState(TaskName, MappingName, SourceTable)!;
        Assert.Equal(ReadHold.Loading, read.Hold);
        Assert.Null(read.Watermark);
    }

    /// <summary>A batch still running (only one of two segments done) must not be mistaken for
    /// complete — the rollup, not a single segment's own outcome, decides.</summary>
    [Fact]
    public void CompleteRun_WhileOtherSegmentsAreStillOutstanding_DoesNotPromote()
    {
        const string batchId = "batch-3";
        _bulkLoadBatches.CreateBatch(batchId, TaskName, MappingName, segmentCount: 2, estimatedRows: null, estimateCaveat: null);
        var runId1 = _workQueue.Enqueue(
            TaskName, RunKind.BulkLoad, MappingName, "seg-1", segmentJson: null, kinds: null, bulkLoadBatchId: batchId);
        _workQueue.Enqueue(
            TaskName, RunKind.BulkLoad, MappingName, "seg-2", segmentJson: null, kinds: null, bulkLoadBatchId: batchId);
        _watermarks.SetPendingLoad(TaskName, MappingName, SourceTable, "999", null, batchId);

        _state.CompleteRun(runId1, RunStatus.Succeeded, 10, 10, errorSummary: null);

        var read = _watermarks.GetReadState(TaskName, MappingName, SourceTable)!;
        Assert.Equal(ReadHold.Loading, read.Hold);
        Assert.Null(read.Watermark);
    }

    /// <summary>An ordinary Primary pass's own completion never touches an unrelated pending load —
    /// the RunKind.BulkLoad guard is what keeps the two independent.</summary>
    [Fact]
    public void CompleteRun_ForAnOrdinaryPrimaryRun_NeverTouchesAPendingLoad()
    {
        const string batchId = "batch-4";
        _bulkLoadBatches.CreateBatch(batchId, TaskName, MappingName, segmentCount: 1, estimatedRows: null, estimateCaveat: null);
        _watermarks.SetPendingLoad(TaskName, MappingName, SourceTable, "999", null, batchId);

        var primaryRunId = _workQueue.Enqueue(TaskName, RunKind.Primary, MappingName);
        _state.CompleteRun(primaryRunId, RunStatus.Succeeded, 5, 5, errorSummary: null);

        var read = _watermarks.GetReadState(TaskName, MappingName, SourceTable)!;
        Assert.Equal(ReadHold.Loading, read.Hold);
        Assert.Null(read.Watermark);
    }

    /// <summary>The ordinary case: nothing else in flight, so the enqueue wins and the pending load is
    /// recorded — <see cref="LocalRunnerState.RequestInitialLoad"/>'s own reordering (phase 143) doesn't
    /// change this outcome, only what happens when it loses (below).</summary>
    [Fact]
    public void RequestInitialLoad_WhenNothingElseIsInFlight_SetsThePendingLoad()
    {
        _state.RequestInitialLoad(TaskName, MappingName, SourceTable, "999", null);

        var read = _watermarks.GetReadState(TaskName, MappingName, SourceTable)!;
        Assert.Equal(ReadHold.Loading, read.Hold);
        Assert.Single(_taskRuns.GetRunHistory(TaskName, RunKind.BulkLoad));
    }

    /// <summary>
    /// Phase 143's own fix, direct: a second request for the identical mapping+segment — modelling an
    /// operator's own concurrent reload racing this mapping's auto-trigger, from whichever side loses —
    /// throws rather than silently colliding, and leaves the winner's own already-durable pending-load
    /// state exactly as the winner left it. Before this phase, the losing call's own
    /// <c>SetPendingLoad</c> ran unconditionally and overwrote <c>PendingBulkLoadBatchId</c> with a batch
    /// nothing would ever complete — the strand this phase exists to make impossible.
    /// </summary>
    [Fact]
    public void RequestInitialLoad_WhenAnotherLoadForTheSameMappingIsAlreadyInFlight_ThrowsAndLeavesTheWinnerAlone()
    {
        _state.RequestInitialLoad(TaskName, MappingName, SourceTable, "999", null);

        Assert.Throws<WorkQueueCollisionException>(
            () => _state.RequestInitialLoad(TaskName, MappingName, SourceTable, "1234", null));

        var read = _watermarks.GetReadState(TaskName, MappingName, SourceTable)!;
        // Still Loading — the winner's own hold, untouched by the loser.
        Assert.Equal(ReadHold.Loading, read.Hold);
        // Still exactly one BulkLoad run for this mapping — the loser minted no durable work of its own.
        Assert.Single(_taskRuns.GetRunHistory(TaskName, RunKind.BulkLoad));
    }
}
