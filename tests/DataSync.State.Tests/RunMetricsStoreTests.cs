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

    /// <summary>Writes a run directly: these are assertions about a query over rows, and going through
    /// the queue to produce them would test the queue.</summary>
    private void AddRun(
        DateTimeOffset startedAt, TimeSpan? duration = null, RunStatus status = RunStatus.Succeeded,
        long rowsRead = 0, long rowsWritten = 0, RunKind kind = RunKind.Primary, string taskName = Task)
    {
        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection, """
            INSERT INTO TaskRuns (RunId, TaskName, Status, RunKind, MappingName, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten)
            VALUES ($runId, $task, $status, $kind, 'orders', $started, $ended, $read, $written);
            """);
        cmd.Bind(_database, "runId", Guid.NewGuid().ToString());
        cmd.Bind(_database, "task", taskName);
        cmd.Bind(_database, "status", status.ToString());
        cmd.Bind(_database, "kind", kind.ToString());
        cmd.Bind(_database, "started", startedAt.ToString("O"));
        cmd.Bind(_database, "ended",
            duration is null ? DBNull.Value : (startedAt + duration.Value).ToString("O"));
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
        Assert.Null(metrics.DurationP50Ms);
        Assert.Null(metrics.DurationMaxMs);
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
        Assert.InRange(metrics.DurationP50Ms!.Value, 99, 101);
        Assert.InRange(metrics.DurationP95Ms!.Value, 9_999, 10_001);
        Assert.InRange(metrics.DurationMaxMs!.Value, 9_999, 10_001);
    }

    [Fact]
    public void ARunStillGoing_CountsButHasNoDuration()
    {
        AddRun(_now.AddMinutes(-5), duration: null, status: RunStatus.Running);

        var metrics = Get(TimeSpan.FromHours(24));

        Assert.Equal(1, metrics.Runs);
        Assert.Null(metrics.DurationP50Ms);
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

        Assert.Contains(plan, line => line.Contains("IX_TaskRuns_TaskName_StartedAt"));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN"));
    }
}
