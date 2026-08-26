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
}
