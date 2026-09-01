using System.Data.Common;

namespace DataSync.State;

/// <param name="StartUtc">The bucket's left edge; it covers up to the next bucket's edge.</param>
public sealed record RunMetricsBucket(DateTimeOffset StartUtc, int Runs, int Failures, long RowsWritten);

/// <param name="LastCompletedPassUtc">
/// When the most recent **successful** pass finished, and deliberately not bounded by the window: it
/// is at its most useful exactly when it is older than the window, because that is the case where a
/// replication has stopped. Null when there has never been one.
/// </param>
/// <param name="DurationP50Ms">
/// How long the runs themselves took: <c>EndedAtUtc - ClaimedAtUtc</c>, which since phase 72 excludes
/// the time a run spent queued. Queue wait is its own figure, from the two timestamps on the run —
/// deliberately not folded back in here, because a slow source and a busy worker pool are different
/// problems and one number could not tell them apart.
/// <para>
/// Null when nothing in the window finished — a run still going has no duration, and inventing one for
/// it would put a number in a card that means nothing. Null too when nothing in the window ever began.
/// </para>
/// </param>
public sealed record RunMetrics(
    string TaskName,
    RunKind? RunKind,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int Runs,
    int Failures,
    long RowsRead,
    long RowsWritten,
    double? DurationP50Ms,
    double? DurationP95Ms,
    double? DurationMaxMs,
    DateTimeOffset? LastCompletedPassUtc,
    IReadOnlyList<RunMetricsBucket> Buckets);

/// <summary>
/// Aggregates over <c>TaskRuns</c>, which already holds everything these numbers are made of — kind,
/// status, start, end, rows read and rows written, per run. This is the query nobody had written, not
/// data nobody had recorded.
/// <para>
/// Timestamps are compared as text, which is correct here because every writer stores
/// <c>DateTimeOffset.UtcNow.ToString("O")</c> — a fixed-width ISO-8601 string at offset +00:00, where
/// lexicographic and chronological order coincide. The same assumption the work queue's own
/// availability check has always made.
/// </para>
/// </summary>
public sealed class RunMetricsStore(StateDatabase database)
{
    public RunMetrics Get(
        string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc, RunKind? runKind, int buckets) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();

            var kindClause = runKind is null ? "" : " AND RunKind = $runKind";
            var totals = ReadTotals(connection, taskName, fromUtc, toUtc, runKind, kindClause);
            var durations = ReadDurations(connection, taskName, fromUtc, toUtc, runKind, kindClause);

            return new RunMetrics(
                taskName, runKind, fromUtc, toUtc,
                totals.Runs, totals.Failures, totals.RowsRead, totals.RowsWritten,
                Percentile(durations, 0.50), Percentile(durations, 0.95),
                durations.Count == 0 ? null : durations[^1],
                ReadLastCompletedPass(connection, taskName, runKind, kindClause),
                ReadBuckets(connection, taskName, fromUtc, toUtc, runKind, kindClause, buckets));
        });

    private (int Runs, int Failures, long RowsRead, long RowsWritten) ReadTotals(
        DbConnection connection, string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        RunKind? runKind, string kindClause)
    {
        using var cmd = database.Command(connection, TotalsSql(kindClause));
        Bind(cmd, taskName, fromUtc, toUtc, runKind);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (0, 0, 0, 0);

        // SUM over no rows is NULL in SQL, and zero is the honest reading of "nothing happened" —
        // a dash where a zero belongs is exactly the invented reading phase 15 refused.
        return (
            reader.Int32(0),
            reader.IsDBNull(1) ? 0 : reader.Int32(1),
            reader.IsDBNull(2) ? 0 : reader.Int64(2),
            reader.IsDBNull(3) ? 0 : reader.Int64(3));
    }

    /// <summary>
    /// The aggregate, as one string, so that <see cref="ExplainTotals"/> can ask the planner about the
    /// statement this actually runs rather than about a copy of it that could drift from it.
    /// </summary>
    private static string TotalsSql(string kindClause) => $"""
        SELECT COUNT(*),
               SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END),
               SUM(RowsRead),
               SUM(RowsWritten)
        FROM TaskRuns
        WHERE TaskName = $taskName AND StartedAtUtc >= $from AND StartedAtUtc < $to{kindClause};
        """;

    /// <summary>
    /// How SQLite says it will answer the aggregate. TaskRuns grows without bound and a console that
    /// offers 7 days invites a large scan, so "does this use the index" is a question worth being able
    /// to ask — of the real statement, and from a test.
    /// </summary>
    public IReadOnlyList<string> ExplainTotals(
        string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc, RunKind? runKind) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "EXPLAIN QUERY PLAN " + TotalsSql(runKind is null ? "" : " AND RunKind = $runKind"));
            Bind(cmd, taskName, fromUtc, toUtc, runKind);

            using var reader = cmd.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read())
                plan.Add(reader.GetString(reader.GetOrdinal("detail")));
            return (IReadOnlyList<string>)plan;
        });

    /// <summary>
    /// Every finished run's duration, ascending, for percentiles computed in C#.
    /// <para>
    /// **Measured from <c>ClaimedAtUtc</c>, not <c>StartedAtUtc</c>** (phase 72). The latter is written
    /// at enqueue time, so until this changed these percentiles reported the run plus however long it
    /// had waited for a worker — under a backlog, a "duration" that was mostly queue. A run with no
    /// <c>ClaimedAtUtc</c> never began, so it contributes no duration at all rather than a wrong one;
    /// that includes rows written before the column existed, which is why the filter is explicit here
    /// rather than left to <c>julianday(NULL)</c>.
    /// </para>
    /// <para>
    /// SQLite has no <c>PERCENTILE_CONT</c>, so the choice was this or three <c>ORDER BY … LIMIT 1
    /// OFFSET n</c> queries. One column of one index scan beats three scans of the same index, the
    /// result is exact rather than interpolated, and at the size this is asked about — a week of runs —
    /// the list is small. Revisit if that stops being true; <c>tools/benchmarks</c> is where.
    /// </para>
    /// </summary>
    private List<double> ReadDurations(
        DbConnection connection, string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        RunKind? runKind, string kindClause)
    {
        using var cmd = database.Command(connection, $"""
            SELECT (julianday(EndedAtUtc) - julianday(ClaimedAtUtc)) * 86400000.0
            FROM TaskRuns
            WHERE TaskName = $taskName AND StartedAtUtc >= $from AND StartedAtUtc < $to
              AND EndedAtUtc IS NOT NULL AND ClaimedAtUtc IS NOT NULL{kindClause}
            ORDER BY 1;
            """);
        Bind(cmd, taskName, fromUtc, toUtc, runKind);

        using var reader = cmd.ExecuteReader();
        var durations = new List<double>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                durations.Add(Math.Max(0, reader.GetDouble(0)));
        }
        return durations;
    }

    private DateTimeOffset? ReadLastCompletedPass(
        DbConnection connection, string taskName, RunKind? runKind, string kindClause)
    {
        using var cmd = connection.CreateCommand();
        // No window: "nothing has completed in 24 hours" is the answer this is for, and a query that
        // could only look inside the window could never give it.
        cmd.CommandText = $"""
            SELECT MAX(EndedAtUtc) FROM TaskRuns
            WHERE TaskName = $taskName AND Status = 'Succeeded' AND EndedAtUtc IS NOT NULL{kindClause};
            """;
        cmd.Bind(database, "taskName", taskName);
        if (runKind is not null)
            cmd.Bind(database, "runKind", runKind.Value.ToString());

        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : DateTimeOffset.Parse((string)result, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    /// <summary>
    /// Bucketed by index rather than by a SQL date function, so the boundaries are exactly the ones
    /// the caller asked for and a bucket cannot land in two places because of rounding.
    /// </summary>
    private IReadOnlyList<RunMetricsBucket> ReadBuckets(
        DbConnection connection, string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        RunKind? runKind, string kindClause, int buckets)
    {
        buckets = Math.Clamp(buckets, 1, 200);
        var width = (toUtc - fromUtc) / buckets;

        var counts = new int[buckets];
        var failures = new int[buckets];
        var written = new long[buckets];

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT StartedAtUtc, Status, RowsWritten FROM TaskRuns
                WHERE TaskName = $taskName AND StartedAtUtc >= $from AND StartedAtUtc < $to{kindClause};
                """;
            Bind(cmd, taskName, fromUtc, toUtc, runKind);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var startedAt = DateTimeOffset.Parse(
                    reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind);

                // The right edge is exclusive in the WHERE above, so this cannot be `buckets`; the
                // clamp is for a zero-width window, where every run belongs to the only bucket.
                var index = Math.Clamp((int)((startedAt - fromUtc) / width), 0, buckets - 1);
                counts[index]++;
                if (reader.GetString(1) == nameof(RunStatus.Failed))
                    failures[index]++;
                written[index] += reader.Int64(2);
            }
        }

        return [.. Enumerable.Range(0, buckets)
            .Select(i => new RunMetricsBucket(fromUtc + width * i, counts[i], failures[i], written[i]))];
    }

    /// <summary>Nearest-rank, on an already-sorted list. Exact — it names a run that actually took
    /// that long, rather than interpolating between two that did not.</summary>
    private static double? Percentile(List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
            return null;
        var rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private void Bind(
        DbCommand cmd, string taskName, DateTimeOffset fromUtc, DateTimeOffset toUtc, RunKind? runKind)
    {
        cmd.Bind(database, "taskName", taskName);
        cmd.Bind(database, "from", fromUtc.ToString("O"));
        cmd.Bind(database, "to", toUtc.ToString("O"));
        if (runKind is not null)
            cmd.Bind(database, "runKind", runKind.Value.ToString());
    }
}
