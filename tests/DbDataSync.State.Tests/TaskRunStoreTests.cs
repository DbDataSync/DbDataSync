namespace DbDataSync.State.Tests;

public sealed class TaskRunStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-state-tests-").FullName;
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

        // The enqueue is the only moment that has happened, so it is the only timestamp written
        // (phase 73). Nobody has claimed it and nothing has started it, and both columns say so —
        // the "absent means it never happened" shape the rest of the optional TaskRuns columns use.
        // A run cancelled while queued keeps both null forever, and correctly reports no times.
        Assert.NotNull(run.EnqueuedAtUtc);
        Assert.Null(run.ClaimedAtUtc);
        Assert.Null(run.StartedAtUtc);
    }

    /// <summary>
    /// The distinction phase 73 exists for: the enqueue writes EnqueuedAtUtc, and BeginRun — the call a
    /// worker makes when it is about to execute — writes StartedAtUtc. The sleep makes the gap real
    /// rather than a coincidence of clock resolution, so this fails if BeginRun stops writing the
    /// column, or writes the enqueue time into it, or (as phase 72 did) writes the wrong column.
    /// </summary>
    [Fact]
    public void BeginRun_RecordsWhenTheRunActuallyStarted_NotWhenItWasQueued()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var queued = _store.GetRun(runId)!;
        Thread.Sleep(20);
        _store.BeginRun(runId, pid: 7);

        var started = _store.GetRun(runId)!;

        Assert.NotNull(started.StartedAtUtc);
        Assert.Equal(queued.EnqueuedAtUtc, started.EnqueuedAtUtc); // the enqueue time is left alone
        Assert.True(
            started.StartedAtUtc!.Value - started.EnqueuedAtUtc!.Value >= TimeSpan.FromMilliseconds(15),
            "the queue time should be visible as the gap between the two timestamps");
    }

    /// <summary>
    /// The claim is written when the item is claimed, by TryClaimNext — not by BeginRun, which happens
    /// later and now writes StartedAtUtc instead.
    /// <para>
    /// The sleeps put a real gap on either side of the claim, so the assertion is that the claim landed
    /// strictly between the enqueue and the start rather than coinciding with either. Both coincidences
    /// are exactly the bug this phase fixes, in the two directions it could be wrong.
    /// </para>
    /// </summary>
    [Fact]
    public void TryClaimNext_RecordsTheClaimOnTheRun_BetweenTheEnqueueAndTheStart()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        Thread.Sleep(20);

        var item = _queue.TryClaimNext("crm-sync", "worker-1");
        Assert.NotNull(item);
        Assert.Equal(runId, item!.RunId);

        var claimed = _store.GetRun(runId)!;
        Assert.NotNull(claimed.ClaimedAtUtc);
        Assert.Null(claimed.StartedAtUtc); // claimed is not started
        Assert.True(
            claimed.ClaimedAtUtc!.Value - claimed.EnqueuedAtUtc!.Value >= TimeSpan.FromMilliseconds(15),
            "the claim happens after the enqueue, by however long the item waited");

        Thread.Sleep(20);
        _store.BeginRun(runId, pid: 7);

        var started = _store.GetRun(runId)!;
        Assert.Equal(claimed.ClaimedAtUtc, started.ClaimedAtUtc); // BeginRun does not touch it
        Assert.True(
            started.StartedAtUtc!.Value - started.ClaimedAtUtc!.Value >= TimeSpan.FromMilliseconds(15),
            "the start happens after the claim, which is what makes them different columns");
    }

    /// <summary>
    /// The claim on <c>WorkQueue</c> and the claim on <c>TaskRuns</c> are one transaction, so an item
    /// that lost the race gets neither — a run must never carry a claim time from a worker that did not
    /// claim it.
    /// <para>
    /// Asserted by racing two workers for one item rather than by inspecting the SQL: the rollback path
    /// is the one where the two writes could come apart, and it is reachable from the public API.
    /// </para>
    /// </summary>
    [Fact]
    public void TryClaimNext_LosingTheRace_LeavesNoClaimTimeOnTheRun()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        Assert.NotNull(_queue.TryClaimNext("crm-sync", "worker-1"));
        var stamped = _store.GetRun(runId)!.ClaimedAtUtc;
        Assert.NotNull(stamped);

        // Nothing left Pending, so the second worker claims nothing — and must not restamp the run.
        Assert.Null(_queue.TryClaimNext("crm-sync", "worker-2"));
        Assert.Equal(stamped, _store.GetRun(runId)!.ClaimedAtUtc);
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
        Thread.Sleep(5); // ensure EnqueuedAtUtc ordering is unambiguous
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
    public void GetLastPrimaryEnqueueByMapping_ReturnsOneEntryPerMapping()
    {
        QueueAndBegin("crm-sync", "orders");
        Thread.Sleep(5);
        var latestOrders = QueueAndBegin("crm-sync", "orders");
        QueueAndBegin("crm-sync", "customers");
        QueueAndBegin("crm-sync", "products", runKind: RunKind.Backfill); // Backfill excluded

        var lastEnqueues = _store.GetLastPrimaryEnqueueByMapping("crm-sync");

        Assert.Equal(2, lastEnqueues.Count);
        Assert.True(lastEnqueues.ContainsKey("orders"));
        Assert.True(lastEnqueues.ContainsKey("customers"));
        Assert.False(lastEnqueues.ContainsKey("products"));
        Assert.Equal(_store.GetRun(latestOrders)!.EnqueuedAtUtc, lastEnqueues["orders"]);
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
