using Microsoft.Data.Sqlite;

namespace DataSync.State;

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
    WorkItemStatus Status);

/// <summary>
/// Durable, SQLite-backed cross-process work queue (architecture/implementation/done/phase-008-work-queue-schema.md).
/// The API process (handling backfill triggers and scheduled due-ness) and the TaskRunner worker
/// process(es) it spawns communicate exclusively through DataSync.State — this table is the mechanism,
/// not an in-memory queue, since the API can't reach into a separate OS process directly.
/// </summary>
public sealed class WorkQueueStore(StateDatabase database)
{
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
    public Guid Enqueue(string taskName, RunKind runKind, string mappingName, string segmentLabel = NoSegment, string? segmentJson = null) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            var runId = Guid.NewGuid();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO WorkQueue (TaskName, RunKind, MappingName, SegmentLabel, SegmentJson, RunId, Status, EnqueuedAtUtc, AvailableAtUtc)
                    VALUES ($task, $kind, $mapping, $segment, $segmentJson, $runId, $status, $now, $now)
                    ON CONFLICT DO NOTHING;
                    """;
                // Bare ON CONFLICT DO NOTHING (no explicit target) — UX_WorkQueue_InFlight is the only
                // unique constraint this table has, so SQLite resolves it unambiguously.
                cmd.Parameters.AddWithValue("$task", taskName);
                cmd.Parameters.AddWithValue("$kind", runKind.ToString());
                cmd.Parameters.AddWithValue("$mapping", mappingName);
                cmd.Parameters.AddWithValue("$segment", segmentLabel);
                cmd.Parameters.AddWithValue("$segmentJson", (object?)segmentJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$status", WorkItemStatus.Pending.ToString());
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                var inserted = cmd.ExecuteNonQuery() == 1;

                if (!inserted)
                {
                    // Already queued/in-flight — return the existing item's RunId instead of minting
                    // an orphaned TaskRuns row for a queue row that will never exist.
                    transaction.Rollback();
                    return GetExistingRunId(connection, taskName, runKind, mappingName, segmentLabel);
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO TaskRuns (RunId, TaskName, Pid, Status, RunKind, MappingName, SegmentLabel, StartedAtUtc, RowsRead, RowsWritten)
                    VALUES ($runId, $taskName, NULL, $status, $runKind, $mapping, $segment, $startedAt, 0, 0);
                    """;
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$taskName", taskName);
                cmd.Parameters.AddWithValue("$status", RunStatus.Queued.ToString());
                cmd.Parameters.AddWithValue("$runKind", runKind.ToString());
                cmd.Parameters.AddWithValue("$mapping", mappingName);
                cmd.Parameters.AddWithValue("$segment", segmentLabel == NoSegment ? (object)DBNull.Value : segmentLabel);
                cmd.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            return runId;
        });

    private static Guid GetExistingRunId(SqliteConnection connection, string taskName, RunKind runKind, string mappingName, string segmentLabel)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT RunId FROM WorkQueue
            WHERE TaskName = $task AND RunKind = $kind AND MappingName = $mapping AND SegmentLabel = $segment
                AND Status IN ('Pending','Claimed','Running');
            """;
        cmd.Parameters.AddWithValue("$task", taskName);
        cmd.Parameters.AddWithValue("$kind", runKind.ToString());
        cmd.Parameters.AddWithValue("$mapping", mappingName);
        cmd.Parameters.AddWithValue("$segment", segmentLabel);
        return Guid.Parse((string)cmd.ExecuteScalar()!);
    }

    /// <summary>Claims the next available Pending item for a task, if any — two-step select-then-
    /// conditional-update so a lost race (another consumer claimed it first) is detected via the
    /// affected-rows check, not a lock. The NOT EXISTS clause skips any mapping that already has
    /// another Claimed/Running item, so a claim never has to be given back for losing a RunLocks race
    /// in the common case (see RunLockStore — this is a fast pre-filter, not a substitute for it).</summary>
    public WorkItem? TryClaimNext(string taskName, string workerId)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = SqliteRetry.Execute(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT Id, TaskName, RunKind, MappingName, SegmentLabel, SegmentJson, RunId, Status
                    FROM WorkQueue w
                    WHERE TaskName = $task AND Status = 'Pending' AND AvailableAtUtc <= $now
                      AND NOT EXISTS (
                        SELECT 1 FROM WorkQueue w2
                        WHERE w2.TaskName = w.TaskName AND w2.RunKind = w.RunKind AND w2.MappingName = w.MappingName
                          AND w2.Status IN ('Claimed','Running') AND w2.Id <> w.Id)
                    ORDER BY Priority DESC, EnqueuedAtUtc ASC LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$task", taskName);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadItem(reader) : null;
            });

            if (candidate is null)
                return null;

            var claimed = SqliteRetry.Execute(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE WorkQueue SET Status = 'Claimed', ClaimedAtUtc = $now, ClaimedByWorkerId = $worker
                    WHERE Id = $id AND Status = 'Pending';
                    """;
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$worker", workerId);
                cmd.Parameters.AddWithValue("$id", candidate.Id);
                return cmd.ExecuteNonQuery() == 1;
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

    /// <summary>Cancels a not-yet-claimed item directly — no process interaction needed. An
    /// already-Claimed/Running item can't be cancelled this way in v1 (see ProcessSupervisor.CancelRun's
    /// fallback to killing the whole worker process — an accepted, documented v1 limitation).</summary>
    public bool TryCancelPending(Guid runId) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE WorkQueue SET Status = 'Cancelled' WHERE RunId = $runId AND Status = 'Pending';";
            cmd.Parameters.AddWithValue("$runId", runId.ToString());
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>True if any Pending/Claimed/Running item remains for this task — a worker's drain
    /// loop exits once this is false.</summary>
    public bool HasOutstandingWork(string taskName) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM WorkQueue WHERE TaskName = $task AND Status IN ('Pending','Claimed','Running');";
            cmd.Parameters.AddWithValue("$task", taskName);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        });

    private void SetStatus(long id, WorkItemStatus status) =>
        SqliteRetry.Execute(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE WorkQueue SET Status = $status WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });

    private static WorkItem ReadItem(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        Enum.Parse<RunKind>(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        Guid.Parse(reader.GetString(6)),
        Enum.Parse<WorkItemStatus>(reader.GetString(7)));
}
