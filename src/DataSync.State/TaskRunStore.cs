using Microsoft.Data.Sqlite;

namespace DataSync.State;

public sealed class TaskRunStore(StateDatabase database)
{
    public void UpsertTask(string taskName, bool enabled) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Tasks (Name, Enabled, UpdatedAtUtc) VALUES ($name, $enabled, $now)
                ON CONFLICT(Name) DO UPDATE SET Enabled = excluded.Enabled, UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            cmd.Parameters.AddWithValue("$name", taskName);
            cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    public void StartRun(Guid runId, string taskName, int? pid) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO TaskRuns (RunId, TaskName, Pid, Status, StartedAtUtc, RowsRead, RowsWritten)
                VALUES ($runId, $taskName, $pid, $status, $startedAt, 0, 0);
                """;
            cmd.Parameters.AddWithValue("$runId", runId.ToString());
            cmd.Parameters.AddWithValue("$taskName", taskName);
            cmd.Parameters.AddWithValue("$pid", (object?)pid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", RunStatus.Running.ToString());
            cmd.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    public void CompleteRun(Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE TaskRuns
                SET Status = $status, EndedAtUtc = $endedAt, RowsRead = $rowsRead, RowsWritten = $rowsWritten, ErrorSummary = $error
                WHERE RunId = $runId;
                """;
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$endedAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$rowsRead", rowsRead);
            cmd.Parameters.AddWithValue("$rowsWritten", rowsWritten);
            cmd.Parameters.AddWithValue("$error", (object?)errorSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$runId", runId.ToString());
            cmd.ExecuteNonQuery();
        });

    public TaskRunRecord? GetRun(Guid runId) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT RunId, TaskName, Pid, Status, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary
                FROM TaskRuns WHERE RunId = $runId;
                """;
            cmd.Parameters.AddWithValue("$runId", runId.ToString());
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRun(reader) : null;
        });

    public IReadOnlyList<TaskRunRecord> GetRunHistory(string taskName, int limit = 50) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT RunId, TaskName, Pid, Status, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary
                FROM TaskRuns WHERE TaskName = $taskName ORDER BY StartedAtUtc DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$taskName", taskName);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var results = new List<TaskRunRecord>();
            while (reader.Read())
                results.Add(ReadRun(reader));
            return (IReadOnlyList<TaskRunRecord>)results;
        });

    /// <summary>Runs still marked Running in the store — used to reconcile against live OS processes
    /// on API startup (architecture/detailed-design.md §3.1).</summary>
    public IReadOnlyList<TaskRunRecord> GetRunningRuns() =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT RunId, TaskName, Pid, Status, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary
                FROM TaskRuns WHERE Status = $status;
                """;
            cmd.Parameters.AddWithValue("$status", RunStatus.Running.ToString());
            using var reader = cmd.ExecuteReader();
            var results = new List<TaskRunRecord>();
            while (reader.Read())
                results.Add(ReadRun(reader));
            return (IReadOnlyList<TaskRunRecord>)results;
        });

    private static TaskRunRecord ReadRun(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetInt32(2),
        Enum.Parse<RunStatus>(reader.GetString(3)),
        DateTimeOffset.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
        reader.GetInt64(6),
        reader.GetInt64(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));
}
