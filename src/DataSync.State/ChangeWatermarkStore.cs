namespace DataSync.State;

public sealed class ChangeWatermarkStore(StateDatabase database)
{
    public void SetWatermark(string taskName, string sourceTable, string watermark) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ChangeWatermarks (TaskName, SourceTable, Watermark, UpdatedAtUtc)
                VALUES ($task, $table, $watermark, $now)
                ON CONFLICT(TaskName, SourceTable) DO UPDATE SET Watermark = excluded.Watermark, UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.Parameters.AddWithValue("$table", sourceTable);
            cmd.Parameters.AddWithValue("$watermark", watermark);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    public string? GetWatermark(string taskName, string sourceTable) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Watermark FROM ChangeWatermarks WHERE TaskName = $task AND SourceTable = $table;";
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.Parameters.AddWithValue("$table", sourceTable);
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
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ChangeWatermarks WHERE TaskName = $task AND SourceTable = $source;";
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.Parameters.AddWithValue("$source", sourceTable);
            return cmd.ExecuteNonQuery() == 1;
        });
}
