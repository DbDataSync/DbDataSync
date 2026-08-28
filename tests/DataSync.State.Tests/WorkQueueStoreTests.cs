namespace DataSync.State.Tests;

public sealed class WorkQueueStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-state-tests-").FullName;
    private readonly WorkQueueStore _queue;
    private readonly TaskRunStore _taskRunStore;

    public WorkQueueStoreTests()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _queue = new WorkQueueStore(database);
        _taskRunStore = new TaskRunStore(database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Enqueue_WritesAQueuedTaskRunsRow_ImmediatelyVisibleInHistory()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        var run = _taskRunStore.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(RunStatus.Queued, run!.Status);
        Assert.Equal(RunKind.Primary, run.RunKind);
        Assert.Equal("orders", run.MappingName);
    }

    [Fact]
    public void Enqueue_WhileAnEquivalentItemIsAlreadyPending_ReturnsTheExistingRunId_NoNewRow()
    {
        var first = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var second = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        Assert.Equal(first, second);
        Assert.Single(_taskRunStore.GetRunHistory("crm-sync"));
    }

    [Fact]
    public void Enqueue_DifferentSegmentLabels_AreIndependent()
    {
        var range1 = _queue.Enqueue("crm-sync", RunKind.Backfill, "orders", segmentLabel: "1-1000");
        var range2 = _queue.Enqueue("crm-sync", RunKind.Backfill, "orders", segmentLabel: "1001-2000");

        Assert.NotEqual(range1, range2);
        Assert.Equal(2, _taskRunStore.GetRunHistory("crm-sync", RunKind.Backfill).Count);
    }

    [Fact]
    public void Enqueue_AfterAPriorItemCompleted_CreatesANewItem_NotBlockedByTerminalHistory()
    {
        // Completing the TaskRuns row alone (without also moving the WorkQueue row to a terminal
        // status) doesn't free up the slot — mirrors what ProcessWorkItemAsync actually does: claim,
        // then MarkDone/MarkFailed together with CompleteRun, not CompleteRun alone.
        var first = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var claimed = _queue.TryClaimNext("crm-sync", "worker-1")!;
        _taskRunStore.CompleteRun(first, RunStatus.Succeeded, 1, 1, null);
        _queue.MarkDone(claimed.Id);

        var second = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryClaimNext_WhenNothingPending_ReturnsNull()
    {
        Assert.Null(_queue.TryClaimNext("crm-sync", "worker-1"));
    }

    [Fact]
    public void TryClaimNext_ClaimsAPendingItem_AndMarksItClaimed()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        var claimed = _queue.TryClaimNext("crm-sync", "worker-1");

        Assert.NotNull(claimed);
        Assert.Equal(runId, claimed!.RunId);
        Assert.Equal(WorkItemStatus.Claimed, claimed.Status);
    }

    [Fact]
    public void TryClaimNext_SkipsAMapping_WhoseOtherItemIsAlreadyClaimedOrRunning()
    {
        // Two segments of the same mapping's backfill queued at once — only one should be claimable
        // while the other is in flight, even though both are independently Pending rows.
        _queue.Enqueue("crm-sync", RunKind.Backfill, "orders", segmentLabel: "seg-1");
        _queue.Enqueue("crm-sync", RunKind.Backfill, "orders", segmentLabel: "seg-2");

        var firstClaim = _queue.TryClaimNext("crm-sync", "worker-1");
        var secondClaim = _queue.TryClaimNext("crm-sync", "worker-1");

        Assert.NotNull(firstClaim);
        Assert.Null(secondClaim); // seg-2 exists but its mapping already has seg-1 in flight
    }

    [Fact]
    public void TryClaimNext_DifferentMappings_BothClaimableConcurrently()
    {
        _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        _queue.Enqueue("crm-sync", RunKind.Primary, "customers");

        var first = _queue.TryClaimNext("crm-sync", "worker-1");
        var second = _queue.TryClaimNext("crm-sync", "worker-1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.MappingName, second!.MappingName);
    }

    [Fact]
    public void TryClaimNext_OnlyReturnsItemsForTheRequestedTaskName()
    {
        _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        Assert.Null(_queue.TryClaimNext("other-replication", "worker-1"));
    }

    [Fact]
    public async Task TryClaimNext_UnderConcurrentContention_NeverDoubleClaimsTheSameItem()
    {
        _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        var claims = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => _queue.TryClaimNext("crm-sync", $"worker-{i}"))));

        Assert.Equal(1, claims.Count(c => c is not null));
    }

    [Fact]
    public void MarkDone_TransitionsStatus()
    {
        _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var claimed = _queue.TryClaimNext("crm-sync", "worker-1")!;

        _queue.MarkDone(claimed.Id);

        // Done items no longer count as outstanding, and no longer block a fresh enqueue for the
        // same mapping.
        Assert.False(_queue.HasOutstandingWork("crm-sync"));
        var newRunId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        Assert.NotEqual(claimed.RunId, newRunId);
    }

    [Fact]
    public void ReleaseClaim_GivesTheItemBackToPending_ClaimableAgain()
    {
        _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var claimed = _queue.TryClaimNext("crm-sync", "worker-1")!;

        _queue.ReleaseClaim(claimed.Id);
        var reclaimed = _queue.TryClaimNext("crm-sync", "worker-2");

        Assert.NotNull(reclaimed);
        Assert.Equal(claimed.Id, reclaimed!.Id);
    }

    [Fact]
    public void TryCancelPending_OnAPendingItem_Succeeds()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        Assert.True(_queue.TryCancelPending(runId));
        Assert.False(_queue.HasOutstandingWork("crm-sync"));
    }

    [Fact]
    public void TryCancelPending_OnAnAlreadyClaimedItem_Fails()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        _queue.TryClaimNext("crm-sync", "worker-1");

        Assert.False(_queue.TryCancelPending(runId));
    }

    /// <summary>
    /// A backfill has to run a different pipeline from the replication it belongs to — a reload
    /// reader and usually a reconciling writer — so which Kinds to use is carried per work item and
    /// has to survive the round trip through the queue intact.
    /// </summary>
    [Fact]
    public void Enqueue_WithKindOverrides_MakesThemAvailableToWhicheverWorkerClaimsTheItem()
    {
        var kinds = new WorkItemKinds("SomeBatchReload", "SomeStaging", "SomeReconcilingWriter");

        _queue.Enqueue("crm-sync", RunKind.Backfill, "orders", "Region in (EU)", "{\"mode\":\"full\"}", kinds);

        var claimed = _queue.TryClaimNext("crm-sync", "worker-1")!;
        Assert.Equal(kinds, claimed.Kinds);
        Assert.Equal("Region in (EU)", claimed.SegmentLabel);
        Assert.Equal("{\"mode\":\"full\"}", claimed.SegmentJson);
    }

    /// <summary>A Primary pass overrides nothing — it runs the replication's own configured
    /// pipeline, which is what null means all the way down.</summary>
    [Fact]
    public void Enqueue_WithoutKindOverrides_ClaimsBackAsUseTheConfiguredPipeline()
    {
        _queue.Enqueue("crm-sync", RunKind.Primary, "orders");

        var claimed = _queue.TryClaimNext("crm-sync", "worker-1")!;
        Assert.Equal(WorkItemKinds.FromConfig, claimed.Kinds);
    }

    /// <summary>
    /// Two segments of the same mapping are distinct units of work, unlike two enqueues of the same
    /// segment — the in-flight uniqueness constraint is per segment, not per mapping, or a segmented
    /// backfill could never queue more than one of its own segments.
    /// </summary>
    [Fact]
    public void Enqueue_DifferentSegmentsOfOneMapping_AreSeparateItems()
    {
        var first = _queue.Enqueue("crm-sync", RunKind.Backfill, "orders", "Id [1, 100)", "{}");
        var second = _queue.Enqueue("crm-sync", RunKind.Backfill, "orders", "Id [100, 200)", "{}");
        var duplicate = _queue.Enqueue("crm-sync", RunKind.Backfill, "orders", "Id [1, 100)", "{}");

        Assert.NotEqual(first, second);
        Assert.Equal(first, duplicate);
        Assert.Equal(2, _taskRunStore.GetRunHistory("crm-sync").Count);
    }

    [Fact]
    public void HasOutstandingWork_ReflectsPendingClaimedAndRunning_NotTerminal()
    {
        Assert.False(_queue.HasOutstandingWork("crm-sync"));

        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        Assert.True(_queue.HasOutstandingWork("crm-sync"));

        var claimed = _queue.TryClaimNext("crm-sync", "worker-1")!;
        Assert.True(_queue.HasOutstandingWork("crm-sync"));

        _queue.MarkRunning(claimed.Id);
        Assert.True(_queue.HasOutstandingWork("crm-sync"));

        _queue.MarkDone(claimed.Id);
        Assert.False(_queue.HasOutstandingWork("crm-sync"));
    }

    /// <summary>
    /// The failure that used to be permanent. UX_WorkQueue_InFlight covers Claimed and Running, so an
    /// item whose worker died without releasing it made its mapping un-enqueueable forever — the
    /// replication stopped silently rather than failing. The API releases them when it finds the
    /// process gone.
    /// </summary>
    [Fact]
    public void ReleaseClaimsForTask_ReturnsAnInFlightItemToTheQueueSoTheMappingCanRunAgain()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var claimed = _queue.TryClaimNext("crm-sync", "worker-that-died")!;
        _queue.MarkRunning(claimed.Id);

        // While it is in flight nothing else can be queued or claimed for that mapping.
        Assert.Equal(runId, _queue.Enqueue("crm-sync", RunKind.Primary, "orders"));
        Assert.Null(_queue.TryClaimNext("crm-sync", "worker-2"));

        Assert.Equal(1, _queue.ReleaseClaimsForTask("crm-sync"));

        var reclaimed = _queue.TryClaimNext("crm-sync", "worker-2");
        Assert.NotNull(reclaimed);
        Assert.Equal(claimed.Id, reclaimed.Id);
    }

    [Fact]
    public void ReleaseClaimsForTask_LeavesAFinishedItemAlone()
    {
        _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var claimed = _queue.TryClaimNext("crm-sync", "worker-1")!;
        _queue.MarkDone(claimed.Id);

        Assert.Equal(0, _queue.ReleaseClaimsForTask("crm-sync"));
        Assert.False(_queue.HasOutstandingWork("crm-sync"));
    }

    /// <summary>A worker claims ahead of its consumers, so it can die holding an item it never
    /// started — which has no started run to be found by, and so has to be released by replication.</summary>
    [Fact]
    public void ReleaseClaimsForTask_AlsoReturnsAnItemThatWasClaimedButNeverStarted()
    {
        _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        var claimed = _queue.TryClaimNext("crm-sync", "worker-that-died")!;
        Assert.Equal(WorkItemStatus.Claimed, claimed.Status);

        Assert.Equal(1, _queue.ReleaseClaimsForTask("crm-sync"));
        Assert.Equal(claimed.Id, _queue.TryClaimNext("crm-sync", "worker-2")!.Id);
    }
}
