namespace DbDataSync.State.Tests;

/// <summary>
/// The batch roll-up a bulk load's Monitoring card reads: <c>BulkLoadBatches</c> holds the planned
/// segment count and one whole-table estimate, and the per-segment progress is summed out of
/// <c>TaskRuns</c>.
/// </summary>
public sealed class BulkLoadBatchStoreTests : IDisposable
{
    private const string Task = "sales";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-bulk-load-").FullName;
    private readonly StateDatabase _database;
    private readonly BulkLoadBatchStore _store;

    public BulkLoadBatchStoreTests()
    {
        _database = new StateDatabase(Path.Combine(_root, "state.db"));
        _store = new BulkLoadBatchStore(_database);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void AddSegmentRun(string batchId, RunStatus status, long rowsRead = 0, long rowsWritten = 0)
    {
        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection, """
            INSERT INTO TaskRuns
                (RunId, TaskName, Status, RunKind, MappingName, EnqueuedAtUtc, StartedAtUtc, EndedAtUtc,
                 RowsRead, RowsWritten, BulkLoadBatchId)
            VALUES ($runId, $task, $status, 'BulkLoad', 'orders', $enqueued, $started, $ended,
                    $read, $written, $batchId);
            """);
        var started = status is RunStatus.Queued ? (object)DBNull.Value : DateTimeOffset.UtcNow.ToString("O");
        var ended = status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled
            ? (object)DateTimeOffset.UtcNow.ToString("O")
            : DBNull.Value;
        cmd.Bind(_database, "runId", Guid.NewGuid().ToString());
        cmd.Bind(_database, "task", Task);
        cmd.Bind(_database, "status", status.ToString());
        cmd.Bind(_database, "enqueued", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Bind(_database, "started", started);
        cmd.Bind(_database, "ended", ended);
        cmd.Bind(_database, "read", rowsRead);
        cmd.Bind(_database, "written", rowsWritten);
        cmd.Bind(_database, "batchId", batchId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ARunningBulkLoad_RollsUpRowsAndSegmentCounts()
    {
        _store.CreateBatch("b1", Task, "orders", segmentCount: 4, estimatedRows: 100_000, estimateCaveat: null);
        AddSegmentRun("b1", RunStatus.Succeeded, rowsRead: 25_000, rowsWritten: 24_000);
        AddSegmentRun("b1", RunStatus.Succeeded, rowsRead: 25_000, rowsWritten: 25_000);
        AddSegmentRun("b1", RunStatus.Running);
        AddSegmentRun("b1", RunStatus.Queued);

        var batch = Assert.Single(_store.GetRecentBulkLoads(Task, 5));

        Assert.Equal("orders", batch.MappingName);
        Assert.Equal(4, batch.SegmentCount);
        Assert.Equal(2, batch.SegmentsSucceeded);
        Assert.Equal(0, batch.SegmentsFailed);
        Assert.Equal(1, batch.SegmentsRunning);
        Assert.Equal(49_000, batch.RowsCopied);
        Assert.Equal(50_000, batch.RowsRead);
        Assert.Equal(100_000, batch.EstimatedRows);
        Assert.Null(batch.EstimateCaveat);
        Assert.Equal(BulkLoadState.Running, batch.State);
    }

    [Fact]
    public void EverySegmentSucceeded_IsCompleted()
    {
        _store.CreateBatch("b1", Task, "orders", 2, estimatedRows: null, estimateCaveat: null);
        AddSegmentRun("b1", RunStatus.Succeeded, rowsWritten: 10);
        AddSegmentRun("b1", RunStatus.Succeeded, rowsWritten: 20);

        var batch = Assert.Single(_store.GetRecentBulkLoads(Task, 5));

        Assert.Equal(BulkLoadState.Completed, batch.State);
        Assert.Equal(30, batch.RowsCopied);
        Assert.Null(batch.EstimatedRows);
    }

    [Fact]
    public void AFailedSegmentAfterTheRestFinish_IsCompletedWithFailures()
    {
        _store.CreateBatch("b1", Task, "orders", 3, estimatedRows: 1, estimateCaveat: "ignores row filter");
        AddSegmentRun("b1", RunStatus.Succeeded);
        AddSegmentRun("b1", RunStatus.Succeeded);
        AddSegmentRun("b1", RunStatus.Failed);

        var batch = Assert.Single(_store.GetRecentBulkLoads(Task, 5));

        Assert.Equal(BulkLoadState.CompletedWithFailures, batch.State);
        Assert.Equal(1, batch.SegmentsFailed);
        Assert.Equal("ignores row filter", batch.EstimateCaveat);
    }

    /// <summary>
    /// SegmentCount comes from the batch row, not <c>COUNT</c> of the runs — a segment whose equivalent
    /// was already in flight when the bulk load was queued has no run of its own, and the card should
    /// still say "1 of 3", not "1 of 1".
    /// </summary>
    [Fact]
    public void SegmentCount_IsThePlannedCount_NotTheRunCount()
    {
        _store.CreateBatch("b1", Task, "orders", segmentCount: 3, estimatedRows: null, estimateCaveat: null);
        AddSegmentRun("b1", RunStatus.Succeeded);

        var batch = Assert.Single(_store.GetRecentBulkLoads(Task, 5));

        Assert.Equal(3, batch.SegmentCount);
        Assert.Equal(1, batch.SegmentsSucceeded);
        Assert.Equal(BulkLoadState.Running, batch.State);
    }

    [Fact]
    public void GetRecentBulkLoads_IsNewestFirst_AndHonoursTheLimit()
    {
        _store.CreateBatch("old", Task, "orders", 1, null, null);
        Thread.Sleep(5);
        _store.CreateBatch("mid", Task, "orders", 1, null, null);
        Thread.Sleep(5);
        _store.CreateBatch("new", Task, "orders", 1, null, null);

        var two = _store.GetRecentBulkLoads(Task, 2);

        Assert.Equal(["new", "mid"], two.Select(b => b.BatchId));
    }

    [Fact]
    public void GetRecentBulkLoads_IsScopedToTheReplication()
    {
        _store.CreateBatch("mine", Task, "orders", 1, null, null);
        _store.CreateBatch("theirs", "other-replication", "orders", 1, null, null);

        var batch = Assert.Single(_store.GetRecentBulkLoads(Task, 5));

        Assert.Equal("mine", batch.BatchId);
    }

    /// <summary>The enqueue path actually stamps the batch id onto the TaskRuns row it writes.</summary>
    [Fact]
    public void Enqueue_WithABatchId_MakesTheRunVisibleToTheRollup()
    {
        var queue = new WorkQueueStore(_database);
        _store.CreateBatch("b1", Task, "orders", segmentCount: 1, estimatedRows: 500, estimateCaveat: null);

        queue.Enqueue(Task, RunKind.BulkLoad, "orders", "seg-1", segmentJson: null, kinds: null, bulkLoadBatchId: "b1");

        var batch = Assert.Single(_store.GetRecentBulkLoads(Task, 5));
        Assert.Equal(1, batch.SegmentCount);
        Assert.Equal(0, batch.SegmentsSucceeded);
        Assert.Equal(BulkLoadState.Running, batch.State);
    }

    // ---- Phase 139: GetHistory's keyset pagination -------------------------------------------------

    /// <summary>
    /// Newest first, and a second page picks up exactly where the first left off — no batch duplicated
    /// or skipped across the boundary. Three batches, a page size of two.
    /// </summary>
    [Fact]
    public void GetHistory_PagesNewestFirst_AcrossAKeysetBoundary()
    {
        _store.CreateBatch("old", Task, "orders", 1, null, null);
        Thread.Sleep(5);
        _store.CreateBatch("mid", Task, "orders", 1, null, null);
        Thread.Sleep(5);
        _store.CreateBatch("new", Task, "orders", 1, null, null);

        var page1 = _store.GetHistory(Task, limit: 2);
        Assert.Equal(["new", "mid"], page1.Batches.Select(b => b.BatchId));
        Assert.NotNull(page1.NextCursor);

        var page2 = _store.GetHistory(Task, cursor: page1.NextCursor, limit: 2);
        Assert.Equal(["old"], page2.Batches.Select(b => b.BatchId));
        Assert.Null(page2.NextCursor);
    }

    /// <summary>The `mappingName` filter scopes to one mapping's own batches, same replication.</summary>
    [Fact]
    public void GetHistory_MappingNameFilter_ScopesToOneMapping()
    {
        _store.CreateBatch("b1", Task, "orders", 1, null, null);
        _store.CreateBatch("b2", Task, "customers", 1, null, null);

        var page = _store.GetHistory(Task, mappingName: "customers", limit: 20);

        Assert.Equal(["b2"], page.Batches.Select(b => b.BatchId));
    }

    /// <summary>
    /// A batch whose segments are all still Queued has no `TaskRuns` row yet — `StartedAtUtc` is null —
    /// but it still appears, ordered by `CreatedAtUtc` rather than dropped for lacking a start time.
    /// </summary>
    [Fact]
    public void GetHistory_ABatchWithNoStartedSegmentsYet_StillAppears_OrderedByCreatedAt()
    {
        _store.CreateBatch("b1", Task, "orders", segmentCount: 2, estimatedRows: null, estimateCaveat: null);

        var page = _store.GetHistory(Task, limit: 20);

        var batch = Assert.Single(page.Batches);
        Assert.Equal("b1", batch.BatchId);
        Assert.Null(batch.StartedAtUtc);
        Assert.Equal(BulkLoadState.Running, batch.State);
    }

    /// <summary>Scoped to the replication, same as `GetRecentBulkLoads`.</summary>
    [Fact]
    public void GetHistory_IsScopedToTheReplication()
    {
        _store.CreateBatch("mine", Task, "orders", 1, null, null);
        _store.CreateBatch("theirs", "other-replication", "orders", 1, null, null);

        var page = _store.GetHistory(Task, limit: 20);

        Assert.Equal(["mine"], page.Batches.Select(b => b.BatchId));
    }
}
