using System.Data.Common;

namespace DbDataSync.State;

public enum WorkItemStatus
{
    Pending,
    Claimed,
    Running,
    Done,
    Failed,
    Cancelled,
}

public sealed record WorkItem(
    long Id,
    string TaskName,
    RunKind RunKind,
    string MappingName,
    string SegmentLabel,
    string? SegmentJson,
    Guid RunId,
    WorkItemStatus Status,
    WorkItemKinds Kinds,
    /// <summary>Phase 124: a serialized <c>DeleteGuard</c> override for this
    /// <see cref="RunKind.ReconcileDeletes"/> item — null for every other kind, and null for a
    /// reconcile item that didn't override the writer's configured/default guard.</summary>
    string? DeleteGuardJson = null);

/// <summary>
/// Which reader/cache/writer this unit of work should use, when that isn't simply the replication's
/// configured pipeline. Every component is null for a Primary pass — an incremental replication runs
/// the pipeline it was configured with. A BulkLoad sets them, because reloading a segment needs a
/// reload reader and (usually) a reconciling writer regardless of what the replication does for its
/// ongoing sync.
/// </summary>
public sealed record WorkItemKinds(string? ReaderKind = null, string? CacheKind = null, string? WriterKind = null)
{
    /// <summary>Use the replication's own configured pipeline.</summary>
    public static WorkItemKinds FromConfig { get; } = new();
}

/// <summary>
/// Durable, SQLite-backed cross-process work queue (architecture/implementation/done/phase-008-work-queue-schema.md).
/// The API process (handling bulk load triggers and scheduled due-ness) and the TaskRunner worker
/// process(es) it spawns communicate exclusively through DbDataSync.State — this table is the mechanism,
/// not an in-memory queue, since the API can't reach into a separate OS process directly.
/// </summary>
public sealed class WorkQueueStore(StateDatabase database)
{
    /// <summary>
    /// What "in flight" means, as the partial unique index UX_WorkQueue_InFlight defines it.
    /// <para>
    /// Written once and shared with the enqueue that relies on it, because the two have to agree
    /// exactly: the index decides which insert collides, and this decides which insert expects to.
    /// </para>
    /// </summary>
    private const string InFlightStatuses = "Status IN ('Pending','Claimed','Running')";

    /// <summary>Sentinel for WorkQueue.SegmentLabel when a unit of work has no segment (every Primary
    /// item, and a BulkLoad's single Full-mode segment) — NOT NULL because this column participates in
    /// a uniqueness constraint, where SQL's every-NULL-is-distinct rule would silently defeat it.</summary>
    public const string NoSegment = "";

    /// <summary>
    /// Enqueues one unit of work and its corresponding Queued TaskRuns row in one transaction, so a
    /// queued backlog is visible in run history immediately, not only once a worker claims it. A
    /// no-op (no new row, existing RunId returned) if an equivalent item is already
    /// Pending/Claimed/Running for this (task, kind, mapping, segment) — relies on the same
    /// ON CONFLICT DO NOTHING idiom RunLockStore.TryAcquire already uses.
    /// <para>
    /// This silent-collapse behaviour is correct when two callers' requests really are the same thing —
    /// two identical reload clicks should collapse into one real reload, not run it twice. See
    /// <see cref="EnqueueOrThrow"/> for the callers where that isn't true.
    /// </para>
    /// </summary>
    public Guid Enqueue(
        string taskName,
        RunKind runKind,
        string mappingName,
        string segmentLabel = NoSegment,
        string? segmentJson = null,
        WorkItemKinds? kinds = null,
        string? bulkLoadBatchId = null,
        // Phase 124: a serialized DeleteGuard override — see WorkItem.DeleteGuardJson.
        string? deleteGuardJson = null) =>
        EnqueueCore(
            taskName, runKind, mappingName, segmentLabel, segmentJson, kinds, bulkLoadBatchId,
            deleteGuardJson, throwOnCollision: false);

    /// <summary>
    /// Like <see cref="Enqueue"/>, except a collision with other in-flight work for this exact (task,
    /// kind, mapping, segment) throws <see cref="WorkQueueCollisionException"/> instead of silently
    /// returning the existing item's RunId.
    /// <para>
    /// For a caller whose request is genuinely independent of whoever else might be enqueuing the same
    /// segment — currently only <c>LocalRunnerState.RequestInitialLoad</c>'s auto-triggered initial
    /// load, which must not be silently attached to an unrelated, differently-scoped, differently-timed
    /// reload that happens to be racing it (phase 143). Every other caller keeps using
    /// <see cref="Enqueue"/>.
    /// </para>
    /// </summary>
    public Guid EnqueueOrThrow(
        string taskName,
        RunKind runKind,
        string mappingName,
        string segmentLabel = NoSegment,
        string? segmentJson = null,
        WorkItemKinds? kinds = null,
        string? bulkLoadBatchId = null,
        string? deleteGuardJson = null) =>
        EnqueueCore(
            taskName, runKind, mappingName, segmentLabel, segmentJson, kinds, bulkLoadBatchId,
            deleteGuardJson, throwOnCollision: true);

    private Guid EnqueueCore(
        string taskName,
        RunKind runKind,
        string mappingName,
        string segmentLabel,
        string? segmentJson,
        WorkItemKinds? kinds,
        string? bulkLoadBatchId,
        string? deleteGuardJson,
        bool throwOnCollision) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            var runId = Guid.NewGuid();
            using (var cmd = database.Command(connection, transaction, database.Dialect.InsertOrIgnore(
                "WorkQueue",
                "TaskName, RunKind, MappingName, SegmentLabel, SegmentJson, RunId, Status, " +
                    "EnqueuedAtUtc, AvailableAtUtc, ReaderKind, CacheKind, WriterKind, DeleteGuardJson",
                "$task, $kind, $mapping, $segment, $segmentJson, $runId, $status, $now, $now, " +
                    "$readerKind, $cacheKind, $writerKind, $deleteGuardJson",
                "TaskName, RunKind, MappingName, SegmentLabel",
                InFlightStatuses)))
            {
                // The uniqueness relied on is UX_WorkQueue_InFlight, which is *partial* — one row per
                // mapping among the in-flight statuses, not one row ever. The predicate has to be
                // stated, and not only for syntax: without it this reads as "one row per mapping for
                // all time", and a mapping that had ever run could never be queued again.
                cmd.Bind(database, "task", taskName);
                cmd.Bind(database, "kind", runKind.ToString());
                cmd.Bind(database, "mapping", mappingName);
                cmd.Bind(database, "segment", segmentLabel);
                cmd.Bind(database, "segmentJson", (object?)segmentJson ?? DBNull.Value);
                cmd.Bind(database, "runId", runId.ToString());
                cmd.Bind(database, "status", WorkItemStatus.Pending.ToString());
                cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Bind(database, "readerKind", (object?)kinds?.ReaderKind ?? DBNull.Value);
                cmd.Bind(database, "cacheKind", (object?)kinds?.CacheKind ?? DBNull.Value);
                cmd.Bind(database, "writerKind", (object?)kinds?.WriterKind ?? DBNull.Value);
                cmd.Bind(database, "deleteGuardJson", (object?)deleteGuardJson ?? DBNull.Value);
                var inserted = cmd.ExecuteNonQuery() == 1;

                if (!inserted)
                {
                    // Already queued/in-flight — return the existing item's RunId instead of minting
                    // an orphaned TaskRuns row for a queue row that will never exist. Unless this
                    // caller's request isn't the same thing as whatever's already in flight, in which
                    // case attaching to it silently would be wrong — see EnqueueOrThrow's own doc.
                    transaction.Rollback();
                    if (throwOnCollision)
                        throw new WorkQueueCollisionException(taskName, runKind, mappingName, segmentLabel);
                    return GetExistingRunId(connection, taskName, runKind, mappingName, segmentLabel);
                }
            }

            using (var cmd = database.Command(connection, transaction, """
                    INSERT INTO TaskRuns (RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, EnqueuedAtUtc, RowsRead, RowsWritten, BulkLoadBatchId)
                    VALUES ($runId, $taskName, NULL, $status, $runKind, $mapping, $segment, $enqueuedAt, 0, 0, $batchId);
                    """))
            {
                cmd.Bind(database, "runId", runId.ToString());
                cmd.Bind(database, "taskName", taskName);
                cmd.Bind(database, "status", RunStatus.Queued.ToString());
                cmd.Bind(database, "runKind", runKind.ToString());
                cmd.Bind(database, "mapping", mappingName);
                cmd.Bind(database, "segment", segmentLabel == NoSegment ? (object)DBNull.Value : segmentLabel);
                cmd.Bind(database, "batchId", (object?)bulkLoadBatchId ?? DBNull.Value);
                // EnqueuedAtUtc, and nothing else: a queued run has not been claimed and has not
                // started, so ClaimedAtUtc and StartedAtUtc stay null until the moments they name
                // actually happen (phase 73). Before that, this wrote the enqueue time into
                // StartedAtUtc, which is how every "duration" in the product came to include the queue.
                cmd.Bind(database, "enqueuedAt", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            return runId;
        });

    private Guid GetExistingRunId(DbConnection connection, string taskName, RunKind runKind, string mappingName, string segmentLabel)
    {
        using var cmd = database.Command(connection, """
            SELECT RunId FROM WorkQueue
            WHERE TaskName = $task AND RunKind = $kind AND MappingName = $mapping AND SegmentLabel = $segment
                AND Status IN ('Pending','Claimed','Running');
            """);
        cmd.Bind(database, "task", taskName);
        cmd.Bind(database, "kind", runKind.ToString());
        cmd.Bind(database, "mapping", mappingName);
        cmd.Bind(database, "segment", segmentLabel);
        return Guid.Parse((string)cmd.ExecuteScalar()!);
    }

    /// <summary>Claims the next available Pending item for a task, if any — two-step select-then-
    /// conditional-update so a lost race (another consumer claimed it first) is detected via the
    /// affected-rows check, not a lock. The NOT EXISTS clause skips any mapping that already has
    /// another Claimed/Running item, so a claim never has to be given back for losing a RunLocks race
    /// in the common case (see RunLockStore — this is a fast pre-filter, not a substitute for it).
    /// <para>
    /// The claim itself writes two rows in one transaction: the queue item, and <c>ClaimedAtUtc</c> on
    /// the run it belongs to (phase 73). This is the genuine claim moment — <c>TaskRunStore.BeginRun</c>
    /// happens later, once the worker is actually starting the work, and writes <c>StartedAtUtc</c>.
    /// </para></summary>
    /// <param name="lane">Restrict the claim to one lane's <see cref="RunKind"/>s. Null claims from
    /// any lane — never what the worker wants (it drives each lane's channel from its own claim loop),
    /// but what a reconciliation check or a test that is not about the split means.</param>
    /// <param name="runId">Test-only: claim a specific run rather than whatever is next for the task.
    /// Production never passes this — a worker wants the queue's own priority order, not one run it
    /// already knows about. It exists so a test can state "the row I just enqueued" as an intent instead
    /// of inferring it from being the only thing in the queue, which silently stops being true the moment
    /// anything else (a scheduler tick, another test) enqueues a competing row for the same task. See
    /// architecture/planning/todo/follow-up-runwatermarktimetests-claims-the-wrong-row-again-via-the-real-scheduler.md.</param>
    public WorkItem? TryClaimNext(string taskName, string workerId, RunLane? lane = null, Guid? runId = null)
    {
        var laneClause = lane is { } l ? $"AND RunKind IN {RunKindsIn(l)}" : "";
        var runIdClause = runId is not null ? "AND RunId = $runId" : "";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(connection, $"""
                    SELECT Id, TaskName, RunKind, MappingName, SegmentLabel, SegmentJson, RunId, Status,
                           ReaderKind, CacheKind, WriterKind, DeleteGuardJson
                    FROM WorkQueue w
                    WHERE TaskName = $task AND Status = 'Pending' AND AvailableAtUtc <= $now
                      {laneClause}
                      {runIdClause}
                      AND NOT EXISTS (
                        SELECT 1 FROM WorkQueue w2
                        WHERE w2.TaskName = w.TaskName AND w2.RunKind = w.RunKind AND w2.MappingName = w.MappingName
                          AND w2.Status IN ('Claimed','Running') AND w2.Id <> w.Id)
                    ORDER BY Priority DESC, EnqueuedAtUtc ASC, Id ASC {database.Limit("take")};
                    """);
                cmd.Bind(database, "task", taskName);
                cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
                if (runId is { } id)
                    cmd.Bind(database, "runId", id.ToString());
                cmd.Bind(database, "take", 1);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadItem(reader) : null;
            });

            if (candidate is null)
                return null;

            var claimed = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var transaction = connection.BeginTransaction();

                // One claim moment, written to both tables from one variable — not two calls to
                // UtcNow, which would disagree by however long the round trip took and make
                // WorkQueue.ClaimedAtUtc and TaskRuns.ClaimedAtUtc two slightly different answers to
                // the same question.
                var now = DateTimeOffset.UtcNow.ToString("O");

                using (var cmd = database.Command(connection, transaction, """
                    UPDATE WorkQueue SET Status = 'Claimed', ClaimedAtUtc = $now, ClaimedByWorkerId = $worker
                    WHERE Id = $id AND Status = 'Pending';
                    """))
                {
                    cmd.Bind(database, "now", now);
                    cmd.Bind(database, "worker", workerId);
                    cmd.Bind(database, "id", candidate.Id);
                    if (cmd.ExecuteNonQuery() != 1)
                    {
                        // Lost the race. Rolled back rather than left open, so the TaskRuns row of an
                        // item somebody else claimed is never stamped with this worker's clock.
                        transaction.Rollback();
                        return false;
                    }
                }

                // In the same transaction as the claim above, and that is the point of the
                // transaction (phase 73): a run must never be Claimed in WorkQueue with no claim time
                // in TaskRuns, which is exactly what two independent statements would allow on a
                // crash between them.
                using (var cmd = database.Command(
                    connection, transaction, "UPDATE TaskRuns SET ClaimedAtUtc = $now WHERE RunId = $runId;"))
                {
                    cmd.Bind(database, "now", now);
                    cmd.Bind(database, "runId", candidate.RunId.ToString());
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                return true;
            });

            if (claimed)
                return candidate with { Status = WorkItemStatus.Claimed };

            // Lost the race to another consumer — try again for a different candidate.
        }

        return null;
    }

    public void MarkRunning(long id) => SetStatus(id, WorkItemStatus.Running);

    public void MarkDone(long id) => SetStatus(id, WorkItemStatus.Done);

    /// <summary>Gives up a claim without executing it — used when a claimed item then loses the real
    /// RunLocks race (rare: the queue's own NOT EXISTS pre-filter already screens out the common case).
    /// Back to Pending, not Failed — this wasn't an execution failure, just a lost race to retry.</summary>
    public void ReleaseClaim(long id) => SetStatus(id, WorkItemStatus.Pending);

    public void MarkFailed(long id) => SetStatus(id, WorkItemStatus.Failed);

    /// <summary>
    /// Returns every in-flight item of a replication to the queue. For when the worker holding them
    /// died without saying anything — a kill, a crash, an API restart that found no live process.
    /// <para>
    /// Pending rather than Failed, for the same reason <see cref="ReleaseClaim"/> is: nobody observed
    /// an execution failure, only that the process holding the work is gone. And it has to happen at
    /// all, because UX_WorkQueue_InFlight covers Claimed and Running — an item left in either state
    /// makes its mapping permanently un-enqueueable, so the replication silently stops rather than
    /// failing.
    /// </para>
    /// <para>
    /// Per replication rather than per run, because a worker claims ahead of its consumers and can die
    /// holding items it never started, which have no started run to be found by. Only correct once the
    /// caller knows the worker is gone — a live one legitimately holds unstarted claims.
    /// </para>
    /// </summary>
    /// <summary>Replications holding Claimed or Running items. What reconciliation needs to ask, because
    /// an item claimed by a worker that died before starting it has no run to be found by.</summary>
    public IReadOnlyList<string> GetTasksWithInFlightWork() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT DISTINCT TaskName FROM WorkQueue WHERE Status IN ('Claimed','Running');";
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return (IReadOnlyList<string>)names;
        });

    public int ReleaseClaimsForTask(string taskName) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                UPDATE WorkQueue
                SET Status = 'Pending', ClaimedAtUtc = NULL, ClaimedByWorkerId = NULL
                WHERE TaskName = $task AND Status IN ('Claimed','Running');
                """);
            cmd.Bind(database, "task", taskName);
            return cmd.ExecuteNonQuery();
        });

    public int ReleaseClaimsForRun(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                UPDATE WorkQueue
                SET Status = 'Pending', ClaimedAtUtc = NULL, ClaimedByWorkerId = NULL
                WHERE RunId = $runId AND Status IN ('Claimed','Running');
                """);
            cmd.Bind(database, "runId", runId.ToString());
            return cmd.ExecuteNonQuery();
        });

    /// <summary>Cancels a not-yet-claimed item directly — no process interaction needed. An
    /// already-Claimed/Running item can't be cancelled this way in v1 (see ProcessSupervisor.CancelRun's
    /// fallback to killing the whole worker process — an accepted, documented v1 limitation).</summary>
    public bool TryCancelPending(Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "UPDATE WorkQueue SET Status = 'Cancelled' WHERE RunId = $runId AND Status = 'Pending';");
            cmd.Bind(database, "runId", runId.ToString());
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>True if any Pending/Claimed/Running item remains for this task. With
    /// <paramref name="lane"/> set, only that lane's <see cref="RunKind"/>s count — a worker's
    /// per-lane drain loop exits once its lane is empty, and never waits on the other lane's work.
    /// Null means "anything at all", which is what reconciliation asks.</summary>
    public bool HasOutstandingWork(string taskName, RunLane? lane = null) =>
        database.Retry(() =>
        {
            var laneClause = lane is { } l ? $"AND RunKind IN {RunKindsIn(l)}" : "";
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT COUNT(*) FROM WorkQueue
                WHERE TaskName = $task AND Status IN ('Pending','Claimed','Running') {laneClause};
                """);
            cmd.Bind(database, "task", taskName);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    /// <summary>Whether this mapping already has a <see cref="RunKind.ReconcileDeletes"/> item
    /// in flight — phase 125's scheduler dedup, mapping-scoped rather than lane-scoped like
    /// <see cref="HasOutstandingWork"/> (which cannot distinguish "some other mapping's bulk load is
    /// running" from "this mapping's own sweep already is"). A dedicated method rather than reusing
    /// <see cref="HasOutstandingWork"/>: the scheduler needs to know about *this* mapping specifically,
    /// and stretching that method with an optional mapping parameter would make its one existing
    /// caller (the worker's per-lane drain loop) carry a parameter it never uses.</summary>
    public bool HasPendingReconcile(string taskName, string mappingName) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"""
                SELECT COUNT(*) FROM WorkQueue
                WHERE TaskName = $task AND RunKind = $kind AND MappingName = $mapping AND {InFlightStatuses};
                """);
            cmd.Bind(database, "task", taskName);
            cmd.Bind(database, "kind", RunKind.ReconcileDeletes.ToString());
            cmd.Bind(database, "mapping", mappingName);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    /// <summary>The <c>RunKind IN (…)</c> fragment for a lane — from <see cref="RunLanes.KindsFor"/>,
    /// which is the single definition of which kinds a lane owns. Enum names, never user input, so
    /// inlining them is safe and keeps the claim query one string.</summary>
    private static string RunKindsIn(RunLane lane) =>
        "(" + string.Join(", ", RunLanes.KindsFor(lane).Select(k => $"'{k}'")) + ")";

    private void SetStatus(long id, WorkItemStatus status) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "UPDATE WorkQueue SET Status = $status WHERE Id = $id;");
            cmd.Bind(database, "status", status.ToString());
            cmd.Bind(database, "id", id);
            cmd.ExecuteNonQuery();
        });

    private static WorkItem ReadItem(DbDataReader reader) => new(
        reader.Int64(0),
        reader.GetString(1),
        Enum.Parse<RunKind>(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        Guid.Parse(reader.GetString(6)),
        Enum.Parse<WorkItemStatus>(reader.GetString(7)),
        new WorkItemKinds(
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10)),
        reader.IsDBNull(11) ? null : reader.GetString(11));
}
