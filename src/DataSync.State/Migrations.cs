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
        -- kind is "the whole replication" anymore. See phase-008-work-queue-schema.md.
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
        -- phase only creates the schema. See phase-008-work-queue-schema.md.
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

        """
        -- Per-item reader/cache/writer Kind overrides. A Backfill is not merely "the replication's
        -- own pipeline, run again over a segment": a replication configured for incremental sync
        -- reads with MsSqlChangeTracking and writes with the upsert-only MsSqlMerge, and reloading a
        -- segment through those would be meaningless (the reader would report the segment's *changes
        -- since a watermark* rather than its rows). A backfill has to select a reload reader and a
        -- reconciling writer for itself, so which Kinds to use is a property of the unit of work, not
        -- of the replication. NULL means "use whatever the replication's ChangeProcessing config
        -- says", which is every Primary item. See phase-010-batch-reload-trigger-and-spa.md.
        ALTER TABLE WorkQueue ADD COLUMN ReaderKind TEXT NULL;
        ALTER TABLE WorkQueue ADD COLUMN CacheKind TEXT NULL;
        ALTER TABLE WorkQueue ADD COLUMN WriterKind TEXT NULL;
        """,

        """
        -- Identifies a log line that arrived from a runner's state journal rather than live, as
        -- '<runId>:<sequence>'. Recovery applies a journal and then deletes it, and a process that
        -- dies between those two steps replays the whole file — so every operation recovery performs
        -- has to be idempotent. Every other one already is (setting a status, an outcome or a
        -- watermark is last-writer-wins); appending a log line is the exception, and this is what
        -- makes it one too.
        --
        -- NULL for every live line, and NULL is distinct from NULL for uniqueness in SQLite, so two
        -- genuinely identical lines logged in the same tick are still two lines. See phase 39.
        ALTER TABLE Logs ADD COLUMN SourceKey TEXT NULL;
        CREATE UNIQUE INDEX UX_Logs_SourceKey ON Logs(SourceKey) WHERE SourceKey IS NOT NULL;
        """,

        """
        -- Every metrics query is "this replication, this time range" (phase 36), and TaskRuns grows
        -- without bound: an aggregate over 24 hours is cheap on a small table and a full scan on a
        -- large one, and a console offering 7 days invites the larger scan.
        --
        -- An index and nothing else. Not a rollup table: that is a second copy of the truth, it needs
        -- maintaining, and there is no evidence yet that this is insufficient. Measure first —
        -- tools/benchmarks is where.
        CREATE INDEX IX_TaskRuns_TaskName_StartedAt ON TaskRuns(TaskName, StartedAtUtc);
        """,

        """
        -- Where a verification result is, not what it says (phase 43). The result itself is a parquet
        -- file the TaskRunner writes straight to disk: it can be large, it is never updated, and
        -- nothing reads it transactionally, so putting it behind the single writer would cost
        -- responsiveness for nothing. This is the index that makes one findable.
        CREATE TABLE VerificationResults (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RunId TEXT NOT NULL,
            TaskName TEXT NOT NULL,
            MappingName TEXT NOT NULL,
            CheckName TEXT NOT NULL,
            CompletedAtUtc TEXT NOT NULL,
            SourceReadAtUtc TEXT NOT NULL,
            TargetReadAtUtc TEXT NOT NULL,
            GroupsCompared INTEGER NOT NULL,
            DifferingGroups INTEGER NOT NULL,
            ResultPath TEXT NOT NULL
        );
        CREATE INDEX IX_VerificationResults_Task ON VerificationResults(TaskName, CompletedAtUtc);

        -- One result per check per run. A run that is replayed from a journal after the owner came
        -- back would otherwise index the same file twice — every recovery operation has to be
        -- idempotent, and this is how this one is.
        CREATE UNIQUE INDEX UX_VerificationResults_RunCheck ON VerificationResults(RunId, CheckName);
        """,

        """
        -- Why a run failed, when the answer is something the product can act on rather than only
        -- report. Null for the ordinary case.
        --
        -- A column rather than a new RunStatus: a position-expired run *is* a failed run — the pass
        -- did not happen — and giving it its own status would have quietly dropped it out of every
        -- "how many failed" count in the app. What is different is the remedy, and that is what this
        -- names. See PositionExpiredException.
        ALTER TABLE TaskRuns ADD COLUMN FailureKind TEXT NULL;
        """,
    ];
}
