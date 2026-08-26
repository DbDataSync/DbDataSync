namespace DataSync.State;

/// <summary>
/// Schema versioned via SQLite's built-in <c>PRAGMA user_version</c> — each entry is applied once,
/// in order, the first time a database is opened at a lower version. See
/// architecture/detailed-design.md §3.7 for the table rationale.
/// </summary>
internal static class Migrations
{
    public static readonly string[] Scripts =
    [
        """
        CREATE TABLE Tasks (
            Name TEXT PRIMARY KEY,
            Enabled INTEGER NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE TaskRuns (
            RunId TEXT PRIMARY KEY,
            TaskName TEXT NOT NULL,
            Pid INTEGER NULL,
            Status TEXT NOT NULL,
            RunKind TEXT NOT NULL DEFAULT 'Primary',
            MappingName TEXT NOT NULL DEFAULT '',
            SegmentLabel TEXT NULL,
            StartedAtUtc TEXT NOT NULL,
            EndedAtUtc TEXT NULL,
            RowsRead INTEGER NOT NULL DEFAULT 0,
            RowsWritten INTEGER NOT NULL DEFAULT 0,
            ErrorSummary TEXT NULL
        );
        CREATE INDEX IX_TaskRuns_TaskName ON TaskRuns(TaskName);
        CREATE INDEX IX_TaskRuns_TaskName_RunKind_MappingName ON TaskRuns(TaskName, RunKind, MappingName);

        CREATE TABLE ChangeWatermarks (
            TaskName TEXT NOT NULL,
            SourceTable TEXT NOT NULL,
            Watermark TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            PRIMARY KEY (TaskName, SourceTable)
        );

        CREATE TABLE Logs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId TEXT NOT NULL,
            TimestampUtc TEXT NOT NULL,
            Level TEXT NOT NULL,
            Message TEXT NOT NULL
        );
        CREATE INDEX IX_Logs_RunId ON Logs(RunId);

        -- No sentinel MappingName: RunKind.Primary is now scoped to one table mapping's own
        -- incremental pass, exactly like RunKind.Backfill is scoped to one mapping's reload — neither
        -- kind is "the whole replication" anymore. See phase-8-work-queue-schema.md.
        CREATE TABLE RunLocks (
            TaskName TEXT NOT NULL,
            RunKind TEXT NOT NULL,
            MappingName TEXT NOT NULL,
            RunId TEXT NOT NULL,
            AcquiredAtUtc TEXT NOT NULL,
            PRIMARY KEY (TaskName, RunKind, MappingName)
        );

        -- Durable, SQLite-backed cross-process work queue: the API (handling backfill triggers and
        -- scheduled due-ness) and the TaskRunner worker process(es) it spawns communicate exclusively
        -- through DataSync.State, so enqueueing has to be a table, not an in-memory structure. Claim
        -- logic (Pending -> Claimed -> Running -> Done/Failed/Cancelled) lands in a later phase; this
        -- phase only creates the schema. See phase-8-work-queue-schema.md.
        -- SegmentLabel is NOT NULL (default '') here, unlike TaskRuns.SegmentLabel — it participates
        -- in UX_WorkQueue_InFlight below, and SQLite (like standard SQL) treats every NULL as
        -- distinct for uniqueness purposes, which would silently defeat "only one in-flight Primary
        -- row per mapping" (Primary rows have no segment). '' is the sentinel for "no segment"
        -- (Primary rows, and a Backfill's single Full-mode segment); real segments get a real label.
        CREATE TABLE WorkQueue (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TaskName TEXT NOT NULL,
            RunKind TEXT NOT NULL,
            MappingName TEXT NOT NULL,
            SegmentLabel TEXT NOT NULL DEFAULT '',
            SegmentJson TEXT NULL,
            RunId TEXT NOT NULL,
            Status TEXT NOT NULL,
            Priority INTEGER NOT NULL DEFAULT 0,
            EnqueuedAtUtc TEXT NOT NULL,
            AvailableAtUtc TEXT NOT NULL,
            ClaimedAtUtc TEXT NULL,
            ClaimedByWorkerId TEXT NULL
        );
        CREATE INDEX IX_WorkQueue_Claim ON WorkQueue(TaskName, Status, AvailableAtUtc);
        CREATE UNIQUE INDEX UX_WorkQueue_InFlight
            ON WorkQueue(TaskName, RunKind, MappingName, SegmentLabel)
            WHERE Status IN ('Pending','Claimed','Running');
        """,
    ];
}
