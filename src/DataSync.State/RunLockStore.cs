namespace DataSync.State;

/// <summary>Prevents overlapping runs of the same task (architecture/detailed-design.md §3.1, §3.7).</summary>
public sealed class RunLockStore(StateDatabase database)
{
    public bool TryAcquire(string taskName, Guid runId) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO RunLocks (TaskName, RunId, AcquiredAtUtc)
                VALUES ($task, $runId, $now)
                ON CONFLICT(TaskName) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.Parameters.AddWithValue("$runId", runId.ToString());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return cmd.ExecuteNonQuery() == 1;
        });

    public void Release(string taskName) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM RunLocks WHERE TaskName = $task;";
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.ExecuteNonQuery();
        });

    public bool IsLocked(string taskName) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM RunLocks WHERE TaskName = $task;";
            cmd.Parameters.AddWithValue("$task", taskName);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });
}
