using System.Data.Common;

namespace DataSync.State;

/// <summary>Where one mapping's last completed pass got to, and when the source says that position
/// committed — see phase 87.</summary>
/// <param name="WatermarkTimeUtc">
/// Null for three distinguishable-in-cause but identical-in-answer reasons: the row was written
/// before phase 87 added the column and the mapping has not run since, its reader has no
/// position-to-time mapping at all, or the engine declined to place the position on the pass that
/// stored it. All three mean the same thing to a report — no figure yet — which is why they are one
/// null rather than a state code.
/// </param>
public sealed record AppliedPosition(string Watermark, DateTimeOffset? WatermarkTimeUtc);

/// <summary>
/// Where each table mapping's incremental read got to — one current value per
/// <c>(TaskName, MappingName, SourceTable)</c>.
/// <para>
/// **The mapping is part of the key, not the table alone.** A replication may have any number of table
/// mappings pointing at the same physical source table, and nothing requires them to read it the same
/// way. Two that disagree — one on Change Tracking and one on the generic Watermark reader, or two
/// generic ones with different <c>watermarkColumn</c> options — were previously one row, each pass
/// overwriting a position the other could not interpret. The second case is the worse of the two,
/// because nothing fails: it just tracks the wrong place. A mapping's own identity settles it without
/// anyone having to reason per-reader about which options change watermark semantics. See phase 74.
/// </para>
/// <para>
/// The table stays in the key beside it. It is not needed to disambiguate mappings, but it is what
/// makes a mapping repointed at a different source table start from the beginning rather than resume
/// from a position belonging to a table it no longer reads.
/// </para>
/// </summary>
public sealed class ChangeWatermarkStore(StateDatabase database)
{
    /// <param name="watermarkTimeUtc">
    /// When the source says <paramref name="watermark"/> committed, where the reader that produced it
    /// could say — see phase 87. **In this call rather than a call of its own**, because a position
    /// and its commit time are one fact: written separately they could end up describing two
    /// different passes, and a lag figure computed from a mismatched pair is confidently wrong rather
    /// than merely absent. Overwritten on every pass including a null one, so a reader that stops
    /// being able to state a time clears the stale one instead of leaving it to be read as current.
    /// </param>
    public void SetWatermark(
        string taskName,
        string mappingName,
        string sourceTable,
        string watermark,
        DateTimeOffset? watermarkTimeUtc = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "ChangeWatermarks",
                "TaskName, MappingName, SourceTable, Watermark, UpdatedAtUtc, WatermarkTimeUtc",
                "$task, $mapping, $table, $watermark, $now, $watermarkTime",
                "TaskName, MappingName, SourceTable",
                "Watermark = EXCLUDED.Watermark, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc, "
                    + "WatermarkTimeUtc = EXCLUDED.WatermarkTimeUtc"));
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);
            cmd.Bind(database, "watermark", watermark);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Bind(database, "watermarkTime", (object?)watermarkTimeUtc?.ToString("O") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });

    /// <summary>
    /// The stored position and the source's own time for it, together — what a lag report reads, and
    /// the reason it needs no connection to the source at all (phase 87).
    /// <para>
    /// One row read rather than <see cref="GetWatermark"/> plus a second lookup, for the reason the
    /// two are written together: a pair fetched in two queries can straddle a pass that ran between
    /// them, and the mismatched result would be a lag figure that is wrong rather than missing.
    /// </para>
    /// </summary>
    /// <returns>Null when the mapping has never completed a pass. A row with a null
    /// <c>WatermarkTimeUtc</c> is a different answer: it has a position, and nothing has yet been able
    /// to say when that position committed.</returns>
    public AppliedPosition? GetAppliedPosition(string taskName, string mappingName, string sourceTable) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "SELECT Watermark, WatermarkTimeUtc FROM ChangeWatermarks "
                    + "WHERE TaskName = $task AND MappingName = $mapping AND SourceTable = $table;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);

            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new AppliedPosition(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)))
                : null;
        });

    public string? GetWatermark(string taskName, string mappingName, string sourceTable) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "SELECT Watermark FROM ChangeWatermarks WHERE TaskName = $task AND MappingName = $mapping AND SourceTable = $table;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);
            return cmd.ExecuteScalar() as string;
        });

    /// <summary>
    /// Forgets where a table got to, so the next pass reads it from the beginning.
    /// <para>
    /// The recovery for a position the source no longer retains — see
    /// <c>PositionExpiredException</c>. Deleting the row rather than writing a sentinel, because every
    /// reader already treats "no watermark" as "read everything and start tracking from here", and a
    /// sentinel would be a second thing meaning the same.
    /// </para>
    /// </summary>
    /// <returns>False when there was nothing stored, so a repeat reads as "already cleared".</returns>
    public bool ClearWatermark(string taskName, string mappingName, string sourceTable) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection,
                "DELETE FROM ChangeWatermarks WHERE TaskName = $task AND MappingName = $mapping AND SourceTable = $source;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "source", sourceTable);
            return cmd.ExecuteNonQuery() == 1;
        });
}
