using Microsoft.Data.Sqlite;

namespace DataSync.State;

public sealed class TaskRunStore(StateDatabase database)
{
    /// <summary>Last Primary-run start time per table mapping, in one query — what
    /// SchedulerService's due-ness check needs. One query per replication per tick, not one per
    /// mapping per tick: a replication can have hundreds of mappings, and N+1 queries at that scale
    /// on every 5-second tick would be the first thing to hurt.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> GetLastPrimaryStartByMapping(string taskName) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT MappingName, MAX(StartedAtUtc)
                FROM TaskRuns WHERE TaskName = $taskName AND RunKind = $runKind
                GROUP BY MappingName;
                """;
            cmd.Parameters.AddWithValue("$taskName", taskName);
            cmd.Parameters.AddWithValue("$runKind", RunKind.Primary.ToString());
            using var reader = cmd.ExecuteReader();
            var results = new Dictionary<string, DateTimeOffset>();
            while (reader.Read())
                results[reader.GetString(0)] = DateTimeOffset.Parse(reader.GetString(1));
            return (IReadOnlyDictionary<string, DateTimeOffset>)results;
        });

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

    /// <summary>Transitions an existing Queued row (written by WorkQueueStore.Enqueue when the work
    /// was queued) to Running, once a worker actually claims and begins processing it. There is no
    /// longer an INSERT-based "start a run" method — every run's row now originates from
    /// WorkQueueStore.Enqueue, so a queued backlog is visible in run history before any worker exists
    /// to work on it.</summary>
    public void BeginRun(Guid runId, int? pid) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE TaskRuns SET Status = $status, Pid = $pid WHERE RunId = $runId;";
            cmd.Parameters.AddWithValue("$status", RunStatus.Running.ToString());
            cmd.Parameters.AddWithValue("$pid", (object?)pid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$runId", runId.ToString());
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
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary
                FROM TaskRuns WHERE RunId = $runId;
                """;
            cmd.Parameters.AddWithValue("$runId", runId.ToString());
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRun(reader) : null;
        });

    /// <summary>Run history for a task, optionally filtered to one RunKind. A null runKind (the
    /// default) mixes Primary and Backfill rows — what the SPA's run-history view wants; scheduling
    /// due-ness checks must always pass RunKind.Primary explicitly so a Backfill run never perturbs
    /// the incremental schedule's timing.</summary>
    public IReadOnlyList<TaskRunRecord> GetRunHistory(string taskName, RunKind? runKind = null, int limit = 50) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary
                FROM TaskRuns WHERE TaskName = $taskName {(runKind is null ? "" : "AND RunKind = $runKind")}
                ORDER BY StartedAtUtc DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$taskName", taskName);
            if (runKind is not null)
                cmd.Parameters.AddWithValue("$runKind", runKind.Value.ToString());
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var results = new List<TaskRunRecord>();
            while (reader.Read())
                results.Add(ReadRun(reader));
            return (IReadOnlyList<TaskRunRecord>)results;
        });

    /// <summary>Run history for one specific table mapping (either RunKind) — used by scheduling
    /// due-ness (RunKind.Primary) and by the SPA's per-mapping history views.</summary>
    public IReadOnlyList<TaskRunRecord> GetMappingRunHistory(string taskName, RunKind runKind, string mappingName, int limit = 50) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary
                FROM TaskRuns WHERE TaskName = $taskName AND RunKind = $runKind AND MappingName = $mapping
                ORDER BY StartedAtUtc DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$taskName", taskName);
            cmd.Parameters.AddWithValue("$runKind", runKind.ToString());
            cmd.Parameters.AddWithValue("$mapping", mappingName);
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
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary
                FROM TaskRuns WHERE Status = $status;
                """;
            cmd.Parameters.AddWithValue("$status", RunStatus.Running.ToString());
            using var reader = cmd.ExecuteReader();
            var results = new List<TaskRunRecord>();
            while (reader.Read())
                results.Add(ReadRun(reader));
            return (IReadOnlyList<TaskRunRecord>)results;
        });

    /// <summary>Rows that ended at or after <paramref name="sinceUtc"/> — lets RunMonitorService
    /// notice a run that completed its entire lifecycle (Queued -> Running -> terminal) between two
    /// polling ticks, which GetActiveRuns() alone can miss entirely: a run that never once overlaps a
    /// poll was never "seen" as active, so nothing would otherwise trigger a runCompleted broadcast for
    /// it. Only possible now that a claimed unit of work can complete in well under a second (no
    /// per-run process spawn overhead) — see phase-8-work-queue-schema.md.</summary>
    public IReadOnlyList<TaskRunRecord> GetRecentlyEndedRuns(DateTimeOffset sinceUtc) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary
                FROM TaskRuns WHERE EndedAtUtc IS NOT NULL AND EndedAtUtc >= $since;
                """;
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
            using var reader = cmd.ExecuteReader();
            var results = new List<TaskRunRecord>();
            while (reader.Read())
                results.Add(ReadRun(reader));
            return (IReadOnlyList<TaskRunRecord>)results;
        });

    /// <summary>Rows in Queued or Running status — what RunMonitorService watches for completion once
    /// one worker process can back many concurrently-active RunIds (Process.HasExited stops being a
    /// meaningful completion signal at that point). See phase-8-work-queue-schema.md.</summary>
    public IReadOnlyList<TaskRunRecord> GetActiveRuns() =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary
                FROM TaskRuns WHERE Status IN ($queued, $running);
                """;
            cmd.Parameters.AddWithValue("$queued", RunStatus.Queued.ToString());
            cmd.Parameters.AddWithValue("$running", RunStatus.Running.ToString());
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
        Enum.Parse<RunKind>(reader.GetString(4)),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        DateTimeOffset.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
        reader.GetInt64(9),
        reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetString(11));
}
