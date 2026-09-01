using System.Data.Common;

namespace DataSync.State;

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
    public void SetWatermark(string taskName, string mappingName, string sourceTable, string watermark) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "ChangeWatermarks",
                "TaskName, MappingName, SourceTable, Watermark, UpdatedAtUtc",
                "$task, $mapping, $table, $watermark, $now",
                "TaskName, MappingName, SourceTable",
                "Watermark = EXCLUDED.Watermark, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc"));
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "table", sourceTable);
            cmd.Bind(database, "watermark", watermark);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
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
