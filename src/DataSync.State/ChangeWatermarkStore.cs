namespace DataSync.State;

public sealed class ChangeWatermarkStore(StateDatabase database)
{
    public void SetWatermark(string taskName, string sourceTable, string cursor) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ChangeWatermarks (TaskName, SourceTable, Cursor, UpdatedAtUtc)
                VALUES ($task, $table, $cursor, $now)
                ON CONFLICT(TaskName, SourceTable) DO UPDATE SET Cursor = excluded.Cursor, UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.Parameters.AddWithValue("$table", sourceTable);
            cmd.Parameters.AddWithValue("$cursor", cursor);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    public string? GetWatermark(string taskName, string sourceTable) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Cursor FROM ChangeWatermarks WHERE TaskName = $task AND SourceTable = $table;";
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.Parameters.AddWithValue("$table", sourceTable);
            return cmd.ExecuteScalar() as string;
        });
}
