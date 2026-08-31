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

    /// <summary>
    /// Holds a replication, or releases it, and records that somebody did — see phase 64.
    /// <para>
    /// Both writes happen in one transaction. <c>Tasks</c> is the fast current state the scheduler
    /// reads on every tick; <c>PauseEvents</c> is the history. Two separate writes could leave a
    /// replication paused with nothing saying who paused it, which is precisely the question the
    /// history exists to answer.
    /// </para>
    /// <para>
    /// The row is upserted rather than updated, because a replication that has never run has no
    /// <c>Tasks</c> row yet — <c>UpsertTask</c> is called by a worker starting up, and pausing
    /// something before it has ever started is a perfectly ordinary thing to do. <c>Enabled</c>
    /// defaults to 1 on insert: it is config's answer, mirrored here, and this method has no business
    /// inventing one, so it writes the permissive value the mirror is refreshed from anyway.
    /// </para>
    /// </summary>
    /// <param name="note">
    /// Null and empty are stored as-is rather than normalised to one: the popup lets an operator
    /// deliberately clear the note, and "cleared it" is a different act from "never wrote one".
    /// </param>
    public void SetPaused(string taskName, bool paused, string? note, string performedBy) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO Tasks (Name, Enabled, Paused, PauseNote, UpdatedAtUtc)
                    VALUES ($name, 1, $paused, $note, $now)
                    ON CONFLICT(Name) DO UPDATE SET
                        Paused = excluded.Paused,
                        PauseNote = excluded.PauseNote,
                        UpdatedAtUtc = excluded.UpdatedAtUtc;
                    """;
                cmd.Parameters.AddWithValue("$name", taskName);
                cmd.Parameters.AddWithValue("$paused", paused ? 1 : 0);
                cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO PauseEvents (TaskName, Action, Note, PerformedAtUtc, PerformedBy)
                    VALUES ($name, $action, $note, $now, $by);
                    """;
                cmd.Parameters.AddWithValue("$name", taskName);
                cmd.Parameters.AddWithValue("$action", paused ? PauseActions.Paused : PauseActions.Resumed);
                cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$by", performedBy);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });

    /// <summary>Whether this replication is currently held. A task with no row has never been paused,
    /// which is not paused.</summary>
    public bool IsPaused(string taskName) => GetPauseState(taskName).Paused;

    /// <summary>The current hold and the note that came with it, in one read — what the status
    /// endpoint needs, and one query rather than two.</summary>
    public (bool Paused, string? Note) GetPauseState(string taskName) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Paused, PauseNote FROM Tasks WHERE Name = $name;";
            cmd.Parameters.AddWithValue("$name", taskName);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return (false, (string?)null);
            return (reader.GetInt64(0) != 0, reader.IsDBNull(1) ? null : reader.GetString(1));
        });

    /// <summary>
    /// Every pause and resume for a replication, most recent first.
    /// <para>
    /// Ordered by Id rather than by PerformedAtUtc: two actions within the same clock tick are
    /// otherwise in an arbitrary order, and the sequence is the whole point of an audit trail. The
    /// autoincrement is the only monotonic thing here.
    /// </para>
    /// <para>
    /// Nothing in the product calls this yet — the viewer is its own follow-up, see
    /// architecture/planning/todo/pause-history-ui.md. It exists so the history is reachable, and
    /// tested so it is reachable correctly.
    /// </para>
    /// </summary>
    public IReadOnlyList<PauseEventRecord> GetPauseHistory(string taskName, int limit = 50) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT Id, TaskName, Action, Note, PerformedAtUtc, PerformedBy
                FROM PauseEvents WHERE TaskName = $name
                ORDER BY Id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$name", taskName);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var results = new List<PauseEventRecord>();
            while (reader.Read())
                results.Add(new PauseEventRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4)),
                    reader.GetString(5)));
            return (IReadOnlyList<PauseEventRecord>)results;
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

    /// <param name="failureKind">
    /// Why it failed, when that is something the product can act on — see <see cref="RunFailureKinds"/>.
    /// Null for the ordinary case, which is nearly all of them.
    /// </param>
    /// <param name="timing">
    /// Per-stage timing, for a mapping that opted into tracing. Null leaves every timing column null
    /// rather than writing zeros — "not measured" and "measured as nothing" are different answers, and
    /// an aggregate over the column has to be able to tell them apart.
    /// </param>
    public void CompleteRun(
        Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary,
        string? failureKind = null, RunTiming? timing = null) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE TaskRuns
                SET Status = $status, EndedAtUtc = $endedAt, RowsRead = $rowsRead, RowsWritten = $rowsWritten,
                    ErrorSummary = $error, FailureKind = $failureKind,
                    ReaderKind = $readerKind, ReaderTimeToFirstRowMs = $timeToFirstRow,
                    ReaderLifetimeMs = $readerLifetime,
                    StagingKind = $stagingKind, StagingDurationMs = $stagingDuration,
                    WriterKind = $writerKind, WriterDurationMs = $writerDuration
                WHERE RunId = $runId;
                """;
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$endedAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$rowsRead", rowsRead);
            cmd.Parameters.AddWithValue("$rowsWritten", rowsWritten);
            cmd.Parameters.AddWithValue("$error", (object?)errorSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$failureKind", (object?)failureKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$readerKind", (object?)timing?.ReaderKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$timeToFirstRow", (object?)timing?.ReaderTimeToFirstRowMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$readerLifetime", (object?)timing?.ReaderLifetimeMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stagingKind", (object?)timing?.StagingKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stagingDuration", (object?)timing?.StagingDurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$writerKind", (object?)timing?.WriterKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$writerDuration", (object?)timing?.WriterDurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$runId", runId.ToString());
            cmd.ExecuteNonQuery();
        });

    public TaskRunRecord? GetRun(Guid runId) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary, FailureKind, ReaderKind, ReaderTimeToFirstRowMs, ReaderLifetimeMs, StagingKind, StagingDurationMs, WriterKind, WriterDurationMs
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
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary, FailureKind, ReaderKind, ReaderTimeToFirstRowMs, ReaderLifetimeMs, StagingKind, StagingDurationMs, WriterKind, WriterDurationMs
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
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary, FailureKind, ReaderKind, ReaderTimeToFirstRowMs, ReaderLifetimeMs, StagingKind, StagingDurationMs, WriterKind, WriterDurationMs
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
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary, FailureKind, ReaderKind, ReaderTimeToFirstRowMs, ReaderLifetimeMs, StagingKind, StagingDurationMs, WriterKind, WriterDurationMs
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
    /// per-run process spawn overhead) — see phase-008-work-queue-schema.md.</summary>
    public IReadOnlyList<TaskRunRecord> GetRecentlyEndedRuns(DateTimeOffset sinceUtc) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary, FailureKind, ReaderKind, ReaderTimeToFirstRowMs, ReaderLifetimeMs, StagingKind, StagingDurationMs, WriterKind, WriterDurationMs
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
    /// meaningful completion signal at that point). See phase-008-work-queue-schema.md.</summary>
    public IReadOnlyList<TaskRunRecord> GetActiveRuns() =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, ErrorSummary, FailureKind, ReaderKind, ReaderTimeToFirstRowMs, ReaderLifetimeMs, StagingKind, StagingDurationMs, WriterKind, WriterDurationMs
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

    /// <summary>
    /// Deletes finished runs beyond either retention cap, and the log lines belonging to them.
    /// Returns how many runs went.
    /// <para>
    /// **Both caps are applied in one statement, and a row failing either is pruned.** Two separate
    /// deletes would be two scans and — worse — would make "which cap removed this" a question with an
    /// answer, which invites somebody to depend on it. A row is either within retention or it is not.
    /// </para>
    /// <para>
    /// **A run that has not finished is never pruned**, whatever its age. `EndedAtUtc IS NULL` covers
    /// Queued, Running and anything stranded mid-flight: an in-flight run is about to be written to,
    /// and deleting the row underneath its own worker would turn a slow pass into a lost one.
    /// </para>
    /// </summary>
    /// <param name="maxPerMapping">
    /// Counted per (TaskName, MappingName), not globally. A continuous replication of one busy table
    /// produces runs orders of magnitude faster than a quiet mapping beside it, and a global cap would
    /// let the busy one evict the quiet one's entire history.
    /// </param>
    public int PruneRuns(TimeSpan? maxAge, int? maxPerMapping) =>
        SqliteRetry.Execute(() =>
        {
            if (maxAge is null && maxPerMapping is null)
                return 0;

            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            // Same transaction as the delete below, because Logs has no enforced foreign key here —
            // SQLite's are off unless asked for, and this schema does not. A crash between the two
            // statements would leave log lines belonging to a run that no longer exists, which nothing
            // would ever clean up.
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM Logs WHERE RunId IN ({DoomedRuns});";
                AddPruneParameters(cmd, maxAge, maxPerMapping);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM TaskRuns WHERE RunId IN ({DoomedRuns});";
                AddPruneParameters(cmd, maxAge, maxPerMapping);
                var deleted = cmd.ExecuteNonQuery();
                transaction.Commit();
                return deleted;
            }
        });

    /// <summary>
    /// The runs both caps condemn, as one subquery used by both deletes above.
    /// <para>
    /// A null cap is expressed as "$param IS NULL OR …" rather than by building different SQL: two
    /// statement shapes would be two things to keep correct, and this one is evaluated once per prune
    /// an hour rather than in any hot path.
    /// </para>
    /// </summary>
    private const string DoomedRuns = """
        SELECT RunId FROM (
            SELECT RunId, StartedAtUtc,
                   ROW_NUMBER() OVER (PARTITION BY TaskName, MappingName ORDER BY StartedAtUtc DESC) AS Recency
            FROM TaskRuns
            WHERE EndedAtUtc IS NOT NULL
        )
        WHERE ($cutoff IS NOT NULL AND StartedAtUtc < $cutoff)
           OR ($maxPerMapping IS NOT NULL AND Recency > $maxPerMapping)
        """;

    private static void AddPruneParameters(SqliteCommand cmd, TimeSpan? maxAge, int? maxPerMapping)
    {
        cmd.Parameters.AddWithValue(
            "$cutoff",
            maxAge is { } age ? (DateTimeOffset.UtcNow - age).ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$maxPerMapping", (object?)maxPerMapping ?? DBNull.Value);
    }

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
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        ReadTiming(reader));

    /// <summary>
    /// The timing columns as a record, or null when the run was never traced.
    /// <para>
    /// Null when *every* column is, rather than an all-null record: a caller asking "was this run
    /// traced" should get a yes or a no, not a record it has to interrogate field by field to find out.
    /// </para>
    /// </summary>
    private static RunTiming? ReadTiming(SqliteDataReader reader)
    {
        const int first = 13;
        var traced = false;
        for (var i = first; i < first + 7; i++)
            traced |= !reader.IsDBNull(i);

        if (!traced)
            return null;

        return new RunTiming(
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetInt64(14),
            reader.IsDBNull(15) ? null : reader.GetInt64(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetInt64(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetInt64(19));
    }
}
