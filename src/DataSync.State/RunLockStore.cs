namespace DataSync.State;

/// <summary>
/// Prevents overlapping runs of the same (task, run kind, table mapping) — architecture/detailed-design.md
/// §3.1, §3.7, and architecture/implementation/phase-9-work-queue-schema.md for why this is scoped per
/// mapping rather than per replication: a Primary pass is now "this mapping's next incremental pass,"
/// not "this replication's next incremental pass over every mapping," so one slow mapping no longer
/// blocks every other mapping's schedule.
/// </summary>
public sealed class RunLockStore(StateDatabase database)
{
    public bool TryAcquire(string taskName, RunKind runKind, string mappingName, Guid runId) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO RunLocks (TaskName, RunKind, MappingName, RunId, AcquiredAtUtc)
                VALUES ($task, $kind, $mapping, $runId, $now)
                ON CONFLICT(TaskName, RunKind, MappingName) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.Parameters.AddWithValue("$kind", runKind.ToString());
            cmd.Parameters.AddWithValue("$mapping", mappingName);
            cmd.Parameters.AddWithValue("$runId", runId.ToString());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return cmd.ExecuteNonQuery() == 1;
        });

    public void Release(string taskName, RunKind runKind, string mappingName) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM RunLocks WHERE TaskName = $task AND RunKind = $kind AND MappingName = $mapping;";
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.Parameters.AddWithValue("$kind", runKind.ToString());
            cmd.Parameters.AddWithValue("$mapping", mappingName);
            cmd.ExecuteNonQuery();
        });

    public bool IsLocked(string taskName, RunKind runKind, string mappingName) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM RunLocks WHERE TaskName = $task AND RunKind = $kind AND MappingName = $mapping;";
            cmd.Parameters.AddWithValue("$task", taskName);
            cmd.Parameters.AddWithValue("$kind", runKind.ToString());
            cmd.Parameters.AddWithValue("$mapping", mappingName);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });
}
