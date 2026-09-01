namespace DataSync.State.Tests;

/// <summary>
/// Aggregates over TaskRuns. Asserted on known distributions, because the point of a percentile is
/// that it is not the average and a test that cannot tell them apart proves nothing.
/// </summary>
public sealed class RunMetricsStoreTests : IDisposable
{
    private const string Task = "sales";

    private readonly string _root = Directory.CreateTempSubdirectory("datasync-metrics-").FullName;
    private readonly StateDatabase _database;
    private readonly RunMetricsStore _metrics;
    private readonly DateTimeOffset _now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    public RunMetricsStoreTests()
    {
        _database = new StateDatabase(Path.Combine(_root, "state.db"));
        _metrics = new RunMetricsStore(_database);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Writes a run directly: these are assertions about a query over rows, and going through the
    /// queue to produce them would test the queue.
    /// </summary>
    /// <param name="enqueuedAt">When the work was queued. What the window is selected on.</param>
    /// <param name="processing">
    /// How long the run itself took, from its start to its end.
    /// </param>
    /// <param name="queueTime">Enqueue to start — the wait, which no processing figure may include.</param>
    /// <param name="claimLatency">
    /// Enqueue to <em>claim</em>, which is a third point and not the same as the second (phase 73).
    /// <para>
    /// It exists so the three candidate formulas give three different answers. A run built with all
    /// three distinct has one processing time from <c>EndedAtUtc - StartedAtUtc</c>, a different one
    /// from <c>EndedAtUtc - ClaimedAtUtc</c> (what phase 72 computed) and a third from
    /// <c>EndedAtUtc - EnqueuedAtUtc</c> (what came before it) — so an assertion on the figure pins
    /// which pair of timestamps produced it, rather than passing for whichever of them happened to
    /// coincide. Defaults to <paramref name="queueTime"/>: claim-to-start is normally sub-millisecond,
    /// so a test that is not about that gap should not have to state one.
    /// </para>
    /// </param>
    /// <param name="claimed">False writes a run that was never claimed and so never started — a
    /// cancelled or still-queued row, and every row written before phase 72's column existed.</param>
    /// <param name="started">False writes a run that was claimed but never began. Also every row
    /// predating phase 73, whose StartedAtUtc is dropped rather than backfilled with a start that never
    /// happened.</param>
    private void AddRun(
        DateTimeOffset enqueuedAt, TimeSpan? processing = null, RunStatus status = RunStatus.Succeeded,
        long rowsRead = 0, long rowsWritten = 0, RunKind kind = RunKind.Primary, string taskName = Task,
        TimeSpan? queueTime = null, TimeSpan? claimLatency = null, bool claimed = true, bool started = true)
    {
        var startedAt = enqueuedAt + (queueTime ?? TimeSpan.Zero);
        var claimedAt = enqueuedAt + (claimLatency ?? queueTime ?? TimeSpan.Zero);
        started &= claimed;

        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection, """
            INSERT INTO TaskRuns (RunId, TaskName, Status, RunKind, MappingName, EnqueuedAtUtc, ClaimedAtUtc, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten)
            VALUES ($runId, $task, $status, $kind, 'orders', $enqueued, $claimed, $started, $ended, $read, $written);
            """);
        cmd.Bind(_database, "runId", Guid.NewGuid().ToString());
        cmd.Bind(_database, "task", taskName);
        cmd.Bind(_database, "status", status.ToString());
        cmd.Bind(_database, "kind", kind.ToString());
        cmd.Bind(_database, "enqueued", enqueuedAt.ToString("O"));
        cmd.Bind(_database, "claimed", claimed ? claimedAt.ToString("O") : DBNull.Value);
        cmd.Bind(_database, "started", started ? startedAt.ToString("O") : DBNull.Value);
        cmd.Bind(_database, "ended",
            // Written even for a run with no recorded start — that is exactly the pre-migration row
            // the null-guard exists for: it ended, and nothing says when it began.
            processing is null ? DBNull.Value : (startedAt + processing.Value).ToString("O"));
        cmd.Bind(_database, "read", rowsRead);
        cmd.Bind(_database, "written", rowsWritten);
        cmd.ExecuteNonQuery();
    }

    private RunMetrics Get(TimeSpan window, RunKind? kind = RunKind.Primary, int buckets = 24) =>
        _metrics.Get(Task, _now - window, _now, kind, buckets);

    /// <summary>
    /// An empty window reports zeroes, not nulls. A dash that reads like zero is exactly the invented
    /// reading phase 15 refused — "nothing ran" and "we do not know" are different answers.
    /// </summary>
    [Fact]
    public void AnEmptyWindow_ReportsZeroesRatherThanNulls()
    {
        var metrics = Get(TimeSpan.FromHours(24));

        Assert.Equal(0, metrics.Runs);
        Assert.Equal(0, metrics.Failures);
        Assert.Equal(0, metrics.RowsRead);
        Assert.Equal(0, metrics.RowsWritten);
        Assert.All(metrics.Buckets, b => Assert.Equal(0, b.Runs));

        // Durations are the exception, and deliberately: no run finished, so there is no duration.
        // Zero would claim runs took no time.
        Assert.Null(metrics.ProcessingP50Ms);
        Assert.Null(metrics.ProcessingMaxMs);
    }

    [Fact]
    public void CountsAndSums_CoverEveryRunInTheWindow()
    {
        AddRun(_now.AddHours(-1), TimeSpan.FromSeconds(1), rowsRead: 10, rowsWritten: 8);
        AddRun(_now.AddHours(-2), TimeSpan.FromSeconds(2), rowsRead: 20, rowsWritten: 16);
        AddRun(_now.AddHours(-3), TimeSpan.FromSeconds(3), RunStatus.Failed, rowsRead: 5, rowsWritten: 0);

        var metrics = Get(TimeSpan.FromHours(24));

        Assert.Equal(3, metrics.Runs);
        Assert.Equal(1, metrics.Failures);
        Assert.Equal(35, metrics.RowsRead);
        Assert.Equal(24, metrics.RowsWritten);
    }

    /// <summary>
    /// A known distribution where the percentile and the average disagree: eighteen fast runs and two
    /// slow ones. The mean is about 1.1s and neither percentile is anywhere near it, so a p95 that
    /// quietly returned an average would fail here.
    /// <para>
    /// Two slow runs rather than one, because nearest-rank p95 of twenty values is the nineteenth —
    /// with a single outlier the honest answer is the fast one, since 95% of runs really did take
    /// 100ms. Asserting otherwise would have been asserting a bug.
    /// </para>
    /// </summary>
    [Fact]
    public void Percentiles_AreNotTheAverage()
    {
        for (var i = 0; i < 18; i++)
            AddRun(_now.AddMinutes(-i - 1), TimeSpan.FromMilliseconds(100));
        AddRun(_now.AddMinutes(-19), TimeSpan.FromMilliseconds(10_000));
        AddRun(_now.AddMinutes(-20), TimeSpan.FromMilliseconds(10_000));

        var metrics = Get(TimeSpan.FromHours(24));

        // Millisecond tolerance: the duration comes from julianday arithmetic, which is a double.
        Assert.InRange(metrics.ProcessingP50Ms!.Value, 99, 101);
        Assert.InRange(metrics.ProcessingP95Ms!.Value, 9_999, 10_001);
        Assert.InRange(metrics.ProcessingMaxMs!.Value, 9_999, 10_001);
    }

    [Fact]
    public void ARunStillGoing_CountsButHasNoProcessingTime()
    {
        AddRun(_now.AddMinutes(-5), processing: null, status: RunStatus.Running);

        var metrics = Get(TimeSpan.FromHours(24));

        Assert.Equal(1, metrics.Runs);
        Assert.Null(metrics.ProcessingP50Ms);
    }

    /// <summary>
    /// Processing time is the run, not the run plus its wait for a worker (phase 72's change, kept).
    /// <para>
    /// Two runs that each executed for 100ms, one of which sat in the queue for a minute first. Under
    /// <c>EndedAtUtc - EnqueuedAtUtc</c> the max would have been ~60,100ms and the p50 somewhere between
    /// the two — a backlog would have looked exactly like a slow source. Both figures say 100ms, and the
    /// spread between p50 and max is zero, which is the assertion: the wait is not in this number at
    /// all, not merely reduced.
    /// </para>
    /// </summary>
    [Fact]
    public void ProcessingTime_ExcludesTimeTheRunSpentWaitingInTheQueue()
    {
        AddRun(_now.AddMinutes(-10), TimeSpan.FromMilliseconds(100));
        AddRun(_now.AddMinutes(-20), TimeSpan.FromMilliseconds(100), queueTime: TimeSpan.FromMinutes(1));

        var metrics = Get(TimeSpan.FromHours(24));

        Assert.Equal(2, metrics.Runs);
        // Millisecond tolerance: julianday arithmetic is a double.
        Assert.InRange(metrics.ProcessingP50Ms!.Value, 99, 101);
        Assert.InRange(metrics.ProcessingMaxMs!.Value, 99, 101);
    }

    /// <summary>
    /// The distinction phase 73 exists for, as a number: processing time is measured from the
    /// <em>start</em>, and the claim is a different, earlier moment.
    /// <para>
    /// One run, three ordered points: queued, claimed a minute later, started five seconds after that,
    /// ran for 100ms. Three formulas, three answers — ~65,100ms from the enqueue, ~5,100ms from the
    /// claim (what phase 72 computed, and would still compute if only the write site had moved), and
    /// 100ms from the start. Only the last is asserted, so nothing here can pass by two timestamps
    /// happening to coincide.
    /// </para>
    /// </summary>
    [Fact]
    public void ProcessingTime_IsMeasuredFromTheStart_NotFromTheClaimBeforeIt()
    {
        AddRun(
            _now.AddMinutes(-10), TimeSpan.FromMilliseconds(100),
            queueTime: TimeSpan.FromSeconds(65), claimLatency: TimeSpan.FromSeconds(60));

        var metrics = Get(TimeSpan.FromHours(24));

        Assert.Equal(1, metrics.Runs);
        Assert.InRange(metrics.ProcessingP50Ms!.Value, 99, 101);
        Assert.InRange(metrics.ProcessingMaxMs!.Value, 99, 101);
    }

    /// <summary>
    /// A run that never started has no processing time to contribute — not a zero, and not the wait
    /// itself. This is also every row written before this migration, whose StartedAtUtc is null because
    /// the moment was never recorded, which is why the answer has to be "absent" rather than "computed
    /// from what is there".
    /// </summary>
    [Fact]
    public void ARunThatNeverStarted_ContributesNoProcessingTime()
    {
        AddRun(_now.AddMinutes(-5), TimeSpan.FromSeconds(30), claimed: false);
        AddRun(_now.AddMinutes(-6), TimeSpan.FromSeconds(30), started: false);

        var metrics = Get(TimeSpan.FromHours(24));

        Assert.Equal(2, metrics.Runs);
        Assert.Null(metrics.ProcessingP50Ms);
        Assert.Null(metrics.ProcessingMaxMs);
    }

    /// <summary>
    /// The case that makes the number mean something: the most recent run failed, so "last run" and
    /// "last completed pass" are different answers and only the second one is worth showing.
    /// </summary>
    [Fact]
    public void LastCompletedPass_IgnoresARunThatFailed()
    {
        AddRun(_now.AddHours(-5), TimeSpan.FromSeconds(1));
        AddRun(_now.AddHours(-1), TimeSpan.FromSeconds(1), RunStatus.Failed);

        var metrics = Get(TimeSpan.FromHours(24));

        Assert.Equal(_now.AddHours(-5).AddSeconds(1), metrics.LastCompletedPassUtc);
    }

    /// <summary>
    /// Not bounded by the window, and this is the whole point: it is at its most useful exactly when
    /// it is older than the window, because that is the case where the replication has stopped.
    /// </summary>
    [Fact]
    public void LastCompletedPass_IsFoundEvenWhenItIsOlderThanTheWindow()
    {
        AddRun(_now.AddDays(-30), TimeSpan.FromSeconds(1));

        var metrics = Get(TimeSpan.FromHours(1));

        Assert.Equal(0, metrics.Runs);
        Assert.Equal(_now.AddDays(-30).AddSeconds(1), metrics.LastCompletedPassUtc);
    }

    /// <summary>
    /// A reload moving millions of rows next to incremental passes moving hundreds would dominate
    /// every total, so the two are asked about separately.
    /// </summary>
    [Fact]
    public void RunKind_SeparatesABackfillFromTheIncrementalPasses()
    {
        AddRun(_now.AddHours(-1), TimeSpan.FromSeconds(1), rowsWritten: 100);
        AddRun(_now.AddHours(-1), TimeSpan.FromMinutes(30), rowsWritten: 10_000_000, kind: RunKind.Backfill);

        Assert.Equal(100, Get(TimeSpan.FromHours(24)).RowsWritten);
        Assert.Equal(10_000_000, Get(TimeSpan.FromHours(24), RunKind.Backfill).RowsWritten);
        Assert.Equal(10_000_100, Get(TimeSpan.FromHours(24), kind: null).RowsWritten);
    }

    [Fact]
    public void AnotherReplicationsRuns_AreNotCounted()
    {
        AddRun(_now.AddHours(-1), TimeSpan.FromSeconds(1), rowsWritten: 5, taskName: "other");

        Assert.Equal(0, Get(TimeSpan.FromHours(24)).Runs);
    }

    /// <summary>Where off-by-one lives: the left edge is in, the right edge is out, and a run exactly
    /// on a boundary belongs to exactly one bucket.</summary>
    [Fact]
    public void Bucketing_PutsEachRunInExactlyOneBucket_AndTheRightEdgeIsExclusive()
    {
        AddRun(_now.AddHours(-24), TimeSpan.FromSeconds(1));   // the window's left edge: included
        AddRun(_now.AddHours(-12), TimeSpan.FromSeconds(1));   // a bucket boundary
        AddRun(_now, TimeSpan.FromSeconds(1));                 // the right edge: excluded
        AddRun(_now.AddSeconds(-1), TimeSpan.FromSeconds(1));  // just inside it

        var metrics = Get(TimeSpan.FromHours(24), buckets: 24);

        Assert.Equal(24, metrics.Buckets.Count);
        Assert.Equal(3, metrics.Runs);
        Assert.Equal(3, metrics.Buckets.Sum(b => b.Runs));
        Assert.Equal(1, metrics.Buckets[0].Runs);   // the left edge
        Assert.Equal(1, metrics.Buckets[12].Runs);  // the boundary, in the later bucket
        Assert.Equal(1, metrics.Buckets[23].Runs);  // just inside the right edge
    }

    /// <summary>
    /// TaskRuns grows without bound and the card offers a 7-day window, so the difference between a
    /// seek and a full scan is the difference between this being usable at scale and not. Asserted
    /// against what SQLite says it will do, not assumed from the presence of an index.
    /// </summary>
    [Fact]
    public void TheAggregate_SeeksTheIndexRatherThanScanningTheTable()
    {
        // Enough rows that the planner is choosing rather than shrugging at an empty table.
        for (var i = 0; i < 2_000; i++)
            AddRun(_now.AddMinutes(-i), TimeSpan.FromMilliseconds(50));

        var plan = _metrics.ExplainTotals(Task, _now.AddHours(-24), _now, RunKind.Primary);

        Assert.Contains(plan, line => line.Contains("IX_TaskRuns_TaskName_EnqueuedAt"));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN"));
    }
}
