namespace DataSync.State;

/// <summary>One counter fetch the scheduler's polling gate made, as it was recorded.</summary>
/// <param name="Value">
/// Null when the source answered with no position at all — <c>fn_cdc_get_max_lsn()</c> before the
/// capture job has ever run. Distinct from there being no row, which means the gate never asked or
/// could not.
/// </param>
public sealed record ChangeCheck(
    string ConnectionName,
    string SourceDatabase,
    string SourceKind,
    string? Value,
    DateTimeOffset CheckedAtUtc);

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
    public void Record(string connectionName, string sourceDatabase, string sourceKind, string? value) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO ChangeCheckHistory (ConnectionName, SourceDatabase, SourceKind, Value, CheckedAtUtc)
                VALUES ($connection, $database, $kind, $value, $now);
                """);
            cmd.Bind(database, "connection", connectionName);
            cmd.Bind(database, "database", sourceDatabase);
            cmd.Bind(database, "kind", sourceKind);
            cmd.Bind(database, "value", (object?)value ?? DBNull.Value);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
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
            using var cmd = database.Command(connection, """
                SELECT ConnectionName, SourceDatabase, SourceKind, Value, CheckedAtUtc
                FROM ChangeCheckHistory
                ORDER BY CheckedAtUtc DESC, Id DESC;
                """);

            var checks = new List<ChangeCheck>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                checks.Add(new ChangeCheck(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4))));

            return (IReadOnlyList<ChangeCheck>)checks;
        });
}
