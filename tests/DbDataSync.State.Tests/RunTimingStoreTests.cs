namespace DbDataSync.State.Tests;

/// <summary>
/// The timing columns phase 59 added. Nullable throughout, and the distinction that matters is
/// "never measured" versus "measured as zero" — an aggregate over these has to be able to exclude
/// runs that were never traced rather than averaging their zeros in.
/// </summary>
public sealed class RunTimingStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-timing-tests-").FullName;
    private readonly TaskRunStore _store;
    private readonly WorkQueueStore _queue;

    public RunTimingStoreTests()
    {
        var database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new TaskRunStore(database);
        _queue = new WorkQueueStore(database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private Guid Begin()
    {
        var runId = _queue.Enqueue("crm-sync", RunKind.Primary, "orders");
        _store.BeginRun(runId, pid: 1);
        return runId;
    }

    [Fact]
    public void AnUntracedRun_HasNoTimingAtAll()
    {
        var runId = Begin();

        _store.CompleteRun(runId, RunStatus.Succeeded, 10, 10, errorSummary: null);

        // Null, not a record of nulls: "was this traced" should be answerable without interrogating
        // seven fields.
        Assert.Null(_store.GetRun(runId)!.Timing);
    }

    [Fact]
    public void ATracedRun_RoundTripsEveryColumn()
    {
        var runId = Begin();
        var timing = new RunTiming(
            ReaderKind: "MsSqlChangeTracking",
            ReaderTimeToFirstRowMs: 12,
            ReaderLifetimeMs: 340,
            StagingKind: "MsSqlStagingTable",
            StagingDurationMs: 355,
            WriterKind: "MsSqlMerge",
            WriterDurationMs: 88);

        _store.CompleteRun(runId, RunStatus.Succeeded, 10, 10, errorSummary: null, timing: timing);

        Assert.Equal(timing, _store.GetRun(runId)!.Timing);
    }

    /// <summary>
    /// The Kinds are stored beside the numbers because a unit of work may override the replication's
    /// configured pipeline — a bulk load reloads through a different reader — so "which reader produced
    /// this number" cannot be recovered from config afterwards.
    /// </summary>
    [Fact]
    public void TheKindsAreRecordedWithTheTimings()
    {
        var runId = Begin();

        _store.CompleteRun(runId, RunStatus.Succeeded, 10, 10, null,
            timing: new RunTiming(ReaderKind: "MsSqlBatchReload", WriterKind: "MsSqlMergeReconcile"));

        var timing = _store.GetRun(runId)!.Timing!;
        Assert.Equal("MsSqlBatchReload", timing.ReaderKind);
        Assert.Equal("MsSqlMergeReconcile", timing.WriterKind);
    }

    [Fact]
    public void ZeroIsDistinguishableFromNotMeasured()
    {
        var runId = Begin();

        _store.CompleteRun(runId, RunStatus.Succeeded, 0, 0, null,
            timing: new RunTiming(ReaderKind: "Watermark", ReaderLifetimeMs: 0, WriterDurationMs: 0));

        var timing = _store.GetRun(runId)!.Timing!;
        Assert.Equal(0, timing.ReaderLifetimeMs);
        Assert.Null(timing.ReaderTimeToFirstRowMs);
        Assert.Equal(0, timing.WriterDurationMs);
    }

    [Fact]
    public void TimingSurvivesInRunHistory()
    {
        var runId = Begin();
        _store.CompleteRun(runId, RunStatus.Succeeded, 1, 1, null,
            timing: new RunTiming(ReaderKind: "Watermark", ReaderLifetimeMs: 5));

        var history = _store.GetRunHistory("crm-sync");

        Assert.Equal("Watermark", history.Single(r => r.RunId == runId).Timing!.ReaderKind);
    }
}
