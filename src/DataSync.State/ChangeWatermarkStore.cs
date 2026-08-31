using System.Data.Common;

namespace DataSync.State;

public sealed class ChangeWatermarkStore(StateDatabase database)
{
    public void SetWatermark(string taskName, string sourceTable, string watermark) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "ChangeWatermarks",
                "TaskName, SourceTable, Watermark, UpdatedAtUtc",
                "$task, $table, $watermark, $now",
                "TaskName, SourceTable",
                "Watermark = EXCLUDED.Watermark, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc"));
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "table", sourceTable);
            cmd.Bind(database, "watermark", watermark);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    public string? GetWatermark(string taskName, string sourceTable) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT Watermark FROM ChangeWatermarks WHERE TaskName = $task AND SourceTable = $table;");
            cmd.Bind(database, "task", taskName);
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
    public bool ClearWatermark(string taskName, string sourceTable) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM ChangeWatermarks WHERE TaskName = $task AND SourceTable = $source;");
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "source", sourceTable);
            return cmd.ExecuteNonQuery() == 1;
        });
}
