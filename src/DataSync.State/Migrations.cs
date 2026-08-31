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

        """
        -- Who may use this, and how they prove it.
        --
        -- In the state store rather than the config repo: users are runtime state, and putting an
        -- access-control list in a git history the UI diffs on screen would publish it to everyone
        -- who can read the repo.
        CREATE TABLE Users (
            Id           TEXT PRIMARY KEY,   -- opaque; never a login name, which people change
            DisplayName  TEXT NOT NULL,
            Email        TEXT NULL,          -- for git attribution, where there is one
            Role         TEXT NOT NULL,      -- Admin | Viewer
            Enabled      INTEGER NOT NULL,
            CreatedAtUtc TEXT NOT NULL
        );

        -- A credential per method, all pointing at one user. That shape is what makes "one person,
        -- both methods" cheap: signing in is "find the credential, take its user", and adding a
        -- passkey to an account that already signs in with Windows is inserting a row. Columns named
        -- WindowsSid and PasskeyPublicKey on Users would have made the same requirement a migration.
        CREATE TABLE UserCredentials (
            Id            TEXT PRIMARY KEY,
            UserId        TEXT NOT NULL REFERENCES Users(Id),
            Method        TEXT NOT NULL,      -- Windows | Passkey
            -- Windows: the account SID. Passkey: the credential id. What a sign-in is looked up by.
            Subject       TEXT NOT NULL,
            Secret        TEXT NULL,          -- a passkey's *public* key; null for Windows
            Label         TEXT NULL,
            CreatedAtUtc  TEXT NOT NULL,
            LastUsedAtUtc TEXT NULL
        );

        CREATE UNIQUE INDEX UX_UserCredentials_Subject ON UserCredentials(Method, Subject);
        CREATE INDEX IX_UserCredentials_User ON UserCredentials(UserId);

        -- Server-side, so signing somebody out — or disabling them — takes effect on their next
        -- request rather than whenever a token would have expired.
        CREATE TABLE Sessions (
            Id           TEXT PRIMARY KEY,
            UserId       TEXT NOT NULL REFERENCES Users(Id),
            CreatedAtUtc TEXT NOT NULL,
            ExpiresAtUtc TEXT NOT NULL
        );

        CREATE INDEX IX_Sessions_User ON Sessions(UserId);
        """,

        """
        -- A one-time capability to create a user, or to add a credential to one.
        --
        -- The code itself is never stored — only a hash of it. It is a bearer credential that arrives
        -- over chat or email and is worth exactly what a password is worth, and a database somebody
        -- can read is a database somebody can sign in from.
        CREATE TABLE Invites (
            Id               TEXT PRIMARY KEY,
            CodeHash         TEXT NOT NULL,
            Role             TEXT NOT NULL,   -- what the invited user becomes
            UserId           TEXT NULL,       -- set when adding a credential to an existing user
            CreatedByUserId  TEXT NULL,       -- null for the bootstrap invite: nobody made it
            CreatedAtUtc     TEXT NOT NULL,
            ExpiresAtUtc     TEXT NOT NULL,
            RedeemedAtUtc    TEXT NULL,
            RedeemedByUserId TEXT NULL
        );

        CREATE UNIQUE INDEX UX_Invites_CodeHash ON Invites(CodeHash);
        """,

        """
        -- Per-pass timing, for a mapping that opted into tracing it — see phase 59.
        --
        -- Columns on TaskRuns rather than log lines, because the question these answer is comparative
        -- ("is this mapping's reader slower than it was last week", "which of forty mappings spends
        -- its time waiting on the source") and a log line cannot be aggregated. Every one is nullable
        -- and stays null for a mapping that never asked, so tracing costs an unopted-in run nothing —
        -- not even a zero.
        ALTER TABLE TaskRuns ADD COLUMN ReaderKind TEXT NULL;

        -- Two numbers, not one, because they mean different things: how long the source took to
        -- *start* answering, and how long it took to finish. A slow first row is a source planning or
        -- queueing; a slow lifetime with a fast first row is volume, or a consumer that cannot keep up.
        ALTER TABLE TaskRuns ADD COLUMN ReaderTimeToFirstRowMs INTEGER NULL;
        ALTER TABLE TaskRuns ADD COLUMN ReaderLifetimeMs INTEGER NULL;

        ALTER TABLE TaskRuns ADD COLUMN StagingKind TEXT NULL;
        ALTER TABLE TaskRuns ADD COLUMN StagingDurationMs INTEGER NULL;

        ALTER TABLE TaskRuns ADD COLUMN WriterKind TEXT NULL;
        ALTER TABLE TaskRuns ADD COLUMN WriterDurationMs INTEGER NULL;
        """,

        """
        -- A temporary hold on a replication, in state rather than in config — see phase 64.
        --
        -- Not a second spelling of Enabled. Enabled is durable intent, git-tracked and diffed, and
        -- changing it is a commit somebody has to justify later; a pause is an operator reacting to
        -- something right now, and making that a commit would either fill the config history with
        -- noise or discourage anybody from using it. The two gates apply independently: ShouldRun is
        -- Enabled AND NOT Paused.
        --
        -- On Tasks rather than in its own current-state table because the scheduler reads it on every
        -- tick, for every replication, and the row is already being read.
        ALTER TABLE Tasks ADD COLUMN Paused INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE Tasks ADD COLUMN PauseNote TEXT NULL;

        -- The append-only history behind those two columns. Tasks says what is true now; this says how
        -- it got there, one row per action, written in the same transaction as the Tasks update so the
        -- two can never disagree.
        --
        -- A pause is the one operational action with no other record: it changes what the product does
        -- without touching the config repo, so without this table "why did this stop replicating for
        -- three days in March" has no answer anywhere. Nothing reads it yet — a viewer is deliberately
        -- separate, see architecture/planning/todo/pause-history-ui.md — but the history has to exist
        -- before it can be shown, and a table added later starts empty.
        CREATE TABLE PauseEvents (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            TaskName       TEXT NOT NULL,
            Action         TEXT NOT NULL,   -- 'Paused' | 'Resumed'
            Note           TEXT NULL,
            PerformedAtUtc TEXT NOT NULL,
            PerformedBy    TEXT NOT NULL
        );
        CREATE INDEX IX_PauseEvents_TaskName ON PauseEvents(TaskName);
        """,
    ];
}
