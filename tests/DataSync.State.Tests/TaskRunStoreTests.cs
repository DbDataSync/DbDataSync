namespace DataSync.State.Tests;

public sealed class TaskRunStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-state-tests-").FullName;
    private readonly TaskRunStore _store;
    private readonly WorkQueueStore _queue;

    public TaskRunStoreTests()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new TaskRunStore(database);
        _queue = new WorkQueueStore(database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // A run's row now always originates from WorkQueueStore.Enqueue (Status=Queued) — there is no
    // longer an INSERT-based "start a run" method on TaskRunStore itself, only BeginRun (an UPDATE
    // transitioning an existing Queued row to Running).
    private Guid QueueAndBegin(string taskName, string mappingName, int? pid = null, RunKind runKind = RunKind.Primary)
    {
        var runId = _queue.Enqueue(taskName, runKind, mappingName);
        _store.BeginRun(runId, pid);
        return runId;
    }

    [Fact]
    public void Enqueue_ThenBeginRun_ThenCompleteRun_RoundTrips()
    {
        var runId = QueueAndBegin("crm-sync", "orders", pid: 4242);

        var started = _store.GetRun(runId);
        Assert.NotNull(started);
        Assert.Equal(RunStatus.Running, started!.Status);
        Assert.Equal(RunKind.Primary, started.RunKind);
        Assert.Equal("orders", started.MappingName);
        Assert.Equal(4242, started.Pid);
        Assert.Null(started.EndedAtUtc);

        _store.CompleteRun(runId, RunStatus.Succeeded, rowsRead: 100, rowsWritten: 98, errorSummary: null);

        var completed = _store.GetRun(runId);
        Assert.NotNull(completed);
        Assert.Equal(RunStatus.Succeeded, completed!.Status);
        Assert.Equal(100, completed.RowsRead);
        Assert.Equal(98, completed.RowsWritten);
        Assert.NotNull(completed.EndedAtUtc);
        Assert.Null(completed.ErrorSummary);
    }

    [Fact]
    public void Enqueue_WritesAQueuedRow_BeforeBeginRunIsCalled()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        var run = _store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(RunStatus.Queued, run!.Status);
        Assert.Null(run.Pid);
    }

    [Fact]
    public void CompleteRun_WithError_RecordsErrorSummary()
    {
        var runId = QueueAndBegin("crm-sync", "orders", pid: null);
        _store.CompleteRun(runId, RunStatus.Failed, rowsRead: 5, rowsWritten: 0, errorSummary: "connection timed out");

        var run = _store.GetRun(runId);
        Assert.Equal(RunStatus.Failed, run!.Status);
        Assert.Equal("connection timed out", run.ErrorSummary);
    }

    [Fact]
    public void GetRunHistory_ReturnsNewestFirst()
    {
        var first = QueueAndBegin("crm-sync", "orders", pid: 1);
        Thread.Sleep(5); // ensure StartedAtUtc ordering is unambiguous
        var second = QueueAndBegin("crm-sync", "customers", pid: 2);

        var history = _store.GetRunHistory("crm-sync");

        Assert.Equal(2, history.Count);
        Assert.Equal(second, history[0].RunId);
        Assert.Equal(first, history[1].RunId);
    }

    [Fact]
    public void GetRunHistory_FilteredByRunKind_ExcludesTheOtherKind()
    {
        var primary = QueueAndBegin("crm-sync", "orders", runKind: RunKind.Primary);
        var backfill = QueueAndBegin("crm-sync", "orders", runKind: RunKind.Backfill);

        var primaryOnly = _store.GetRunHistory("crm-sync", RunKind.Primary);
        Assert.Single(primaryOnly);
        Assert.Equal(primary, primaryOnly[0].RunId);

        var backfillOnly = _store.GetRunHistory("crm-sync", RunKind.Backfill);
        Assert.Single(backfillOnly);
        Assert.Equal(backfill, backfillOnly[0].RunId);

        Assert.Equal(2, _store.GetRunHistory("crm-sync").Count);
    }

    [Fact]
    public void GetMappingRunHistory_OnlyReturnsThatMapping()
    {
        QueueAndBegin("crm-sync", "orders");
        var customersRun = QueueAndBegin("crm-sync", "customers");

        var history = _store.GetMappingRunHistory("crm-sync", RunKind.Primary, "customers");

        Assert.Single(history);
        Assert.Equal(customersRun, history[0].RunId);
    }

    [Fact]
    public void GetLastPrimaryStartByMapping_ReturnsOneEntryPerMapping()
    {
        QueueAndBegin("crm-sync", "orders");
        Thread.Sleep(5);
        var latestOrders = QueueAndBegin("crm-sync", "orders");
        QueueAndBegin("crm-sync", "customers");
        QueueAndBegin("crm-sync", "products", runKind: RunKind.Backfill); // Backfill excluded

        var lastStarts = _store.GetLastPrimaryStartByMapping("crm-sync");

        Assert.Equal(2, lastStarts.Count);
        Assert.True(lastStarts.ContainsKey("orders"));
        Assert.True(lastStarts.ContainsKey("customers"));
        Assert.False(lastStarts.ContainsKey("products"));
        Assert.Equal(_store.GetRun(latestOrders)!.StartedAtUtc, lastStarts["orders"]);
    }

    [Fact]
    public void GetRunningRuns_OnlyReturnsRunsStillInProgress()
    {
        var running = QueueAndBegin("crm-sync", "orders", pid: 1);
        var finished = QueueAndBegin("crm-sync", "customers", pid: 2);
        _store.CompleteRun(finished, RunStatus.Succeeded, 1, 1, null);

        var stillRunning = _store.GetRunningRuns();

        Assert.Single(stillRunning);
        Assert.Equal(running, stillRunning[0].RunId);
    }

    [Fact]
    public void GetActiveRuns_IncludesQueuedAndRunning_ExcludesTerminalStatuses()
    {
        var queued = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var running = QueueAndBegin("crm-sync", "customers");
        var succeeded = QueueAndBegin("crm-sync", "products");
        _store.CompleteRun(succeeded, RunStatus.Succeeded, 1, 1, null);

        var active = _store.GetActiveRuns().Select(r => r.RunId).ToHashSet();

        Assert.Contains(queued, active);
        Assert.Contains(running, active);
        Assert.DoesNotContain(succeeded, active);
    }

    [Fact]
    public async Task ParallelRunWrites_AllRunsPersistedUnderContention()
    {
        const int count = 50;
        var mappingNames = Enumerable.Range(0, count).Select(i => $"mapping-{i}").ToArray();

        await Task.WhenAll(mappingNames.Select(mappingName => Task.Run(() =>
        {
            var runId = QueueAndBegin("crm-sync", mappingName, pid: 1234);
            _store.CompleteRun(runId, RunStatus.Succeeded, rowsRead: 10, rowsWritten: 10, errorSummary: null);
        })));

        var history = _store.GetRunHistory("crm-sync", limit: count + 10);
        Assert.Equal(count, history.Count);
        Assert.All(history, r => Assert.Equal(RunStatus.Succeeded, r.Status));
    }
}
