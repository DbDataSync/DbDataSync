using System.Data.Common;

namespace DbDataSync.State;

public sealed class TaskRunStore(StateDatabase database)
{
    /// <summary>
    /// Every column <see cref="ReadRun"/> reads, in the order it reads them — named once because six
    /// queries select exactly this list and <see cref="ReadRun"/> addresses it by ordinal. Six copies
    /// were six chances for one of them to drift by a column and hand the reader the wrong field.
    /// <para>
    /// The three timestamps are listed in the order they happen (phase 73), which is also the order
    /// <c>TaskRunRecord</c> declares them.
    /// </para>
    /// </summary>
    private const string RunColumns =
        "RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, " +
        "EnqueuedAtUtc, ClaimedAtUtc, StartedAtUtc, EndedAtUtc, RowsRead, RowsWritten, " +
        "ErrorSummary, FailureKind, ReaderKind, ReaderTimeToFirstRowMs, ReaderLifetimeMs, " +
        "StagingKind, StagingDurationMs, WriterKind, WriterDurationMs, PreviousWatermark, NewWatermark, " +
        "ErrorDetail";

    /// <summary>Last Primary-run enqueue time per table mapping, in one query — what
    /// SchedulerService's due-ness check needs. One query per replication per tick, not one per
    /// mapping per tick: a replication can have hundreds of mappings, and N+1 queries at that scale
    /// on every 5-second tick would be the first thing to hurt.
    /// <para>
    /// The enqueue time, not the start (phase 73): due-ness asks "has it been N seconds since we last
    /// asked for this", and asking is the enqueue. Measuring from the start would let a run that
    /// queued behind a backlog push its own next run further out, so a busy replication would fall
    /// progressively further behind its own schedule.
    /// </para></summary>
    public IReadOnlyDictionary<string, DateTimeOffset> GetLastPrimaryEnqueueByMapping(string taskName) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                SELECT MappingName, MAX(EnqueuedAtUtc)
                FROM TaskRuns WHERE TaskName = $taskName AND RunKind = $runKind
                GROUP BY MappingName;
                """);
            cmd.Bind(database, "taskName", taskName);
            cmd.Bind(database, "runKind", RunKind.Primary.ToString());
            using var reader = cmd.ExecuteReader();
            var results = new Dictionary<string, DateTimeOffset>();
            while (reader.Read())
                results[reader.GetString(0)] = DateTimeOffset.Parse(reader.GetString(1));
            return (IReadOnlyDictionary<string, DateTimeOffset>)results;
        });

    public void UpsertTask(string taskName, bool enabled) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, database.Dialect.Upsert(
                "Tasks",
                "Name, Enabled, UpdatedAtUtc",
                "$name, $enabled, $now",
                "Name",
                "Enabled = EXCLUDED.Enabled, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc"));
            cmd.Bind(database, "name", taskName);
            cmd.Bind(database, "enabled", enabled ? 1 : 0);
            cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
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
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            // Read before the write, so the notification below can tell a replication being held from
            // one that already was — see NotifyIfNewlyPaused.
            var wasPaused = ReadPausedFlag(connection, transaction, taskName);

            using (var cmd = database.Command(connection, transaction, database.Dialect.Upsert(
                "Tasks",
                "Name, Enabled, Paused, PauseNote, UpdatedAtUtc",
                "$name, 1, $paused, $note, $now",
                "Name",
                "Paused = EXCLUDED.Paused, PauseNote = EXCLUDED.PauseNote, UpdatedAtUtc = EXCLUDED.UpdatedAtUtc")))
            {
                cmd.Bind(database, "name", taskName);
                cmd.Bind(database, "paused", paused ? 1 : 0);
                cmd.Bind(database, "note", (object?)note ?? DBNull.Value);
                cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            using (var cmd = database.Command(connection, transaction, """
                    INSERT INTO PauseEvents (TaskName, Action, Note, PerformedAtUtc, PerformedBy)
                    VALUES ($name, $action, $note, $now, $by);
                    """))
            {
                cmd.Bind(database, "name", taskName);
                cmd.Bind(database, "action", paused ? PauseActions.Paused : PauseActions.Resumed);
                cmd.Bind(database, "note", (object?)note ?? DBNull.Value);
                cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Bind(database, "by", performedBy);
                cmd.ExecuteNonQuery();
            }

            NotifyIfNewlyPaused(connection, transaction, taskName, paused, note, performedBy, wasPaused);

            transaction.Commit();
        });

    /// <summary>Whether this replication is currently held, read on a connection somebody else owns.
    /// A task with no row has never been paused, which is not paused.</summary>
    private bool ReadPausedFlag(DbConnection connection, DbTransaction transaction, string taskName)
    {
        using var cmd = database.Command(
            connection, transaction, "SELECT Paused FROM Tasks WHERE Name = $name;");
        cmd.Bind(database, "name", taskName);
        using var reader = cmd.ExecuteReader();
        return reader.Read() && reader.Int64(0) != 0;
    }

    /// <summary>
    /// The pause notification — phase 80.
    /// <para>
    /// **Pauses only, never resumes.** A hold is something people who were not in the room need to be
    /// told about: replication has stopped and will stay stopped until somebody acts. A resume is the
    /// world going back to how it is supposed to be, which nobody needs pushed at them, and notifying
    /// on both would double the volume of this kind to say nothing extra. Stated here rather than left
    /// as an unexplained <c>if</c>, because "why don't resumes notify" is otherwise a question with no
    /// answer in the code.
    /// </para>
    /// <para>
    /// **On the transition, matching the run-failure producer.** Pausing an already-paused replication
    /// is reachable — the endpoint does not short-circuit it, and it deliberately still writes its own
    /// <c>PauseEvents</c> row, because re-pausing with a new note is a real act somebody performed and
    /// the history is an audit trail. A notification is not an audit trail: "this replication has been
    /// paused" is not news a second time, and announcing it again would make a stuck retry look like a
    /// spreading outage.
    /// </para>
    /// <para>
    /// In the transaction that writes <c>Tasks</c> and <c>PauseEvents</c>, so all three commit or none
    /// do. A notification about a pause that did not happen is the worst of the three outcomes.
    /// </para>
    /// </summary>
    private void NotifyIfNewlyPaused(
        DbConnection connection, DbTransaction transaction, string taskName, bool paused,
        string? note, string performedBy, bool wasPaused)
    {
        if (!paused || wasPaused)
            return;

        var reason = string.IsNullOrWhiteSpace(note) ? "" : $" — {note}";

        NotificationStore.Insert(
            database, connection, transaction,
            NotificationKinds.ReplicationPaused,
            $"Replication '{taskName}' was paused by {performedBy}{reason}",
            taskName, mappingName: null, runId: null);
    }

    /// <summary>Whether this replication is currently held. A task with no row has never been paused,
    /// which is not paused.</summary>
    public bool IsPaused(string taskName) => GetPauseState(taskName).Paused;

    /// <summary>The current hold and the note that came with it, in one read — what the status
    /// endpoint needs, and one query rather than two.</summary>
    public (bool Paused, string? Note) GetPauseState(string taskName) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT Paused, PauseNote FROM Tasks WHERE Name = $name;");
            cmd.Bind(database, "name", taskName);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return (false, (string?)null);
            return (reader.Int64(0) != 0, reader.IsDBNull(1) ? null : reader.GetString(1));
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
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT Id, TaskName, Action, Note, PerformedAtUtc, PerformedBy
                FROM PauseEvents WHERE TaskName = $name
                ORDER BY Id DESC {database.Limit("limit")};
                """);
            cmd.Bind(database, "name", taskName);
            cmd.Bind(database, "limit", limit);
            using var reader = cmd.ExecuteReader();
            var results = new List<PauseEventRecord>();
            while (reader.Read())
                results.Add(new PauseEventRecord(
                    reader.Int64(0),
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
    /// to work on it.
    /// <para>
    /// This is where <c>StartedAtUtc</c> is written (phase 73), because this call *is* the moment the
    /// run starts — the enqueue and the claim already happened, and each wrote its own column. One
    /// statement sets it alongside <c>Status</c>, so a run cannot be <c>Running</c> without a start
    /// time or the reverse.
    /// </para>
    /// <para>
    /// Phase 72 wrote <c>ClaimedAtUtc</c> here instead, which was the wrong column for this moment: by
    /// the time a worker calls this it has held the claim since <c>WorkQueueStore.TryClaimNext</c>,
    /// which is now what writes that one.
    /// </para></summary>
    public void BeginRun(Guid runId, int? pid) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "UPDATE TaskRuns SET Status = $status, Pid = $pid, StartedAtUtc = $startedAt WHERE RunId = $runId;");
            cmd.Bind(database, "status", RunStatus.Running.ToString());
            cmd.Bind(database, "pid", (object?)pid ?? DBNull.Value);
            cmd.Bind(database, "startedAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Bind(database, "runId", runId.ToString());
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
    /// <param name="previousWatermark">
    /// Where this pass started from, and where it left off — both null unless the run actually made a
    /// new position durable (phase 71). A failed run passes null for both even when it had computed a
    /// position before failing: it never committed one, and recording it here would claim a history
    /// that did not happen.
    /// </param>
    /// <param name="newWatermark">See <paramref name="previousWatermark"/>.</param>
    /// <param name="errorDetail">The full exception — type, message, stack trace, inner exceptions —
    /// for the Runs tab's failure popup. Null on success, and null on a failure path that has no
    /// exception object to hand it (a supervisor-recorded cancellation or orphan, not one raised by
    /// the work itself), in which case the popup falls back to <paramref name="errorSummary"/>.</param>
    public void CompleteRun(
        Guid runId, RunStatus status, long rowsRead, long rowsWritten, string? errorSummary,
        string? failureKind = null, RunTiming? timing = null,
        string? previousWatermark = null, string? newWatermark = null, string? errorDetail = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            // Read before the write, so "was this run already Failed" is answerable — see
            // NotifyIfNewlyFailed. In one transaction with the update because a notification that
            // could commit without the failure it announces, or a failure without it, is the pair
            // phase 77 exists to keep together.
            var before = ReadOutcomeContext(connection, transaction, runId);

            using var cmd = database.Command(connection, transaction, """
                UPDATE TaskRuns
                SET Status = $status, EndedAtUtc = $endedAt, RowsRead = $rowsRead, RowsWritten = $rowsWritten,
                    ErrorSummary = $error, FailureKind = $failureKind,
                    ReaderKind = $readerKind, ReaderTimeToFirstRowMs = $timeToFirstRow,
                    ReaderLifetimeMs = $readerLifetime,
                    StagingKind = $stagingKind, StagingDurationMs = $stagingDuration,
                    WriterKind = $writerKind, WriterDurationMs = $writerDuration,
                    PreviousWatermark = $previousWatermark, NewWatermark = $newWatermark,
                    ErrorDetail = $errorDetail
                WHERE RunId = $runId;
                """);
            cmd.Bind(database, "status", status.ToString());
            cmd.Bind(database, "endedAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Bind(database, "rowsRead", rowsRead);
            cmd.Bind(database, "rowsWritten", rowsWritten);
            cmd.Bind(database, "error", (object?)errorSummary ?? DBNull.Value);
            cmd.Bind(database, "failureKind", (object?)failureKind ?? DBNull.Value);
            cmd.Bind(database, "readerKind", (object?)timing?.ReaderKind ?? DBNull.Value);
            cmd.Bind(database, "timeToFirstRow", (object?)timing?.ReaderTimeToFirstRowMs ?? DBNull.Value);
            cmd.Bind(database, "readerLifetime", (object?)timing?.ReaderLifetimeMs ?? DBNull.Value);
            cmd.Bind(database, "stagingKind", (object?)timing?.StagingKind ?? DBNull.Value);
            cmd.Bind(database, "stagingDuration", (object?)timing?.StagingDurationMs ?? DBNull.Value);
            cmd.Bind(database, "writerKind", (object?)timing?.WriterKind ?? DBNull.Value);
            cmd.Bind(database, "writerDuration", (object?)timing?.WriterDurationMs ?? DBNull.Value);
            cmd.Bind(database, "previousWatermark", (object?)previousWatermark ?? DBNull.Value);
            cmd.Bind(database, "newWatermark", (object?)newWatermark ?? DBNull.Value);
            cmd.Bind(database, "errorDetail", (object?)errorDetail ?? DBNull.Value);
            cmd.Bind(database, "runId", runId.ToString());
            cmd.ExecuteNonQuery();

            NotifyIfNewlyFailed(connection, transaction, runId, status, errorSummary, failureKind, before);

            transaction.Commit();
        });

    /// <summary>What a run was before it was completed — enough to notify about it, and enough to
    /// tell a first completion from a repeat of one. Null where the run has no row, which is not a
    /// case worth failing a completion over but is one worth not announcing.</summary>
    private (string TaskName, string? MappingName, string Status)? ReadOutcomeContext(
        DbConnection connection, DbTransaction transaction, Guid runId)
    {
        using var cmd = database.Command(connection, transaction,
            "SELECT TaskName, MappingName, Status FROM TaskRuns WHERE RunId = $runId;");
        cmd.Bind(database, "runId", runId.ToString());
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2))
            : null;
    }

    /// <summary>
    /// The run-failure notification — phase 77's producer, and phase 80's watermark-expiry one.
    /// <para>
    /// **Here rather than at the six call sites that complete runs.** A worker completes a run through
    /// the state channel, a locally-hosted runner completes one directly, the supervisor completes
    /// orphans and stopped processes on its own, and journal recovery replays completions that a
    /// worker could not deliver at the time. Every one of those is a failure somebody should be told
    /// about, and wiring the producer to each would be five chances to miss one and a sixth waiting in
    /// the next phase that adds a completion path.
    /// </para>
    /// <para>
    /// **On the transition, not on the write.** A completion can legitimately arrive twice — journal
    /// recovery replaying an entry whose original write did land, or the supervisor reaping a process
    /// whose own failure report was already in flight — and a second announcement of the same failure
    /// is noise a reader cannot distinguish from a second failure. A run that is already Failed
    /// produces nothing.
    /// </para>
    /// <para>
    /// **A position expiry is its own kind**, not a RunFailed row whose message happens to say so. It
    /// is the one failure with a known one-click fix — the Runs tab already offers the reload off the
    /// same <c>FailureKind</c> — and a feed should be able to find those without matching on prose.
    /// Its message carries <c>PositionExpiredException</c>'s own words, which already name the
    /// mechanism, the table, the position that expired and the oldest one still available, beside the
    /// mapping this row identifies. Specific by construction rather than by reassembling four fields
    /// into a worse sentence than the exception already wrote.
    /// </para>
    /// <para>
    /// **A missing metadata cache (phase 91) is its own kind for the same reason.** Its fix is Refresh
    /// metadata rather than a reload, so folding it into <see cref="RunFailureKinds.PositionExpired"/>
    /// would point an operator at the wrong button, and folding it into the generic
    /// <see cref="NotificationKinds.RunFailed"/> would hide a failure this specific behind "read the
    /// logs". <c>MetadataNotCachedException</c>'s own message already names the mapping, the side and
    /// the column, so this branch reassembles nothing either.
    /// </para>
    /// </summary>
    private void NotifyIfNewlyFailed(
        DbConnection connection, DbTransaction transaction, Guid runId, RunStatus status,
        string? errorSummary, string? failureKind,
        (string TaskName, string? MappingName, string Status)? before)
    {
        if (status != RunStatus.Failed || before is not { } run)
            return;

        if (string.Equals(run.Status, nameof(RunStatus.Failed), StringComparison.Ordinal))
            return;

        var subject = run.MappingName is { } mapping
            ? $"'{run.TaskName}' (mapping '{mapping}')"
            : $"'{run.TaskName}'";

        var (kind, message) = failureKind switch
        {
            RunFailureKinds.PositionExpired =>
                (NotificationKinds.PositionExpired, $"Source position for {subject} has expired. {errorSummary}"),
            RunFailureKinds.MetadataNotCached =>
                (NotificationKinds.MetadataNotCached, $"Cached metadata for {subject} is missing. {errorSummary}"),
            _ => (NotificationKinds.RunFailed, $"Run of {subject} failed: {errorSummary ?? "no error was recorded."}"),
        };

        NotificationStore.Insert(
            database, connection, transaction, kind, message, run.TaskName, run.MappingName, runId);
    }

    public TaskRunRecord? GetRun(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT {RunColumns}
                FROM TaskRuns WHERE RunId = $runId;
                """);
            cmd.Bind(database, "runId", runId.ToString());
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRun(reader) : null;
        });

    /// <summary>Run history for a task, optionally filtered to one RunKind. A null runKind (the
    /// default) mixes Primary and Backfill rows — what the SPA's run-history view wants; scheduling
    /// due-ness checks must always pass RunKind.Primary explicitly so a Backfill run never perturbs
    /// the incremental schedule's timing.</summary>
    public IReadOnlyList<TaskRunRecord> GetRunHistory(string taskName, RunKind? runKind = null, int limit = 50) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT {RunColumns}
                FROM TaskRuns WHERE TaskName = $taskName {(runKind is null ? "" : "AND RunKind = $runKind")}
                ORDER BY EnqueuedAtUtc DESC {database.Limit("limit")};
                """);
            cmd.Bind(database, "taskName", taskName);
            if (runKind is not null)
                cmd.Bind(database, "runKind", runKind.Value.ToString());
            cmd.Bind(database, "limit", limit);
            using var reader = cmd.ExecuteReader();
            var results = new List<TaskRunRecord>();
            while (reader.Read())
                results.Add(ReadRun(reader));
            return (IReadOnlyList<TaskRunRecord>)results;
        });

    /// <summary>Run history for one specific table mapping (either RunKind) — used by scheduling
    /// due-ness (RunKind.Primary) and by the SPA's per-mapping history views.</summary>
    public IReadOnlyList<TaskRunRecord> GetMappingRunHistory(string taskName, RunKind runKind, string mappingName, int limit = 50) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT {RunColumns}
                FROM TaskRuns WHERE TaskName = $taskName AND RunKind = $runKind AND MappingName = $mapping
                ORDER BY EnqueuedAtUtc DESC {database.Limit("limit")};
                """);
            cmd.Bind(database, "taskName", taskName);
            cmd.Bind(database, "runKind", runKind.ToString());
            cmd.Bind(database, "mapping", mappingName);
            cmd.Bind(database, "limit", limit);
            using var reader = cmd.ExecuteReader();
            var results = new List<TaskRunRecord>();
            while (reader.Read())
                results.Add(ReadRun(reader));
            return (IReadOnlyList<TaskRunRecord>)results;
        });

    /// <summary>Runs still marked Running in the store — used to reconcile against live OS processes
    /// on API startup (architecture/detailed-design.md §3.1).</summary>
    public IReadOnlyList<TaskRunRecord> GetRunningRuns() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT {RunColumns}
                FROM TaskRuns WHERE Status = $status;
                """);
            cmd.Bind(database, "status", RunStatus.Running.ToString());
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
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT {RunColumns}
                FROM TaskRuns WHERE EndedAtUtc IS NOT NULL AND EndedAtUtc >= $since;
                """);
            cmd.Bind(database, "since", sinceUtc.ToString("O"));
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
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT {RunColumns}
                FROM TaskRuns WHERE Status IN ($queued, $running);
                """);
            cmd.Bind(database, "queued", RunStatus.Queued.ToString());
            cmd.Bind(database, "running", RunStatus.Running.ToString());
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
        database.Retry(() =>
        {
            if (maxAge is null && maxPerMapping is null)
                return 0;

            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            // Same transaction as the delete below, because Logs has no enforced foreign key here —
            // SQLite's are off unless asked for, and this schema does not. A crash between the two
            // statements would leave log lines belonging to a run that no longer exists, which nothing
            // would ever clean up.
            using (var cmd = database.Command(connection, transaction, $"DELETE FROM Logs WHERE RunId IN ({DoomedRuns});"))
            {
                AddPruneParameters(cmd, maxAge, maxPerMapping);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = database.Command(connection, transaction, $"DELETE FROM TaskRuns WHERE RunId IN ({DoomedRuns});"))
            {
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
            SELECT RunId, EnqueuedAtUtc,
                   ROW_NUMBER() OVER (PARTITION BY TaskName, MappingName ORDER BY EnqueuedAtUtc DESC) AS Recency
            FROM TaskRuns
            WHERE EndedAtUtc IS NOT NULL
        ) AS Ranked
        WHERE ($cutoff IS NOT NULL AND EnqueuedAtUtc < $cutoff)
           OR ($maxPerMapping IS NOT NULL AND Recency > $maxPerMapping)
        """;

    private void AddPruneParameters(DbCommand cmd, TimeSpan? maxAge, int? maxPerMapping)
    {
        // Typed explicitly, because both are null on the ordinary path and both are used only in an
        // `IS NOT NULL` test — from which Postgres cannot infer a type and refuses to plan the
        // statement at all. The other two engines do not care, and giving them the type costs nothing.
        cmd.Bind(
            database, "cutoff",
            maxAge is { } age ? (DateTimeOffset.UtcNow - age).ToString("O") : null,
            System.Data.DbType.String);
        cmd.Bind(database, "maxPerMapping", maxPerMapping, System.Data.DbType.Int32);
    }

    private static TaskRunRecord ReadRun(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.Int32(2),
        Enum.Parse<RunStatus>(reader.GetString(3)),
        Enum.Parse<RunKind>(reader.GetString(4)),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
        reader.Int64(11),
        reader.Int64(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        ReadTiming(reader),
        reader.IsDBNull(22) ? null : reader.GetString(22),
        reader.IsDBNull(23) ? null : reader.GetString(23),
        reader.IsDBNull(24) ? null : reader.GetString(24));

    /// <summary>
    /// The timing columns as a record, or null when the run was never traced.
    /// <para>
    /// Null when *every* column is, rather than an all-null record: a caller asking "was this run
    /// traced" should get a yes or a no, not a record it has to interrogate field by field to find out.
    /// </para>
    /// </summary>
    private static RunTiming? ReadTiming(DbDataReader reader)
    {
        const int first = 15;
        var traced = false;
        for (var i = first; i < first + 7; i++)
            traced |= !reader.IsDBNull(i);

        if (!traced)
            return null;

        return new RunTiming(
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.Int64(16),
            reader.IsDBNull(17) ? null : reader.Int64(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.Int64(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.Int64(21));
    }
}
