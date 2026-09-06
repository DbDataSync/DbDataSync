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
    WorkItemKinds Kinds);

/// <summary>
/// Which reader/cache/writer this unit of work should use, when that isn't simply the replication's
/// configured pipeline. Every component is null for a Primary pass — an incremental replication runs
/// the pipeline it was configured with. A Backfill sets them, because reloading a segment needs a
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
/// The API process (handling backfill triggers and scheduled due-ness) and the TaskRunner worker
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
    /// item, and a Backfill's single Full-mode segment) — NOT NULL because this column participates in
    /// a uniqueness constraint, where SQL's every-NULL-is-distinct rule would silently defeat it.</summary>
    public const string NoSegment = "";

    /// <summary>
    /// Enqueues one unit of work and its corresponding Queued TaskRuns row in one transaction, so a
    /// queued backlog is visible in run history immediately, not only once a worker claims it. A
    /// no-op (no new row, existing RunId returned) if an equivalent item is already
    /// Pending/Claimed/Running for this (task, kind, mapping, segment) — relies on the same
    /// ON CONFLICT DO NOTHING idiom RunLockStore.TryAcquire already uses.
    /// </summary>
    public Guid Enqueue(
        string taskName,
        RunKind runKind,
        string mappingName,
        string segmentLabel = NoSegment,
        string? segmentJson = null,
        WorkItemKinds? kinds = null,
        string? backfillBatchId = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            var runId = Guid.NewGuid();
            using (var cmd = database.Command(connection, transaction, database.Dialect.InsertOrIgnore(
                "WorkQueue",
                "TaskName, RunKind, MappingName, SegmentLabel, SegmentJson, RunId, Status, " +
                    "EnqueuedAtUtc, AvailableAtUtc, ReaderKind, CacheKind, WriterKind",
                "$task, $kind, $mapping, $segment, $segmentJson, $runId, $status, $now, $now, " +
                    "$readerKind, $cacheKind, $writerKind",
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
                var inserted = cmd.ExecuteNonQuery() == 1;

                if (!inserted)
                {
                    // Already queued/in-flight — return the existing item's RunId instead of minting
                    // an orphaned TaskRuns row for a queue row that will never exist.
                    transaction.Rollback();
                    return GetExistingRunId(connection, taskName, runKind, mappingName, segmentLabel);
                }
            }

            using (var cmd = database.Command(connection, transaction, """
                    INSERT INTO TaskRuns (RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, EnqueuedAtUtc, RowsRead, RowsWritten, BackfillBatchId)
                    VALUES ($runId, $taskName, NULL, $status, $runKind, $mapping, $segment, $enqueuedAt, 0, 0, $batchId);
                    """))
            {
                cmd.Bind(database, "runId", runId.ToString());
                cmd.Bind(database, "taskName", taskName);
                cmd.Bind(database, "status", RunStatus.Queued.ToString());
                cmd.Bind(database, "runKind", runKind.ToString());
                cmd.Bind(database, "mapping", mappingName);
                cmd.Bind(database, "segment", segmentLabel == NoSegment ? (object)DBNull.Value : segmentLabel);
                cmd.Bind(database, "batchId", (object?)backfillBatchId ?? DBNull.Value);
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
    public WorkItem? TryClaimNext(string taskName, string workerId)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(connection, $"""
                    SELECT Id, TaskName, RunKind, MappingName, SegmentLabel, SegmentJson, RunId, Status,
                           ReaderKind, CacheKind, WriterKind
                    FROM WorkQueue w
                    WHERE TaskName = $task AND Status = 'Pending' AND AvailableAtUtc <= $now
                      AND NOT EXISTS (
                        SELECT 1 FROM WorkQueue w2
                        WHERE w2.TaskName = w.TaskName AND w2.RunKind = w.RunKind AND w2.MappingName = w.MappingName
                          AND w2.Status IN ('Claimed','Running') AND w2.Id <> w.Id)
                    ORDER BY Priority DESC, EnqueuedAtUtc ASC {database.Limit("take")};
                    """);
                cmd.Bind(database, "task", taskName);
                cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
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

    /// <summary>True if any Pending/Claimed/Running item remains for this task — a worker's drain
    /// loop exits once this is false.</summary>
    public bool HasOutstandingWork(string taskName) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT COUNT(*) FROM WorkQueue WHERE TaskName = $task AND Status IN ('Pending','Claimed','Running');");
            cmd.Bind(database, "task", taskName);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

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
            reader.IsDBNull(10) ? null : reader.GetString(10)));
}
