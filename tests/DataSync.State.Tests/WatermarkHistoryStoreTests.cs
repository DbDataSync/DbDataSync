namespace DataSync.State.Tests;

/// <summary>
/// The watermark history phase 71 added to <c>TaskRuns</c>. <c>ChangeWatermarks</c> holds one current
/// value per mapping and is overwritten every pass; these two columns are the only record of what that
/// value used to be, and the reason they live on <c>TaskRuns</c> rather than in a table of their own is
/// that phase 60's pruning then covers them for free — which the last test here is about.
/// </summary>
public sealed class WatermarkHistoryStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-watermark-history-").FullName;
    private readonly StateDatabase _database;
    private readonly TaskRunStore _store;
    private readonly WorkQueueStore _queue;

    public WatermarkHistoryStoreTests()
    {
        _database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new TaskRunStore(_database);
        _queue = new WorkQueueStore(_database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>
    /// Each run gets a distinct segment label: Enqueue deduplicates on the in-flight unique index, so
    /// enqueuing the same (task, kind, mapping, segment) twice hands back the first run rather than
    /// making a second — correct for the queue, and it would silently give these tests one run where
    /// they asked for three.
    /// </summary>
    private Guid Begin(RunKind kind = RunKind.Primary, string mapping = "orders")
    {
        var runId = _queue.Enqueue("crm-sync", kind, mapping, segmentLabel: Guid.NewGuid().ToString("N"));
        _store.BeginRun(runId, pid: 1);
        return runId;
    }

    /// <summary>History is ordered by EnqueuedAtUtc, which the queue sets to "now" — so runs made in
    /// the same instant need separating before the order means anything.</summary>
    private void Backdate(Guid runId, TimeSpan age)
    {
        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(
            connection, "UPDATE TaskRuns SET EnqueuedAtUtc = $enqueued WHERE RunId = $runId;");
        cmd.Bind(_database, "enqueued", (DateTimeOffset.UtcNow - age).ToString("O"));
        cmd.Bind(_database, "runId", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ASuccessfulPass_RecordsWhereItStartedAndWhereItLeftOff()
    {
        var runId = Begin();

        _store.CompleteRun(
            runId, RunStatus.Succeeded, 10, 10, errorSummary: null,
            previousWatermark: "1200", newWatermark: "1450");

        var run = _store.GetRun(runId)!;
        Assert.Equal("1200", run.PreviousWatermark);
        Assert.Equal("1450", run.NewWatermark);
    }

    /// <summary>
    /// The first pass a mapping ever runs has no previous position, which is a real and different
    /// answer from "it did not move" — so the previous column is null while the new one is not.
    /// </summary>
    [Fact]
    public void AFirstPass_RecordsANullPrevious_AndAConcreteNew()
    {
        var runId = Begin();

        _store.CompleteRun(
            runId, RunStatus.Succeeded, 10, 10, errorSummary: null,
            previousWatermark: null, newWatermark: "1450");

        var run = _store.GetRun(runId)!;
        Assert.Null(run.PreviousWatermark);
        Assert.Equal("1450", run.NewWatermark);
    }

    /// <summary>A run kind that produces no position at all leaves both columns alone.</summary>
    [Theory]
    [InlineData(RunKind.Backfill)]
    [InlineData(RunKind.Verification)]
    public void ARunKindWithNoWatermark_LeavesBothColumnsNull(RunKind kind)
    {
        var runId = Begin(kind);

        _store.CompleteRun(runId, RunStatus.Succeeded, 10, 10, errorSummary: null);

        var run = _store.GetRun(runId)!;
        Assert.Null(run.PreviousWatermark);
        Assert.Null(run.NewWatermark);
    }

    /// <summary>
    /// A failure records no advance even when the pass had computed one, because it never committed
    /// one — the watermark advances only after the write does. Enforced at the call site in
    /// <c>RunExecutor</c>, which passes null from every failure path; asserted here as the store's own
    /// default so the column cannot pick up a value nobody asked it to write.
    /// </summary>
    [Fact]
    public void AFailedRun_RecordsNoWatermarkAdvance()
    {
        var runId = Begin();

        _store.CompleteRun(runId, RunStatus.Failed, 0, 0, "the target went away");

        var run = _store.GetRun(runId)!;
        Assert.Null(run.PreviousWatermark);
        Assert.Null(run.NewWatermark);
    }

    /// <summary>
    /// The point of the whole design: history, where <c>ChangeWatermarks</c> has only a current value.
    /// </summary>
    [Fact]
    public void RunHistoryInOrder_ShowsTheWatermarkAtEachPointInTime()
    {
        var passes = new[] { ((string?)null, "100"), ("100", "250"), ("250", "600") };
        for (var i = 0; i < passes.Length; i++)
        {
            var (previous, next) = passes[i];
            var runId = Begin();
            _store.CompleteRun(
                runId, RunStatus.Succeeded, 1, 1, errorSummary: null,
                previousWatermark: previous, newWatermark: next);
            Backdate(runId, TimeSpan.FromMinutes(passes.Length - i));
        }

        // Newest first, as the history endpoint returns it.
        var history = _store.GetRunHistory("crm-sync", limit: 10);

        Assert.Equal(["600", "250", "100"], history.Select(r => r.NewWatermark));
        Assert.Equal([(string?)"250", "100", null], history.Select(r => r.PreviousWatermark));
    }

    /// <summary>
    /// Why these columns are on <c>TaskRuns</c> at all. Phase 60's prune deletes whole rows, so it
    /// purges the watermark history with them and <c>RunPruningService</c> needs no second mechanism —
    /// asserted rather than assumed, since "no extra pruning logic" is the claim the design rests on.
    /// </summary>
    [Fact]
    public void PruningARunRemovesItsWatermarkHistoryWithIt_WithNoSeparateStep()
    {
        Guid Completed(string watermark, int minutesAgo)
        {
            var runId = Begin();
            _store.CompleteRun(
                runId, RunStatus.Succeeded, 1, 1, errorSummary: null,
                previousWatermark: "0", newWatermark: watermark);
            Backdate(runId, TimeSpan.FromMinutes(minutesAgo));
            return runId;
        }

        var oldest = Completed("100", minutesAgo: 30);
        Completed("200", minutesAgo: 20);
        var newest = Completed("300", minutesAgo: 10);

        Assert.Equal(1, _store.PruneRuns(maxAge: null, maxPerMapping: 2));

        Assert.Null(_store.GetRun(oldest));
        Assert.Equal("300", _store.GetRun(newest)!.NewWatermark);

        // Nothing of the pruned run is left behind in the columns — the row went, so they went.
        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(
            connection, "SELECT COUNT(*) FROM TaskRuns WHERE NewWatermark = $watermark;");
        cmd.Bind(_database, "watermark", "100");
        Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
    }
}
