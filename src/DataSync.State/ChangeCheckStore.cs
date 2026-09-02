namespace DataSync.State;

/// <summary>One counter fetch the scheduler's polling gate made, as it was recorded.</summary>
/// <param name="Value">
/// Null when the source answered with no position at all — <c>fn_cdc_get_max_lsn()</c> before the
/// capture job has ever run. Distinct from there being no row, which means the gate never asked or
/// could not.
/// </param>
/// <param name="SourceTimeUtc">
/// When the source says the position in <paramref name="Value"/> was committed — via
/// <c>sys.fn_cdc_map_lsn_to_time</c> for a CDC row and <c>sys.dm_tran_commit_table</c> for a Change
/// Tracking one, both captured on the gate's own round-trip (phases 85 and 87).
/// <para>
/// Null when there is no position to map, when the engine will not place the one there is, and on
/// any Change Tracking row written before phase 87 — which is what keeps that mechanism's
/// polling-history estimate a live fallback rather than dead code.
/// </para>
/// </param>
public sealed record ChangeCheck(
    string ConnectionName,
    string SourceDatabase,
    string SourceKind,
    string? Value,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? SourceTimeUtc = null);

/// <summary>
/// The append-only history of the scheduler's change-counter fetches — see phase 75.
/// <para>
/// **Written, never read back by the gate that writes it.** The gate decides whether to dispatch a
/// mapping by comparing the value it just fetched against that mapping's own row in
/// <see cref="ChangeWatermarkStore"/>, not against anything stored here. That distinction is the
/// phase's central correctness point: a "last value the gate saw" cache would skip a mapping that is
/// mid-drain of a real backlog under a row cap, because the source counter genuinely does not move
/// between those passes when nothing new is being written. A mapping's own watermark is the only
/// thing that can tell "caught up" from "still catching up".
/// </para>
/// <para>
/// What this table is for is the question the gate makes harder to answer by existing: the scheduler
/// now suppresses work, so "the source was quiet" and "we stopped looking" need to be
/// distinguishable after the fact. One row per fetch, with its timestamp, is that record.
/// </para>
/// </summary>
public sealed class ChangeCheckStore(StateDatabase database)
{
    /// <summary>
    /// Records one fetch. Called once per <c>(ConnectionName, SourceDatabase, SourceKind)</c> group
    /// per tick, however many mappings share that group — the row describes the source round-trip,
    /// not the mappings that benefited from it.
    /// </summary>
    /// <param name="sourceTimeUtc">The source's own time for <paramref name="value"/>, captured on
    /// the same tick that fetched it — for both mechanisms since phase 87, and CDC only before
    /// that.</param>
    public void Record(
        string connectionName,
        string sourceDatabase,
        string sourceKind,
        string? value,
        DateTimeOffset? sourceTimeUtc = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO ChangeCheckHistory (ConnectionName, SourceDatabase, SourceKind, Value, CheckedAtUtc, SourceTimeUtc)
                VALUES ($connection, $database, $kind, $value, $now, $sourceTime);
                """);
            cmd.Bind(database, "connection", connectionName);
            cmd.Bind(database, "database", sourceDatabase);
            cmd.Bind(database, "kind", sourceKind);
            cmd.Bind(database, "value", (object?)value ?? DBNull.Value);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Bind(database, "sourceTime", (object?)sourceTimeUtc?.ToString("O") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// Deletes checks older than <paramref name="maxAge"/>. Null keeps every check forever, matching
    /// how <c>RunRetentionDays</c> reads a configured 0.
    /// <para>
    /// Age is the only criterion. <c>PruneRuns</c>' second cap is per mapping, and this table is not
    /// mapping-scoped — a group is shared by however many mappings poll through it — so there is no
    /// per-something count that would mean the same thing here.
    /// </para>
    /// </summary>
    public int PruneChecks(TimeSpan? maxAge) =>
        database.Retry(() =>
        {
            if (maxAge is null)
                return 0;

            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "DELETE FROM ChangeCheckHistory WHERE CheckedAtUtc < $cutoff;");
            cmd.Bind(database, "cutoff", DateTimeOffset.UtcNow.Subtract(maxAge.Value).ToString("O"));
            return cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// Every recorded check, most recent first. The whole table, because nothing in the product pages
    /// through it yet — a status view that wants the latest value per group is a later phase, and
    /// would want its own query and its own index rather than this one filtered in memory.
    /// </summary>
    public IReadOnlyList<ChangeCheck> ListChecks() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT {Columns}
                FROM ChangeCheckHistory
                ORDER BY CheckedAtUtc DESC, Id DESC;
                """);

            var checks = new List<ChangeCheck>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                checks.Add(Read(reader));

            return (IReadOnlyList<ChangeCheck>)checks;
        });

    /// <summary>
    /// The most recent check for one <c>(ConnectionName, SourceDatabase, SourceKind)</c> group, or
    /// null when the gate has never recorded one for it — see phase 85, which is the first reader of
    /// this table other than the age purge.
    /// <para>
    /// This is what every lag figure is measured *against*: where the source had got to the last time
    /// anybody looked, never wall-clock now. Comparing a mapping's position to the present makes lag
    /// climb for ever on a source that is quiet and fully caught up, which is precisely the state
    /// nothing should be reported about.
    /// </para>
    /// </summary>
    /// <param name="requireSourceTime">
    /// True to take the most recent row that carries a <c>SourceTimeUtc</c>, skipping any newer rows
    /// without one. The two nulls this column can hold — the capture job has not run, and the
    /// position is no longer retained — are recorded rather than hidden, so the caller that needs a
    /// time asks for the latest row that has one instead of the latest row.
    /// </param>
    public ChangeCheck? GetLatestCheck(
        string connectionName, string sourceDatabase, string sourceKind, bool requireSourceTime = false) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT {Columns}
                FROM ChangeCheckHistory
                WHERE ConnectionName = $connection AND SourceDatabase = $database AND SourceKind = $kind
                  {(requireSourceTime ? "AND SourceTimeUtc IS NOT NULL" : string.Empty)}
                ORDER BY CheckedAtUtc DESC, Id DESC;
                """);
            cmd.Bind(database, "connection", connectionName);
            cmd.Bind(database, "database", sourceDatabase);
            cmd.Bind(database, "kind", sourceKind);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    /// <summary>
    /// The earliest check in a group whose recorded value <paramref name="matches"/> accepts — the
    /// crossing row a Change Tracking time estimate is anchored on when, and only when, the engine's
    /// own <c>dm_tran_commit_table</c> mapping has aged the mapping's version out (phase 85).
    /// </summary>
    /// <param name="matches">
    /// **The comparison is a delegate rather than a predicate in the SQL, deliberately.** <c>Value</c>
    /// is text, and this store runs on SQLite, Postgres and SQL Server; a <c>&gt;=</c> against it in
    /// SQL is a collation-ordered string comparison in all three, which puts "9" above "10" and would
    /// anchor the estimate on the wrong row for every group whose version count has just gained a
    /// digit. Parsing to <c>long</c> in the caller's own language keeps one definition of "further
    /// on" — the same point <c>ChangeCounters.Compare</c> makes for this table's other reader. Rows
    /// with no value never reach it.
    /// </param>
    /// <remarks>
    /// Streamed and stopped at the first match rather than materialised: a group is written to once
    /// per tick, so a week of retention is six figures of rows and the crossing row is usually near
    /// the far end of them.
    /// </remarks>
    public ChangeCheck? FindEarliestCheck(
        string connectionName, string sourceDatabase, string sourceKind, Func<string, bool> matches) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = AscendingByTime(connection, connectionName, sourceDatabase, sourceKind);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var check = Read(reader);
                if (matches(check.Value!))
                    return check;
            }

            return null;
        });

    /// <summary>
    /// The same crossing-row lookup as <see cref="FindEarliestCheck"/>, for several targets at once
    /// and in a single pass over the group's history — phase 88, which asks it of every watermark on
    /// a page of run history rather than of one mapping's version.
    /// <para>
    /// **One scan, not one per target, and that is the whole reason this exists.** A page of fifty
    /// runs carries up to a hundred watermarks, and <see cref="FindEarliestCheck"/> streams the
    /// group's rows from the oldest until it matches — so calling it a hundred times is a hundred
    /// scans of a table that holds a row per tick per group, which for a week of retention is six
    /// figures of them. The rows are read once here and every target is tested against each row as
    /// it goes by, which makes the cost the history's size rather than the history's size times the
    /// page's.
    /// </para>
    /// <para>
    /// **A target the very first retained row already reaches gets no answer**, and that is the one
    /// behavioural difference from <see cref="FindEarliestCheck"/> rather than an optimisation. The
    /// two ask different questions of the same rows. Phase 85 wants an *anchor* — the earliest row
    /// we can prove the source had got that far — and the oldest retained row is a fine one. This
    /// wants a *date*, and a crossing is only dated by a row that a strictly earlier row had not yet
    /// reached: without one below it, the real crossing happened before the window and the oldest
    /// row's timestamp is a later moment being reported as the answer. Counter values only ever
    /// climb, so every run older than <c>ChangeCheckRetentionDays</c> would otherwise come back
    /// dated to whenever the purge last ran.
    /// </para>
    /// <para>
    /// It costs the genuine first poll of a brand-new install, which is indistinguishable from a
    /// purged one and is reported as unknown. That is the direction to be wrong in: the caller
    /// renders a missing timestamp as missing, and a fabricated one it would render as fact.
    /// </para>
    /// <para>
    /// Returns only the targets that were dated. An absent target is one the retained history cannot
    /// place — below its oldest row, or beyond its newest — and the caller is expected to render
    /// that absence rather than treat it as a failure.
    /// </para>
    /// </summary>
    /// <param name="reaches">
    /// <c>reaches(recordedValue, target)</c> — true when a history row's value has got to or past
    /// that target. A delegate for exactly the reason <see cref="FindEarliestCheck"/>'s is: the
    /// comparison belongs to the mechanism, and in SQL it would be a collation-ordered comparison of
    /// text on all three engines this store runs on.
    /// </param>
    public IReadOnlyDictionary<string, ChangeCheck> FindEarliestChecks(
        string connectionName,
        string sourceDatabase,
        string sourceKind,
        IReadOnlyCollection<string> targets,
        Func<string, string, bool> reaches) =>
        database.Retry(() =>
        {
            var found = new Dictionary<string, ChangeCheck>(StringComparer.Ordinal);
            if (targets.Count == 0)
                return (IReadOnlyDictionary<string, ChangeCheck>)found;

            var pending = new List<string>(targets.Distinct(StringComparer.Ordinal));

            using var connection = database.OpenConnection();
            using var cmd = AscendingByTime(connection, connectionName, sourceDatabase, sourceKind);

            using var reader = cmd.ExecuteReader();
            var oldest = true;
            while (reader.Read() && pending.Count > 0)
            {
                var check = Read(reader);

                // Backwards so a resolved target can be removed without disturbing the rest of the
                // walk. Earliest wins, which the ascending order gives for free: the first row to
                // reach a target is the one that gets recorded, and the target leaves the list.
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    if (!reaches(check.Value!, pending[i]))
                        continue;

                    // On the oldest retained row, "reached" means "was already reached before we
                    // can see" — the target leaves the list undated rather than dated to a row that
                    // did not cross it. See the remarks above.
                    if (!oldest)
                        found[pending[i]] = check;

                    pending.RemoveAt(i);
                }

                oldest = false;
            }

            return (IReadOnlyDictionary<string, ChangeCheck>)found;
        });

    private System.Data.Common.DbCommand AscendingByTime(
        System.Data.Common.DbConnection connection,
        string connectionName,
        string sourceDatabase,
        string sourceKind)
    {
        var cmd = database.Command(connection, $"""
            SELECT {Columns}
            FROM ChangeCheckHistory
            WHERE ConnectionName = $connection AND SourceDatabase = $database AND SourceKind = $kind
              AND Value IS NOT NULL
            ORDER BY CheckedAtUtc, Id;
            """);
        cmd.Bind(database, "connection", connectionName);
        cmd.Bind(database, "database", sourceDatabase);
        cmd.Bind(database, "kind", sourceKind);
        return cmd;
    }

    private const string Columns =
        "ConnectionName, SourceDatabase, SourceKind, Value, CheckedAtUtc, SourceTimeUtc";

    private static ChangeCheck Read(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)));
}
