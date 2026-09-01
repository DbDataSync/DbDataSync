namespace DataSync.State;

/// <summary>
/// The schema, as a numbered list of scripts applied once each, in order, the first time a database is
/// opened at a lower version. See architecture/detailed-design.md §3.7 for the table rationale.
/// <para>
/// **Written once, in tokens, rather than once per engine.** The three engines disagree about four
/// things in DDL — how an auto-assigned key is declared, what unbounded text is called, what bounded
/// text is called, and what an integer is called — and nothing else in these ten tables. Three copies
/// of this file would triple the cost of every future migration and guarantee that one of the copies
/// eventually drifts; the tokens keep the schema, and every word of reasoning attached to it, in one
/// place. See <see cref="StateDialect"/> for what each renders to.
/// </para>
/// <para>
/// <c>{{key}}</c> rather than <c>{{text}}</c> marks every column that participates in a primary key, a
/// foreign key or an index. That distinction exists for SQL Server alone, which cannot index an
/// <c>NVARCHAR(MAX)</c> — the other two treat both the same. It is stated in the schema rather than
/// inferred, because "is this column indexed" is a question about the whole file (an index added three
/// migrations later still counts) and the answer has to be maintained deliberately.
/// </para>
/// </summary>
internal static class Migrations
{
    /// <summary>Each script's statements, rendered for one engine and split so they can be executed
    /// one at a time — some engines refuse several DDL statements in a single command.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ScriptsFor(StateDialect dialect) =>
        [.. Templates.Select(template => SplitStatements(Render(template, dialect)))];

    private static string Render(string template, StateDialect dialect) => DropIndexToken.Replace(
        template
            .Replace("{{text}}", dialect.Text)
            .Replace("{{key}}", dialect.KeyText)
            .Replace("{{int}}", dialect.Integer)
            .Replace("{{addcolumn}}", dialect.AddColumn)
            .Replace("{{identity:Id}}", dialect.IdentityKey("Id")),
        match => dialect.DropIndex(match.Groups["index"].Value, match.Groups["table"].Value));

    /// <summary><c>{{dropindex:IndexName:TableName}}</c> — the table is part of the token because SQL
    /// Server needs it and the other two do not, so the schema has to state it either way.</summary>
    private static readonly System.Text.RegularExpressions.Regex DropIndexToken = new(
        @"\{\{dropindex:(?<index>\w+):(?<table>\w+)\}\}");

    /// <summary>
    /// Splits a script into statements on top-level semicolons.
    /// <para>
    /// Comment-aware, and not optionally: these scripts carry more prose than DDL, and several of
    /// those comments contain a semicolon mid-sentence. Splitting naively would cut a statement in
    /// half at a word boundary inside a comment and produce two fragments that are each a syntax
    /// error.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        var inLineComment = false;
        var inString = false;

        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
            }
            else if (inString)
            {
                // '' is an escaped quote inside a literal, not the end of one.
                if (c == '\'' && (i + 1 >= script.Length || script[i + 1] != '\'')) inString = false;
                else if (c == '\'') { current.Append(c); i++; }
            }
            else if (c == '-' && i + 1 < script.Length && script[i + 1] == '-') inLineComment = true;
            else if (c == '\'') inString = true;
            else if (c == ';')
            {
                Flush();
                continue;
            }

            current.Append(c);
        }

        Flush();
        return statements;

        void Flush()
        {
            var statement = current.ToString().Trim();
            current.Clear();
            // A trailing fragment of nothing but comments is not a statement.
            if (statement.Split('\n').Any(line => line.TrimStart().Length > 0 && !line.TrimStart().StartsWith("--")))
                statements.Add(statement);
        }
    }

    private static readonly string[] Templates =
    [
        """
        CREATE TABLE Tasks (
            Name {{key}} PRIMARY KEY,
            Enabled {{int}} NOT NULL,
            UpdatedAtUtc {{text}} NOT NULL
        );

        CREATE TABLE TaskRuns (
            RunId {{key}} PRIMARY KEY,
            TaskName {{key}} NOT NULL,
            Pid {{int}} NULL,
            Status {{text}} NOT NULL,
            RunKind {{key}} NOT NULL DEFAULT 'Primary',
            MappingName {{key}} NOT NULL DEFAULT '',
            SegmentLabel {{text}} NULL,
            StartedAtUtc {{key}} NOT NULL,
            EndedAtUtc {{text}} NULL,
            RowsRead {{int}} NOT NULL DEFAULT 0,
            RowsWritten {{int}} NOT NULL DEFAULT 0,
            ErrorSummary {{text}} NULL
        );
        CREATE INDEX IX_TaskRuns_TaskName ON TaskRuns(TaskName);
        CREATE INDEX IX_TaskRuns_TaskName_RunKind_MappingName ON TaskRuns(TaskName, RunKind, MappingName);

        CREATE TABLE ChangeWatermarks (
            TaskName {{key}} NOT NULL,
            SourceTable {{key}} NOT NULL,
            Watermark {{text}} NOT NULL,
            UpdatedAtUtc {{text}} NOT NULL,
            PRIMARY KEY (TaskName, SourceTable)
        );

        CREATE TABLE Logs (
            {{identity:Id}},
            RunId {{key}} NOT NULL,
            TimestampUtc {{text}} NOT NULL,
            Level {{text}} NOT NULL,
            Message {{text}} NOT NULL
        );
        CREATE INDEX IX_Logs_RunId ON Logs(RunId);

        -- No sentinel MappingName: RunKind.Primary is now scoped to one table mapping's own
        -- incremental pass, exactly like RunKind.Backfill is scoped to one mapping's reload — neither
        -- kind is "the whole replication" anymore. See phase-008-work-queue-schema.md.
        CREATE TABLE RunLocks (
            TaskName {{key}} NOT NULL,
            RunKind {{key}} NOT NULL,
            MappingName {{key}} NOT NULL,
            RunId {{text}} NOT NULL,
            AcquiredAtUtc {{text}} NOT NULL,
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
            {{identity:Id}},
            TaskName {{key}} NOT NULL,
            RunKind {{key}} NOT NULL,
            MappingName {{key}} NOT NULL,
            SegmentLabel {{key}} NOT NULL DEFAULT '',
            SegmentJson {{text}} NULL,
            RunId {{text}} NOT NULL,
            Status {{key}} NOT NULL,
            Priority {{int}} NOT NULL DEFAULT 0,
            EnqueuedAtUtc {{text}} NOT NULL,
            AvailableAtUtc {{key}} NOT NULL,
            ClaimedAtUtc {{text}} NULL,
            ClaimedByWorkerId {{text}} NULL
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
        ALTER TABLE WorkQueue {{addcolumn}} ReaderKind {{text}} NULL;
        ALTER TABLE WorkQueue {{addcolumn}} CacheKind {{text}} NULL;
        ALTER TABLE WorkQueue {{addcolumn}} WriterKind {{text}} NULL;
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
        ALTER TABLE Logs {{addcolumn}} SourceKey {{key}} NULL;
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
            {{identity:Id}},
            RunId {{key}} NOT NULL,
            TaskName {{key}} NOT NULL,
            MappingName {{text}} NOT NULL,
            CheckName {{key}} NOT NULL,
            CompletedAtUtc {{key}} NOT NULL,
            SourceReadAtUtc {{text}} NOT NULL,
            TargetReadAtUtc {{text}} NOT NULL,
            GroupsCompared {{int}} NOT NULL,
            DifferingGroups {{int}} NOT NULL,
            ResultPath {{text}} NOT NULL
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
        ALTER TABLE TaskRuns {{addcolumn}} FailureKind {{text}} NULL;
        """,

        """
        -- Who may use this, and how they prove it.
        --
        -- In the state store rather than the config repo: users are runtime state, and putting an
        -- access-control list in a git history the UI diffs on screen would publish it to everyone
        -- who can read the repo.
        CREATE TABLE Users (
            Id           {{key}} PRIMARY KEY,   -- opaque; never a login name, which people change
            DisplayName  {{text}} NOT NULL,
            Email        {{text}} NULL,          -- for git attribution, where there is one
            Role         {{text}} NOT NULL,      -- Admin | Viewer
            Enabled      {{int}} NOT NULL,
            CreatedAtUtc {{text}} NOT NULL
        );

        -- A credential per method, all pointing at one user. That shape is what makes "one person,
        -- both methods" cheap: signing in is "find the credential, take its user", and adding a
        -- passkey to an account that already signs in with Windows is inserting a row. Columns named
        -- WindowsSid and PasskeyPublicKey on Users would have made the same requirement a migration.
        CREATE TABLE UserCredentials (
            Id            {{key}} PRIMARY KEY,
            UserId        {{key}} NOT NULL REFERENCES Users(Id),
            Method        {{key}} NOT NULL,      -- Windows | Passkey
            -- Windows: the account SID. Passkey: the credential id. What a sign-in is looked up by.
            Subject       {{key}} NOT NULL,
            Secret        {{text}} NULL,          -- a passkey's *public* key; null for Windows
            Label         {{text}} NULL,
            CreatedAtUtc  {{text}} NOT NULL,
            LastUsedAtUtc {{text}} NULL
        );

        CREATE UNIQUE INDEX UX_UserCredentials_Subject ON UserCredentials(Method, Subject);
        CREATE INDEX IX_UserCredentials_User ON UserCredentials(UserId);

        -- Server-side, so signing somebody out — or disabling them — takes effect on their next
        -- request rather than whenever a token would have expired.
        CREATE TABLE Sessions (
            Id           {{key}} PRIMARY KEY,
            UserId       {{key}} NOT NULL REFERENCES Users(Id),
            CreatedAtUtc {{text}} NOT NULL,
            ExpiresAtUtc {{text}} NOT NULL
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
            Id               {{key}} PRIMARY KEY,
            CodeHash         {{key}} NOT NULL,
            Role             {{text}} NOT NULL,   -- what the invited user becomes
            UserId           {{text}} NULL,       -- set when adding a credential to an existing user
            CreatedByUserId  {{text}} NULL,       -- null for the bootstrap invite: nobody made it
            CreatedAtUtc     {{text}} NOT NULL,
            ExpiresAtUtc     {{text}} NOT NULL,
            RedeemedAtUtc    {{text}} NULL,
            RedeemedByUserId {{text}} NULL
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
        ALTER TABLE TaskRuns {{addcolumn}} ReaderKind {{text}} NULL;

        -- Two numbers, not one, because they mean different things: how long the source took to
        -- *start* answering, and how long it took to finish. A slow first row is a source planning or
        -- queueing; a slow lifetime with a fast first row is volume, or a consumer that cannot keep up.
        ALTER TABLE TaskRuns {{addcolumn}} ReaderTimeToFirstRowMs {{int}} NULL;
        ALTER TABLE TaskRuns {{addcolumn}} ReaderLifetimeMs {{int}} NULL;

        ALTER TABLE TaskRuns {{addcolumn}} StagingKind {{text}} NULL;
        ALTER TABLE TaskRuns {{addcolumn}} StagingDurationMs {{int}} NULL;

        ALTER TABLE TaskRuns {{addcolumn}} WriterKind {{text}} NULL;
        ALTER TABLE TaskRuns {{addcolumn}} WriterDurationMs {{int}} NULL;
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
        ALTER TABLE Tasks {{addcolumn}} Paused {{int}} NOT NULL DEFAULT 0;
        ALTER TABLE Tasks {{addcolumn}} PauseNote {{text}} NULL;

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
            {{identity:Id}},
            TaskName       {{key}} NOT NULL,
            Action         {{text}} NOT NULL,   -- 'Paused' | 'Resumed'
            Note           {{text}} NULL,
            PerformedAtUtc {{text}} NOT NULL,
            PerformedBy    {{text}} NOT NULL
        );
        CREATE INDEX IX_PauseEvents_TaskName ON PauseEvents(TaskName);
        """,

        """
        -- Watermark history — see phase 71.
        --
        -- ChangeWatermarks is current-value only: one row per (TaskName, SourceTable), overwritten
        -- every pass, so "where did this mapping's position go over the last month" had no answer
        -- anywhere. These two columns are that history, and they are on TaskRuns rather than in a
        -- table of their own precisely so they need no retention mechanism: phase 60 already deletes
        -- whole TaskRuns rows by age and per-mapping count, which purges these with them. A separate
        -- history table would have been a second pruning policy to build and keep in step with the
        -- first.
        --
        -- Both values, not just the new one, so a row says what the pass actually moved without
        -- anyone joining it to the row before it — which would be wrong at exactly the interesting
        -- moments anyway, since the previous row may have been pruned or may belong to a run that
        -- never advanced the watermark.
        --
        -- Null for a run that did not produce a durable watermark: a Backfill or Verification, or any
        -- failed run. A failure that had already computed a position must not record one, because the
        -- position was never made durable — the watermark advances only after the write commits.
        ALTER TABLE TaskRuns {{addcolumn}} PreviousWatermark {{text}} NULL;
        ALTER TABLE TaskRuns {{addcolumn}} NewWatermark {{text}} NULL;
        """,

        """
        -- When a worker actually started executing this run — see phase 72.
        --
        -- StartedAtUtc is written by WorkQueueStore.Enqueue, before any worker exists to do the work,
        -- so it has always meant "enqueued at" despite its name. Every duration computed as
        -- EndedAtUtc - StartedAtUtc therefore silently included however long the run sat queued. This
        -- column is the missing half: BeginRun writes it at the moment the item is claimed and begins,
        -- so duration becomes EndedAtUtc - ClaimedAtUtc and queue wait becomes ClaimedAtUtc -
        -- StartedAtUtc — two numbers that were previously one, added together, called by the name of
        -- only one of them.
        --
        -- Named to match WorkQueue.ClaimedAtUtc, which has always meant exactly this.
        --
        -- Null for a run that never reached Running: still queued, or cancelled before a worker
        -- claimed it. Such a run has no duration to report, the same as one with no EndedAtUtc. Rows
        -- that predate this column are null too, and are not backfilled — there is nothing to
        -- backfill from, and inventing a claim time would fabricate the very figure this exists to
        -- measure.
        --
        -- StartedAtUtc is deliberately NOT renamed here. Its meaning does not change; only what is
        -- computed from it does. A rename touches every reader, every query and the API contract, and
        -- is tracked separately.
        ALTER TABLE TaskRuns {{addcolumn}} ClaimedAtUtc {{text}} NULL;
        """,

        """
        -- Three timestamps, each recording the moment its name says — see phase 73.
        --
        -- The migration above got the column it added right and the moment wrong: BeginRun writes
        -- ClaimedAtUtc, and BeginRun is what a worker calls once it has *already* claimed an item and
        -- is starting to execute it. Meanwhile StartedAtUtc has been written by WorkQueueStore.Enqueue
        -- since phase 8, so it has always meant "enqueued at". Two columns, neither named for what it
        -- held, on a table sitting next to a WorkQueue whose own EnqueuedAtUtc/ClaimedAtUtc were
        -- always correct.
        --
        -- After this: EnqueuedAtUtc from Enqueue, ClaimedAtUtc from TryClaimNext, StartedAtUtc from
        -- BeginRun. Neither existing column is renamed — only the write sites move — so there is no
        -- RENAME COLUMN to spell three ways.
        --
        -- EnqueuedAtUtc *is* backfilled, unlike phase 72's ClaimedAtUtc: every existing row's
        -- StartedAtUtc already holds exactly this value, which is the whole bug. No history is lost.
        ALTER TABLE TaskRuns {{addcolumn}} EnqueuedAtUtc {{key}} NULL;
        UPDATE TaskRuns SET EnqueuedAtUtc = StartedAtUtc;

        -- The index follows the meaning, not the name. It exists for the metrics window and the
        -- prune's recency ranking, both of which ask "when was this work asked for" — EnqueuedAtUtc
        -- from here on. It also has to go before the column below can be dropped: SQLite refuses to
        -- drop an indexed column, and has no ALTER COLUMN to relax the constraint instead.
        {{dropindex:IX_TaskRuns_TaskName_StartedAt:TaskRuns}};
        CREATE INDEX IX_TaskRuns_TaskName_EnqueuedAt ON TaskRuns(TaskName, EnqueuedAtUtc);

        -- StartedAtUtc is dropped and re-added rather than altered, for two reasons at once. It was
        -- declared NOT NULL, and a run that is queued but not yet started genuinely has no start —
        -- writing the enqueue time there again to satisfy the constraint would recreate the very lie
        -- this phase is removing. And its old values are enqueue times, now safely in EnqueuedAtUtc;
        -- keeping them under this name would leave every historical row asserting a start that never
        -- happened. Null instead, on the same reasoning phase 72 gave for not backfilling ClaimedAtUtc:
        -- the true moment was never recorded, and inventing it would fabricate the figure the column
        -- exists to measure.
        ALTER TABLE TaskRuns DROP COLUMN StartedAtUtc;
        ALTER TABLE TaskRuns {{addcolumn}} StartedAtUtc {{text}} NULL;
        """,

        """
        -- Watermarks keyed by the mapping that reads, not by the table it reads — see phase 74.
        --
        -- The old key was (TaskName, SourceTable). A replication may have any number of table mappings
        -- pointing at the same physical source table, and nothing makes them read it the same way, so
        -- one row was serving two readers with incompatible notions of a position. Two mappings on one
        -- table, one using Change Tracking and one using the generic Watermark reader, overwrote each
        -- other with values neither could parse. Two generic ones with different watermarkColumn
        -- options did something quieter and worse: no parse error, just the wrong position.
        --
        -- SourceTable stays in the key beside MappingName. It is redundant for telling mappings apart,
        -- but it is what makes a mapping repointed at a different source table read from the beginning
        -- rather than resume from a position belonging to a table it no longer reads.
        --
        -- **Every stored watermark is discarded, and none is backfilled.** Not a shortcut — there is
        -- no way to recover which mapping an old row belonged to, and the case where it matters is
        -- exactly the case where more than one shared it. Every replication using CDC, Change Tracking
        -- or the generic Watermark reader reads its source from scratch on its next pass after this
        -- migration. Backfilling by guessing a mapping would silently hand one mapping another's
        -- position, which is the bug this exists to remove.
        --
        -- The same drop also pays for WatermarkKey.Build's format change, which is why the two ship
        -- together. Build now qualifies the schema and table through the source's own dialect instead
        -- of interpolating dots, so schema 'a.b' table 'c' no longer keys identically to schema 'a'
        -- table 'b.c'. That reinterprets every stored key too; done in the same migration it costs
        -- nothing beyond a resync already being paid for, and done separately it would cost a second.
        --
        -- Dropped and recreated rather than altered because the change is to the primary key, which
        -- none of the three engines can alter in place, and because there is nothing in the table
        -- worth carrying across.
        DROP TABLE ChangeWatermarks;
        CREATE TABLE ChangeWatermarks (
            TaskName {{key}} NOT NULL,
            MappingName {{key}} NOT NULL,
            SourceTable {{key}} NOT NULL,
            Watermark {{text}} NOT NULL,
            UpdatedAtUtc {{text}} NOT NULL,
            PRIMARY KEY (TaskName, MappingName, SourceTable)
        );
        """,

        """
        -- What the scheduler's polling gate saw, and when — see phase 75.
        --
        -- The gate fetches one database-wide change counter per (ConnectionName, SourceDatabase, SourceKind)
        -- group per tick, in place of every due mapping in that group fetching its own. This table is
        -- the audit trail of those fetches: one append-only row each, so "was the source actually
        -- quiet at 03:00, or were we not looking" has an answer. It is history, not mechanism — the
        -- gate never reads it back. What it decides with is each mapping's own ChangeWatermarks row,
        -- which is the only thing that can tell a caught-up mapping from one still draining a backlog
        -- under a row cap with no new source writes behind it.
        --
        -- **SourceKind is in the key, and that is the point of it.** CDC's max LSN is a byte[]
        -- position in the transaction log; Change Tracking's current version is a monotonic bigint.
        -- One database can have both mechanisms active on different tables, and a row shared between
        -- them would hold whichever polled last, in a format the other cannot parse — the same
        -- collision phase 74 removed from ChangeWatermarks one layer down.
        --
        -- Value is nullable because absence is a real answer, not an error: fn_cdc_get_max_lsn()
        -- returns null when the capture job has never run or has been stopped. Recording that as a
        -- row with no value says "we asked and there was no position", which is exactly what
        -- happened, and is not the same as no row at all (we never asked, or the source was down).
        --
        -- SourceDatabase rather than Database: DATABASE is a reserved word in T-SQL, and this schema
        -- is one file rendered for three engines, so a name that needs quoting in one of them needs
        -- quoting everywhere or nowhere. The prefix also matches SourceKind beside it.
        --
        -- Stored as text in both cases, in the same encoding ChangeWatermarks uses — hex for an LSN,
        -- decimal for a version — so the value the gate compares and the value it records are one
        -- string, not two representations that could disagree.
        CREATE TABLE ChangeCheckHistory (
            {{identity:Id}},
            ConnectionName {{key}} NOT NULL,
            SourceDatabase {{key}} NOT NULL,
            SourceKind     {{key}} NOT NULL,
            Value          {{text}} NULL,
            CheckedAtUtc   {{key}} NOT NULL
        );

        -- On CheckedAtUtc alone, because the only reader of this table today is the age purge. The
        -- group columns are indexed as part of no index deliberately: a status view that wants "the
        -- latest value for this group" can be given its own index when it exists, and an index
        -- maintained for a hypothetical reader costs every one of this table's very frequent writes.
        CREATE INDEX IX_ChangeCheckHistory_CheckedAt ON ChangeCheckHistory(CheckedAtUtc);
        """,

        """
        -- The notification feed, and the per-user cursor into it — see phase 77.
        --
        -- Global and append-only: one row per notable event, visible to everybody, in the same
        -- Id-ordered shape Logs has. That shape is the point rather than a coincidence — a client
        -- polls with WHERE Id > $sinceId and gets exactly what it has not seen, with no server-side
        -- session and nothing to reconcile if a poll is missed or repeated. Per-user rows were the
        -- alternative and were rejected in planning: broadcast semantics were what was asked for, and
        -- a fan-out row per user per event would multiply this table by the size of the org to store
        -- the same sentence N times.
        --
        -- Kind is a category, not a message. This phase writes exactly one value into it (RunFailed);
        -- phase 80 adds two more and later phases will add others, and none of them should be a
        -- migration. A UI that wants to filter or icon-code by kind reads this column; a UI that wants
        -- to render the event reads Message.
        --
        -- Message is stored, not composed at read time from the context columns. The context that
        -- explains a failure — the error summary, the position that expired — lives in rows that are
        -- themselves pruned on their own schedule, so a feed that rebuilt its sentences by joining
        -- back to TaskRuns would start saying less about older events precisely as they became harder
        -- to remember. A notification is a record of what was said, at the moment it was said.
        --
        -- TaskName/MappingName/RunId are nullable because not every kind has all three: a paused
        -- replication has no run and no mapping, and a future kind may have neither. They exist beside
        -- Message so a later notification centre can link to the thing a row is about without parsing
        -- the sentence, which is the one thing a rendered string cannot be asked to support.
        CREATE TABLE Notifications (
            {{identity:Id}},
            Kind         {{key}} NOT NULL,   -- 'RunFailed' | 'ReplicationPaused' | 'PositionExpired'
            CreatedAtUtc {{key}} NOT NULL,
            TaskName     {{text}} NULL,
            MappingName  {{text}} NULL,
            RunId        {{text}} NULL,
            Message      {{text}} NOT NULL
        );

        -- On CreatedAtUtc alone, for the age purge. Reads by Id use the primary key, and Kind is
        -- indexed as part of nothing on the same reasoning ChangeCheckHistory's group columns are: no
        -- reader filters on it yet, and an index for a hypothetical one costs every write.
        CREATE INDEX IX_Notifications_CreatedAt ON Notifications(CreatedAtUtc);

        -- One row per user, holding the highest Id that user has acknowledged. Everything above it is
        -- unread; the count is a comparison, not a per-notification flag table.
        --
        -- **No row is written for a deployment with authentication disabled.** There is no user id to
        -- key one to, and inventing a shared sentinel would mean one person's dismissal silencing the
        -- badge for everybody at a terminal where nobody can be told apart. That mode has a defined
        -- answer instead — everything always reads as unread — stated by the API rather than left to
        -- a null key landing somewhere by accident. See NotificationStore.
        CREATE TABLE NotificationReadState (
            UserId                 {{key}} NOT NULL,
            LastSeenNotificationId {{int}} NOT NULL,
            UpdatedAtUtc           {{text}} NOT NULL,
            PRIMARY KEY (UserId)
        );
        """,
    ];
}
