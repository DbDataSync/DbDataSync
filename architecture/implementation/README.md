# Implementation Docs — Planning & Tracking Convention

This folder is where every non-trivial unit of work on DbDataSync is planned *before* it's built and
documented *after* it's built — in the repo, in git history, not in a chat transcript, a temporary
plan file outside the repo, or an AI assistant's own context. If it isn't written here, it doesn't
count as planned or as done.

## Structure

- **`todo/`** — work that has been designed but not yet implemented. One file per phase.
- **`done/`** — work that has been implemented, verified, and committed. One file per phase.

Upstream of this folder is `architecture/planning/`, where a thought is captured *before* anyone knows
what to do about it; a planning doc moves to its own `done/` once we've agreed on a plan, which is
typically the moment a phase doc appears in this folder's `todo/`. See `architecture/planning/README.md`.

A phase moves from `todo/` to `done/` exactly once, at the point its implementation is verified and
committed — see "Workflow" below. Nothing is ever deleted; a phase whose plan changed materially before
implementation gets its `todo/` file edited in place (with a note on what changed and why), not
silently replaced.

## Phase IDs

As of phase 156, a phase doc's filename is `phase-<N><Letter>-<kebab-slug>.md` — the number, one
uppercase letter, then a descriptive slug: `phase-157Q-mysql-mariadb-driver.md`,
`phase-158Q-oracle-driver.md`. Almost nothing about how numbers get picked changes: check `todo/`/`done/`
for the highest one in use, take the next — the exact procedure this repo has always used. **The letter
is not a topic, a lineage, or anything else that needs a judgment call.** Pick one uppercase letter at
random, once, the first time a given session/worktree writes a phase doc, and reuse that same letter for
every phase doc that session goes on to write, regardless of what each one is about. A cheap check
against letters already in use in `todo/`/`done/` is worth doing before settling on one, but even without
it the odds are good — a fresh letter only gets claimed once per session, not once per phase, so the
26-letter space empties far more slowly than the number does.

**What the letter is actually for**: nothing, in the common case — refer to a phase by its bare number
("phase 157") exactly as before, and that's almost always all a reader needs, since the letter's whole
job is to keep the *filename* from colliding even on the rare occasion the number does. Two sessions
checking `todo/`/`done/` within moments of each other can still both conclude "157" is the next free
number — that race is exactly what already happened twice in this repo's own history (`phase-150` and
`phase-154`, each independently claimed and only discovered at rebase, needing a manual reconciliation
commit each time). Under the new scheme that same race produces `phase-157Q-...md` and
`phase-157M-...md` — two real files that coexist with no conflict at all, because each session picked its
own letter independently. The number collided; the filename didn't. Only when a genuine number collision
like that actually happens does the letter matter for a human or an agent — reach for the full ID
(`phase 157Q`, not just `phase 157`) to say which one, until someone renumbers the later one to close the
gap (or doesn't bother, since nothing requires the sequence to be gap-free either).

See `architecture/implementation/done/phase-156-branching-and-phase-numbering-methodology.md` for the
fuller reasoning, including the companion branching-flow change made in the same pass for an unrelated
but similarly-shaped reason, and that doc's own Retrospective for how this section's first draft got the
mechanic wrong (a letter per *topic*, exhausting the alphabet in days) before landing here.

**This is prospective only.** Nothing already in `todo/`/`done/` under the old `phase-NNN` scheme is
renamed — doing so would break every git-history and cross-doc reference to it, exactly the harm the
"Build order" section below already refuses to inflict by renumbering for priority. `phase-156` is the
last phase issued under the old all-integer scheme, deliberately.

## Build order

`todo/` is a set, not a queue — a phase's own ID records when it was *designed*, not when it will be
built, and renumbering files to express priority would break every reference in git history and in the
planning docs that point at them.

So the order lives here, and is the one to work through:

| | phase | why here |
| --- | --- | --- |
| 1 | **151** — deprovisioning: source-side state goes when the config does | split out of 034. Not a Postgres phase — phase 33's trigger-audit shadow tables and triggers are left behind the same way, and have been since they shipped |
| 2 | **152** — replication slot lag, and slots nobody claims | split out of 034. Smaller than it looks: the statements exist and are tested; the work is an API surface and a place on the connection card |
| 3 | **150** — the columnar decision, measured | split out of 038, which shipped the sink the question was waiting on. Needs a real server; its deliverable is numbers, not code |
| 4 | **155** — `pgoutput`: logical decoding with nothing installed on the source | **gated on a product decision, not on engineering** — last deliberately, because if the answer is "managed Postgres" it should never be built at all. See the phase doc's own "Why this might not be worth building" |
| 5 | **158K** — snapshot releases for every promoted `test` build, and `dbdatasync update` to list, stage and print the install commands | placed after the queue above rather than jumping it — reorder freely, only this table changes. Also the first phase to verify the release pipeline's own rollout: its workflow only goes live once a release has put it on `main` |
| 6 | **159K** — apply an update automatically, from the CLI and the web console | **must follow 158K** (it executes 158K's `UpdatePlan` and reuses its `DbDataSync.Updates` library). **Built for Linux and the CLI; not yet verified on real hosts, and Windows is deliberately off** — see its Progress section for exactly what is and is not proven |
| 7 | **162K** — images embedded, docs audited, links rewritten at pack time for the NuGet listing | **follows 160K, which is done**; independent of 161K. Authors docs and README relative, rewrites to a commit-SHA-pinned form only for the NuGet README |

Updated 2026-09-19 (later): **159K is built** for Linux and the CLI, with Windows deliberately switched off. **Read its
"trust boundary" section first:** the first build had the root-privileged pre-start step act on a request file that
the unprivileged service can write — a compromised service could have had root install a package of its choosing,
for every default Linux install. It was found by rereading the design from the attacker's side, before anything was
committed, and redesigned: the request is now a version and nothing else, root re-derives everything from the pinned
release sources and its own location, its records live in a directory the service cannot write, and the step only
exists in a unit that root registered with `service install --self-update`. Running the real thing then found a
second bug (a relative path made a rollback uninstall the new version and fail to install the old), also fixed. Checking
the docs' claims about signatures found a third problem, a documentation one: `dotnet tool install` does **not**
enforce NuGet's signature trust policy (tested), so nothing in 158/159 verifies a package signature, and neither
release nor snapshot packages are author-signed — written up as
`architecture/planning/todo/follow-up-phase-158-159-release-and-snapshot-packages-are-unsigned-and-tool-install-does-not-verify-them.md`. A
`DbDataSync.Updates` applier that installs, keeps the outgoing package aside and rolls back; `dbdatasync update
--apply` (a synchronous stop → install → start → health check path) and `--status`; a privileged
`internal apply-update` step the systemd unit runs before every start; `api/admin/update/*` behind a setting that is
off by default; and Admin → Updates in the console, driven in a real browser. The mechanism is *start-counting*: the
service asks to be restarted with exit code 75, the unit applies the update before the next start and records it
"on trial", and only a new version that has then served for a while confirms it — otherwise the next start rolls it
back, with nothing watching but the restart systemd does anyway. What was checked for real: the applier against a
real tool root (apply, then rollback from the copy kept out of the real `.store`, run from the installed tool
itself), a real `--apply` downgrade through nuget.org, the rendered unit against `systemd-analyze`, and the page in
Chromium. What was **not**, because it needs a host this was not built on: `ExecStartPre=+` escaping the hardened
unit's sandbox (systemd's documented behaviour; the user manager here cannot create a mount namespace), ownership
handling as root, a whole service updating itself under a system unit, and everything on Windows. The Linux spike's
findings changed the design in two ways worth knowing: exit 75 needs `SuccessExitStatus`/`RestartForceExitStatus` or
it logs a failed unit on every update, and confirmation has to wait out a grace period rather than happen at "ready".
See `architecture/implementation/todo/phase-159K-automated-update-from-cli-and-web-console.md`.

Updated 2026-09-19 (earlier): **158K and 159K join `todo/`, as an ordered pair.** From two planning
docs (`planning/done/snapshot-packages-on-github-packages.md`, `planning/done/self-update-and-release-channels.md`).
158K publishes a snapshot GitHub *prerelease* for every promoted `test` build (newest 20 kept) and adds
`dbdatasync update` — list, choose, stage a snapshot's nupkg, print the commands. 159K makes the swap
automatic from the CLI and an Admin → Updates page. Three things checked while writing them are worth
knowing, because each changed the design: GitHub Packages was **not** chosen — its NuGet feed needs a token
to read even for a public repo, while an anonymously downloaded nupkg in a plain folder installs fine (tested
against a real release, with nuget.org excluded, since `--add-source` alone *adds* to it and hid a first
attempt's failure); **`dotnet tool update` refuses a lower `--version`**, so a rollback is uninstall +
install; and the default Linux unit is hardened enough that an in-process "helper" cannot apply an update
there at all, which is why 159K proposes a privileged `ExecStartPre=+` step instead.

158K is **implemented and verified as far as it can be before a release exists** — `DbDataSync.Updates`
(118 tests), `dbdatasync update` (34 end-to-end tests, and run for real against the live sources and from a real
tool install, including the printed uninstall+install and snapshot-from-a-folder commands), and
`publish-snapshot.yml` (each step's logic exercised, never run on GitHub). It stays in `todo/` until that first
real run, as 127 did; its own Progress section lists exactly what is and is not verified.

Updated 2026-09-18 (previously latest): **157 is done** — never added to the table above, same situation as
135/136/137: written as a `planning/todo/` doc and a phase doc in the same session, picked up directly.
An interactive CLI command on Windows (`config check`, `setup`, a foreground `serve`, or any other
command) now disables libgit2's repository-ownership check for itself before touching the config repo,
warning first — the flip side of phase 135, which transferred that same directory's ownership to the
service account and left every other command hitting the identical "not owned by current user" error
phase 135's own fix exists to avoid, just from the other identity. One call
(`GitOwnershipValidation.DisableIfInteractive`, `Program.cs`) gated on the exact predicate `ServeCommand`
already uses twice for an unrelated purpose — `WindowsServiceHelpers.IsWindowsService()` — so the
long-running service process is untouched and keeps failing the way phase 135 already made it fail if it
somehow isn't the owner. `LibGit2Sharp.GlobalSettings.SetOwnerValidation`'s exact signature was confirmed
by loading the installed package assembly via reflection, not assumed from a changelog. Verified for real
on this (Linux) sandbox — the non-Windows no-op branch, and the full `DbDataSync.Cli.Tests` suite (140
passed, 11 skipped, 0 failed); the actual Windows behavior this phase exists to fix needs a real Windows
box or CI runner, neither available here, so the `[WindowsOnlyFact]` test covering that branch reports
`[SKIP]` rather than a faked pass. See
`architecture/implementation/done/phase-157K-windows-interactive-cli-disables-libgit2-ownership-check.md`.

Updated 2026-09-17 (previously latest): **156 is done.** Two related collision problems this session's own
history demonstrated in real time — `phase-150` and `phase-154` each independently claimed by two
concurrent sessions, discovered only at rebase — fixed in one pass, since both trace back to the same
root cause: a flat, shared namespace with no structural separation between concurrent lines of work.
`dev`/`test`/`main` replaces everyone pushing straight to `main` (`architecture/branching-and-releases.md`);
number+letter (`phase-157Q-...`, `phase-158Q-...` — same incrementing number as always, plus one letter
picked per session to keep a rare number collision from becoming a filename collision) replaces one shared
incrementing integer alone for phase docs (this README's own new "Phase IDs" section, above). Both
prospective only — nothing renamed, no branch protection turned on, no automation added that wasn't
explicitly asked for. The numbering section's first draft used a per-topic letter instead of a per-session
one and got corrected in the same pass — see
`architecture/implementation/done/phase-156-branching-and-phase-numbering-methodology.md`'s own
Retrospective for the real mistake and the user's correction.

Updated 2026-09-17 (previously latest): **155 is new, and deliberately conditional.** The other half of
phase 34's own still-open product question, written up so the decision can be made once against a real
design rather than re-argued. `pgoutput` is the output plugin PostgreSQL's native logical replication
uses; it ships with Postgres, so it decodes the same WAL through the same replication slots with
**nothing third-party installed on the source server** — which is the one prerequisite `wal2json`
carries that an operator may be unable to satisfy without a change-control ticket, and the reason this
repo now maintains its own Postgres image at all. The prerequisites split better by kind than by count:
settings (a restart) and SQL objects (a slot, and for `pgoutput` a publication) are the same either
way; only the third-party-software row differs, and `pgoutput` empties it. **What it does not buy is
the restart** — `wal_level = logical` is what makes Postgres write the old tuple an update or delete
needs, so no plugin and no client-side decoding recovers information that was never written; if the
restart alone is the blocker, `TriggerAudit` is the honest answer. The phase is a *sibling* reader, not
a rewrite: slot lifecycle, every provisioning check, the expiry handling and the advance-is-off default
all carry over unchanged, and Npgsql's typed columns remove the JSON conversion layer phase 34 needed.
Its biggest open question is recorded rather than deferred — `IChangeReader` hands a reader an open
`DbConnection`, and a replication session cannot use one. See
`architecture/implementation/todo/phase-155-pgoutput-logical-decoding.md`.

Updated 2026-09-17 (previously latest): **154 is done.** `dotnet-integration`'s `services:` block — a
hand-maintained second copy of `docker-compose.yml`'s own container topology — is gone, replaced by a
`docker compose up -d --wait` step (no explicit service list; this job needs every engine the file
defines). That duplication is exactly what let phase 153's own mariadb/oracle gap happen in the first
place, and removing it also removed phase 153's manual "Provision Oracle" `docker exec`/`sqlplus`
workaround entirely — that only ever existed because a `services:` container is created before
`actions/checkout` puts the repo on disk, so `docker-compose.yml`'s own bind mount of
`docker/oracle-init/*.sql` couldn't work there; running `docker compose up` after checkout removes the
reason for the workaround. Confirmed on a real, fully green CI run (`35283992102`) — every job passed,
Oracle's tests included, with no manual provisioning step at all. Two real, unrelated test bugs found and
fixed along the way, discovered while verifying phase 153's own first CI run: `LibraryLoadTests`' MySQL
canary test was stale since phase 147 legitimately gave `DbDataSync.Drivers.MySql.csproj` a real
(`ExcludeAssets="runtime"`) reference to MySqlConnector, and `BulkLoadIntegrationTests`' race assertion
assumed an explicit reload always beats a mapping's own auto-triggered first pass — untrue given
`RunExecutor.ExecuteWorkerAsync` runs both lanes concurrently on the same worker process, confirmed by a
real CI failure and fixed to accept either legitimate outcome. See
`architecture/implementation/done/phase-154-ci-integration-suite-onto-docker-compose.md`. A third,
genuinely unrelated failure turned up on the very next run — out of this phase's scope, written up in
`architecture/planning/todo/follow-up-phase-154-scd2-cdc-timestamp-mapping-race.md`.

Updated 2026-09-17 (previously latest): **153 is done.** CI's `dotnet-integration` job was missing the
MariaDB and Oracle service containers phases 147/148's own new `Category=Integration` tests need — every
`MariaDb*`/`Oracle*` test in `DbDataSync.Drivers.MySql.Tests`/`DbDataSync.Drivers.Oracle.Tests` had been
failing on `main` with a connection refused since phase 147 merged. Fixed by adding both services to
`.github/workflows/ci.yml`, matching `docker-compose.yml`'s own ports exactly so no test fixture's default
connection string needed to change. One real finding: `docker-compose.yml`'s own mechanism for Oracle's
grants/probe-table setup — bind-mounting `docker/oracle-init/` into the container — cannot work as a
GitHub Actions `services:` entry at all, since service containers are created and pass their healthchecks
*before* `actions/checkout` puts the repository on disk; the container would report healthy having
silently skipped both scripts. Fixed with an explicit "Provision Oracle" CI step running them via
`docker exec ... sqlplus -s / as sysdba` after checkout — confirmed against a live container, not assumed,
that bare peer authentication lands in `CDB$ROOT` (what both scripts' own `ALTER SESSION SET CONTAINER`
needs) rather than the PDB the password-based form used during phase 148's own local testing would have
landed in. No source code changed. The one thing this phase could not itself verify: a real GitHub Actions
run — everything was tested against an equivalent local container, not the actual runner. See
`architecture/implementation/done/phase-153-ci-mariadb-and-oracle-service-containers.md`.

Updated 2026-09-17 (previously latest): **149 is done.** Docs updated for MySQL/MariaDB and Oracle now that
phases 147/148 actually shipped — `docs/replication-concepts.md`, `docs/drivers-and-libraries.md`,
`README.md`, and both `additional-database-drivers.md`/`change-tracking-strategies.md`'s own outcome
tables. The plan predicted three new reader-kind rows; shipped as one (MySQL's and Oracle's trigger-audit
options are the same existing `TriggerAudit` Kind Postgres/SQL Server already use, not new ones — only
`OracleFlashback` needed a row of its own). One finding the plan didn't anticipate at all: the MySQL
descriptor worked example, written before phase 147 existed, had become a stale, false claim ("an engine
no part of DbDataSync's own compiled code references at all") now that MySQL is a compiled built-in —
fixed by reframing the section rather than deleting a still-useful descriptor-mechanism walkthrough. No
source code changed. See `architecture/implementation/done/phase-149-mysql-oracle-docs-update.md`.

Updated 2026-09-17 (previously latest): **148 is done.** Oracle driver (`DbDataSync.Drivers.Oracle`) plus
trigger-audit *and* Flashback Version Query change tracking, verified against a real
`gvenzl/oracle-free:23-slim` instance throughout the build. Five real findings, more than any prior driver
phase: `OVERRIDING SYSTEM VALUE` does not parse on Oracle at all (confirmed via `sqlplus`) — a
`GENERATED BY DEFAULT ON NULL AS IDENTITY` target column needs no override machinery instead, and a
`GENERATED ALWAYS` one has no working one; `INSERT ALL` cannot generate a unique value per branch because
Oracle evaluates a sequence at most once per *statement*, however many times it's referenced — fixed by
switching the multi-row insert to `INSERT ... SELECT ... UNION ALL ... FROM dual`; Oracle bind variables
reject both a colon baked into `ParameterName` and a small set of reserved words (`table`, `trigger`) as
names outright; and `AS` before a table/subquery alias is invalid Oracle syntax, which surfaced two
latent portability bugs in **shared** `DbDataSync.Drivers.Generic` code (`TriggerAuditStatement`,
`HistorizedStatement`, `SegmentScope`, `BatchInsertStagingProvider`) now fixed for every engine. One
finding named but not resolved: Flashback Version Query cannot see history for a table created too
recently, regardless of the requested SCN window — practically irrelevant for a real deployment, but real
enough that its own test suite needed a container-init-provisioned table with real age rather than one
created fresh per test. Verified: 58 tests (37 unit, 21 integration) against a live server, plus the full
solution's entire test run (2000+ tests, every project) confirming the two shared-code fixes broke
nothing on Postgres/MsSql/MySQL. See
`architecture/implementation/done/phase-148-oracle-driver-trigger-audit-and-flashback.md`.

Updated 2026-09-16 (previously latest): **147 is done.** MySQL/MariaDB driver
(`DbDataSync.Drivers.MySql`) plus trigger-audit change tracking, built directly against the Postgres
driver as its structural template and phase 33's already-built generic trigger-audit mechanism — every
reader/writer/staging provider it registers is `DbDataSync.Drivers.Generic`'s, unmodified. Two real bugs
caught before they shipped: `InformationSchemaQueries.ListTablesAsync` (correct for Postgres, silently
wrong for MySQL — `information_schema.tables` is server-wide there, not database-scoped, so it needed
its own override) and three ANSI-default DDL forms MySQL doesn't actually accept (`CAST ... AS VARCHAR`,
`ALTER COLUMN ... TYPE`, needing a stated `RENAME COLUMN` version floor). One real, unresolved gap named
rather than papered over: `RenderTieSafeRowLimit` has no tie-safe MySQL implementation — see the phase
doc's own Finding 2. Verified against real `mysql:9` **and** `mariadb:11` containers from one shared test
body per pair, 28 integration tests green on both — the empirical half of the sibling planning doc's
"one implementation covers both forks" claim. See
`architecture/implementation/done/phase-147-mysql-mariadb-driver-and-trigger-audit.md`.
Updated 2026-09-16 (previously latest): **035 is done and removed.** The Version Control tab has shown the
auto-commit log since phase 6 and until now that was all it did; it now has **View changes** (an inline
Monaco diff editor, with `yaml` joining `csharp` and `sql` as a lazy language chunk) and **Restore to
here**. Three things worth knowing. **The restore preview is a different question from the commit's own
patch** — the plan said the confirmation is "built from the same diff the first action shows", which is
right about the machinery and wrong about the anchor: a commit's patch says what it changed, a
confirmation has to say what *will* change, and restoring to a commit that only renamed a column may
delete three mappings created since. Two endpoints over one diff engine, anchored differently. **The
API returns both sides of each file rather than a unified patch**, because that is what a diff editor
renders from and because a patch elides the context a config file is read for — which settles the
plan's diff-size question as "complete file list, capped content". And **a dangling connection warns
rather than refuses**: the plan's rule is that a restore must not reach config a save would reject, and
its own example (a connection that no longer exists) is not something a save rejects, so refusing it
would make the restore stricter than the save it restores. The save-time validation is now literally
shared (`ValidateTableMapping`) so the two doors cannot drift. Phase 81's repo-root
`dbdatasync.config.yaml` blind spot is fixed at the query layer and tested — it was the one-line prefix
bug 81 predicted — and the screen for it is
`architecture/planning/todo/follow-up-phase-035-root-config-has-history-but-no-screen.md`, because
restoring that file can take the console offline and that needs a decision rather than a dialog. See
`architecture/implementation/done/phase-035-config-history-diff-and-revert.md`.

Updated 2026-09-16 (previously latest): **034 is done and removed, and split twice.** `PgLogicalSlotReader`
reads PostgreSQL logical decoding as a query — `pg_logical_slot_peek_changes` through `wal2json`, the
slot's LSN as the watermark — and the plan's central claim held: it needed no change to `IChangeReader`
and no streaming subsystem. **The plan's slot model did not hold, and that is the thing to know.** It
said one slot per replication; advancing a slot is `IPositionAcknowledging`, which is per-mapping by its
own signature, so a shared slot advanced by whichever mapping ran last discards changes the others have
not read — silent data loss, arriving through a different door than the one "peek, never get" guards.
Resolved by defaulting to a slot per source table and making advancement opt-in and **off**, the same
call `TriggerAuditReader`'s pruning option already makes here. The cost is named rather than hidden:
`max_replication_slots` defaults to 10. Three other things the plan did not anticipate are in the
retrospective — `wal2json` cannot apply a column transform (refused by name), decoded JSON values have
to be converted or the same mapping works through one staging provider and fails through the other, and
**no public image carries `wal2json` any more** (Debezium's build only `decoderbufs` as of 3.x), so
the environment is a two-line Dockerfile and CI brings it up through compose (the workflow change
itself is phase 154's, per that phase doc's rebase note). **One product question is still open and
wants a decision, not more work**: whether `wal2json` is an acceptable prerequisite or
`pgoutput` has to come first. Nothing built here has to be undone either way. Split out: **151**
(deprovisioning — there is no such concept anywhere in this codebase, and phase 33's shadow tables have
the same gap) and **152** (slot lag and orphan reporting). See
`architecture/implementation/done/phase-034-postgres-logical-replication.md`.

Updated 2026-09-16 (previously latest): **038 is done and removed, and split.** Part 1 shipped:
`PgCopyStagingProvider` stages through `COPY … FROM STDIN (FORMAT BINARY)`, registered ahead of the
generic batched-`INSERT` provider, creating the identical staging table from that provider's own DDL
builder — so a mapping moves between the two without anything downstream noticing, which is what the
integration tests assert by running one body against both Kinds. It is the first thing `PostgresDriver`
has ever registered that is not `Drivers.Generic`'s, and the reason is worth keeping: `COPY` is a
protocol on the connection, not a statement, so there was no `SqlDialect` hook it could have gone
through. The difference that keeps both providers registered is **not** speed — binary `COPY` sends the
column's own wire format and the server converts nothing, so a value it cannot be written as fails the
pass rather than being coerced, and the phase spends its care on making that failure name the column,
the types, and the alternative. **Part 2 — the columnar decision — is now 147** rather than being
quietly dropped: its deliverable is a set of benchmark numbers off a real server, this machine has no
Docker, and the existing harness does not model the one configuration the decision actually turns on
(typed columnar fed from a boxing source) or use a real `COPY` sink. Writing that harness blind, to
produce the numbers a large architectural change would be decided on, is worse than specifying it. See
`architecture/implementation/done/phase-038-postgres-copy-staging.md`.

Updated 2026-09-16 (previously latest): **145 is done and removed.** Phase 132's per-key/per-row loop for a
duplicate natural key is gone, replaced by two window-function statements over one derived table:
`1 + K + 2KR` round trips for `K` duplicate keys of `R` staged rows each became **3**, whatever `K` and
`R` are. Two things the plan had not worked out turned up in the building and are the parts worth
knowing about. **Its own SQL sketch was wrong** — a plain `LEAD` over every staged row would have opened
a spurious second version for any change that touches no mapped column, which
`Scd2CdcGuaranteedDeliveryIntegrationTests`' Id 3 already covers and would have failed on; the mechanism
that actually reproduces the loop is `LAG` (a row's predecessor *is* the version open when it arrives, so
only a key's first row consults the target) with `LEAD` over the *boundary* rows only. And **the two
statements had an ordering hazard the plan did not anticipate**: both need the pre-pass answer to "what
was open for this key", and whichever runs second reads a target the first has written to — fixed by
having both ignore the rows this pass itself opened, matched on a surrogate key that is a pure function
of staged data. Timing was *not* measured (no Docker on the implementing machine, and CI runs correctness
tests rather than benchmarks) — carried forward, for the second time, as
`architecture/planning/todo/follow-up-phase-145-set-based-scd2-duplicates-never-timed.md`. See
`architecture/implementation/done/phase-145-scd2-duplicate-keys-set-based.md`.

Updated 2026-09-16 (previously latest): **146 is done and removed.** The published `dbdatasync` tool's
nupkg was 152.6MB, ~330MB uncompressed of it DuckDB's own native binaries for all five platforms,
shipped unconditionally on every install regardless of which one a machine could load — phase 109i's
"DuckDB decoupling" only ever excluded the *managed* assembly (NuGet treats `native` as a separate
asset bucket `ExcludeAssets="runtime"` never touched, and said so in its own "out of scope" section).
Widened the exclusion to `ExcludeAssets="runtime;native"` on the three consumers — 46.4MB after, a 70%
cut, zero new code: `LibraryInstaller`'s already-existing RID-aware install (109i's own unconditional
`serve`-start hook) was already restoring the correct single-platform native binary alongside the
managed one, it just had nothing to matter against while the package carried a redundant copy of every
platform regardless. Verified for real, not assumed — a fresh install through a throwaway console
project followed by an actual `SELECT 6 * 7` through the freshly-loaded DuckDB connection. See
`architecture/implementation/done/phase-146-duckdb-native-asset-exclusion.md`.

Updated 2026-09-15 (previously latest): **five open follow-ups resolved**, all not requiring Windows or
further user input, per a walkthrough of `architecture/planning/todo/`'s remaining backlog:
- A manual "Run Now" against a still-`Loading` mapping now fails cleanly (`MappingLoadingException` /
  `RunFailureKinds.MappingStillLoading`) instead of crashing with a bare `ArgumentNullException`.
- A Bulk Load reader override pointed at a position-capturing reader (reachable through the SPA's own
  pipeline editor) now fails with a clear message instead of crashing.
- `/request-initial-load` hardens every failure now, not just `WorkQueueCollisionException` — a 400,
  reconstructed client-side, rather than an unhandled 500 `IsUnreachable` could misread as the owner
  being gone. Also closed a real gap: phase 143's own 409 reconstruction had no wire-level test at all.
- A multi-segment initial load that partly collides no longer leaves an orphaned, permanently-`Running`
  batch — the segments it already enqueued are cancelled and the batch row removed, rollback rather than
  true cross-store atomicity.
- `RunWatermarkTimeTests`' own flaky-on-CI helper (`follow-up-phase-140-...`) is fixed: a dead test pid
  was racing `ProcessSupervisor.ReconcileOrphanedRuns()` for real, reopening a claim mid-flight — reproduced
  directly (not just reasoned about) by calling `ReconcileOrphanedRuns()` mid-sequence, then fixed with a
  live pid and a permanent regression test.

Each is a real code change with its own commit and test coverage, not a documentation-only resolution —
see each follow-up doc's own "Fix" section in `architecture/planning/done/` for specifics. The two
Windows-only remaining follow-ups (`follow-up-phase-136-140-windows-service-event-log-output-never-read-
by-a-human.md`, `follow-up-phase-140-windows-cannot-reach-the-managed-self-signed-certificate-from-the-
cli.md`) are still open in `architecture/planning/todo/` — both need a real elevated Windows box.

Updated 2026-09-15 (previously latest): **144 is done and removed.** Fixed two independent bugs that had
made the `playwright` job fail most runs on `main` for weeks: golden-path test 18 raced phase 134's Bulk
Load divert (the same shape phase 141 already fixed in `Api.Tests`, now with a TypeScript sibling of
`MappingLoadWaiter`), and `DbDataSync.TaskRunner` — a separate process spawned per replication — had no
equivalent of the API's own auto-install for a built-in driver's library
(`microsoft-data-sqlclient`/`npgsql`), so a worker that happened to touch a driver before the API did
threw `Could not load file or assembly` outright. Both root-caused against real CI logs (not sampled),
the second confirmed with a throwaway console harness reproducing the exact real error message with and
without the fix. Also made the job self-reporting (`github` reporter, an uploaded JSON report) — the
reason nine earlier red runs drew no investigation. Merged after three consecutive green `playwright`
runs, against a pre-fix baseline of 3 green out of the last 12. See
`architecture/implementation/done/phase-144-playwright-ci-intermittent-failures.md`.

Updated 2026-09-15 (previously latest): **145 is new.** Phase 132's own deferred
window-function alternative for duplicate-key handling, designed from scratch (neither phase 132 nor its
own plan doc had worked out the SQL beyond naming the mechanism) and carried forward while walking
through open follow-ups with the user — see
`architecture/implementation/todo/phase-145-scd2-duplicate-keys-set-based.md`. Placed at the bottom, not
jumping the queue: this is a pure optimization with no correctness or urgency argument behind it.

Updated 2026-09-15 (previously latest): **143 is done and removed.** Fixed the real production bug found
while chasing phase 141's last known failure — a losing auto-triggered initial load (a mapping's own
first pass racing a concurrent operator reload for the identical segment) used to strand `ReadHold` at
`Loading` forever; now the loser fails cleanly and self-heals on its mapping's next scheduled pass. Also
resolved phase 141's own still-open row-count flake, which turned out to share this exact cause via a
stale test fixture rather than being an independent bug — `dotnet-integration` has no known failures
left. See `architecture/implementation/done/phase-143-initial-load-race-loses-cleanly.md`.

Updated 2026-09-15 (previously latest): **140 and 141 are both done and removed.** 140 (a
separate session, on a real Windows host) fixed every remaining Windows CI failure this doc's own
"rescoped" note below describes — see `architecture/implementation/done/phase-140-windows-ci-
verification-and-remaining-failure.md` for the full retrospective. 141 fixed `dotnet-integration`'s
~46-test wave down to one known, intermittent flake (`BulkLoadIntegrationTests.
PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`'s row-count race, left open and documented in its
own doc) — the real root cause was never container contention (this doc's own original hypothesis,
disproven by reproducing the exact failure counts locally against healthy, uncontended containers): a
stub `IInitialLoadEnqueuer` in test fixtures, and a batch of pre-phase-134 tests asserting behaviour
that permanently moved once a position-capturing reader's first pass stopped reading directly.
Investigating that one remaining flake surfaced a real, separate production bug — a losing
auto-triggered initial load strands a mapping's `ReadHold` at `Loading` forever, reproduced
deterministically (15/15 local timeouts) — written up in
`architecture/planning/done/initial-load-pending-batch-stranded-by-a-concurrent-reload.md` and carried
forward as **143**, now at the top of this table.

Updated 2026-09-15 (later than the three notes below): **139 is done and removed** — Bulk Load History,
Monitoring's fourth sub-tab. Real keyset pagination mirroring phase 104's Run History exactly
(`BulkLoadHistoryCursor`/`Page`, `BulkLoadHistoryCursorCodec` — kept independent of `RunHistoryCursorCodec`
rather than sharing a generic base, since what differs between them (tiebreak field type, filter count)
would need more indirection than the ~15 duplicated lines saved — `BulkLoadBatchStore.GetHistory`), a
`mappingName` filter, and a static, non-polled list (`BulkLoadProgressCard` stays the one live view).
Built on its own branch/PR (#2) per the CI-gated convention. The orchestrating session independently
re-verified the whole PR beyond the implementing agent's own report — read the full diff, ran every new
test locally (20 total, all passing), and re-ran the new Playwright spec from scratch against a fresh
instance (3/3, matching the agent's claim) — before merging. Merged with three CI jobs red
(`dotnet-integration`, `dotnet-windows`, `playwright`), each traced to its actual failure content and
confirmed as an already-tracked, unrelated pre-existing issue (phase 140's Windows gap, phase 141's
environmental flakiness, and one unrelated flaky `golden-path.spec.ts` test) rather than assumed clean —
merged only after the user explicitly authorized proceeding with red CI, following an auto-mode safety
check's (correct) initial refusal.

Updated 2026-09-15 (later than the two notes below): **138 is done and removed** — a mapping-level
Delete Reconciliation override in the SPA. `TableMappingForm` gained `reconcileOverride` state, wired
into the dirty-check and save payload the same way `pipeline`'s overrides already are; a new
`MappingReconcileCard` wraps the existing `ReconcileConfigCard` (reused unmodified) in a whole-object
inherit/override toggle on the mapping's Pipeline tab, mirroring `MappingPipelineCard`'s own per-stage
pattern. The writer Kind the reused card needs is recomputed locally
(`pipeline.writerOverride?.kind ?? task.changeProcessing.writer.kind`) rather than exporting anything
from `MappingPipelineCard` — the phase doc's one open question, resolved in its own favor ("small either
way"). Verified against a real running instance — `tools/dev-harness`, a real Chromium browser via
Playwright (no browser extension available this session) — not just a clean `npm run build`/`lint`: the
toggle round-trips a saved override across a full page reload in both directions (on with a real cadence
value, and back off to `null`, not an empty object), and the writer-Kind resolution was checked against
a real `MsSqlMerge` mapping. Independent of 140/141 — pure SPA, over a backend phase 125 already shipped.

Updated 2026-09-15 (latest of all): **140 rescoped, and 141 split out of it**, both the same day 140 was
first opened — checking the real next `dotnet-windows` run's *actual* numbers (not another sample) while
answering a question about phase 134's own follow-ups showed the original scope ("verify 5 fixes, chase
1 failure") badly undersold the problem. The real picture: the first-ever `dotnet-windows` run showed
**270 of 447 `Api.Tests` failing** — this session's five fixes had only been checked against a *sample*
of that run's failures, not all of it. Reconciling exact counts across runs: the libgit2 cleanup fix
really did recover `Core.Tests`/`State.Tests`/most of `Cli.Tests`, and — confirmed by arithmetic, xUnit's
console runner only names failures/skips — phases 135 and 136's own real-Windows checkpoints (the real
`icacls` ownership test, the three real Event Log tests) all **passed for real**, the first actual
confirmation either phase has had. But `Api.Tests` moved only 270 → 253 failed, essentially unchanged
across every fix this session made, including the `Auth:Disabled`/Negotiate one — that fix was correct
for the one route it targeted but left the much larger `AuthenticatedApiFactory`-based population
(auth-*enabled*, so the same gate doesn't apply) still 500ing on the same
`IConnectionItemsFeature`/Negotiate mechanism. `Cli.Tests` also turned up 17 never-before-sampled
failures: real Linux-only-assumption gaps in `ToolCommandTests`/two more `SystemdServiceTests` cases,
and a distinct, unexplored Windows certificate/crypto failure cluster (`NewSelfSignedFileTests`,
`CertUsePemTests`, more). 140's own doc now carries all of this. Separately, `dotnet-integration`'s
~46-test remainder — flagged in phase 134's own follow-up as possibly explained by 140's
concurrent-install race fix — **checked directly: it isn't**, the same order of magnitude persists after
that fix landed, though the failure *shape* shifted from assembly-load exceptions to data/timing
mismatches. Split into its own phase (141) since it's a `ubuntu-latest` job issue with nothing to do with
Windows.

Updated 2026-09-15 (earlier than the note above, same day): **134 is done and removed** — every change reader stops full-loading;
`RunExecutor` captures the change feed's position (`IPositionCapturing`) before touching the table, hands
it to a new `IRunnerState.RequestInitialLoad` (Prerequisite, not journalled — it creates new work), which
persists it as `Pending`, sets `ReadHold.Loading`, and starts a Bulk Load batch segmented like an ordinary
reload. `LocalRunnerState.CompleteRun` promotes the pending watermark, clears the hold, and flips the
intent to `Changes` in one statement once the batch reaches `BulkLoadState.Completed` — matched purely on
the batch's own globally unique id, so an ordinary operator-triggered reload never touches it. This was the
first-ever PR in this repo (`#1`), using the new CI-gated handoff convention below, and it earned its
keep immediately: CI (specifically, Playwright actually starting the app) caught a real circular DI
dependency (`StateHost → LocalRunnerState → IInitialLoadEnqueuer → BulkLoadService → ProcessSupervisor →
StateHost`) that hung the API at startup with zero log output — invisible to `dotnet build`/unit tests —
fixed by resolving `IInitialLoadEnqueuer` through a `Lazy<>` instead of eagerly. Also found and fixed in
passing: an unrelated, days-old CI bug (phase 109i onward) where the `playwright` job never built
`DbDataSync.Cli`, which `globalSetup` needs to seed a real DuckDB install.

**Merged ahead of full CI confirmation, on explicit user instruction** — `dotnet-integration`'s last
completed run before the merge showed widespread failures with a uniform "Succeeded → Failed" / "N rows →
0" shape alongside repeated SQL Server `sa` login failures, consistent with a connectivity problem in that
run rather than real regressions, and the job has an otherwise clean history on `main` — but a retrigger
was in flight, unconfirmed, when the merge happened. Two narrower gaps also remain, named in the phase
doc's own "Known follow-up" section: a handful of Docker-backed driver test files beyond the three
directly fixed were never audited for the same full-load-assumption pattern, and configuring
`MsSqlChangeTrackingReader`/`MsSqlCdcReader`/`TriggerAuditReader` as a mapping's *Bulk Load* reader
override now throws instead of full-loading (a narrow, previously-untested regression). Whoever picks
this up next should check the retriggered CI run and decide on both gaps.

Updated 2026-09-14 (later than the note below): **136 is done and removed** — built in a background fork while 134
was in progress elsewhere, independently of the table above (never added to it — same situation as 135
and 137). `WindowsServiceEventLog` (new, `[SupportedOSPlatform("windows")]`) writes directly to the
Windows Event Log, bypassing `ILogger` entirely since the failure class this phase exists for (an
exception from `ServeCommand.Prepare()`/`DbDataSyncHost.Build()`) predates the DI container that would
carry one. `ServeCommand.RunAsync` now wraps its whole body in one outer try/catch, routed through a
new `Fail` helper (Event Log under a real Windows service, `Console.Error` otherwise) — the existing
`Prepare()`-specific catch nests inside it unchanged, so phase 135's own richer failure message (the
registered service's account/platform) still reaches the log, not a generic `Type: Message` line.
`ServiceCommand.Install` registers the event source at install time, elevated, rather than lazily.
Verified for real where this sandbox allows it (138 passed, 4 skipped, 0 failed — the 3 new Windows-only
tests report `[SKIP]` here, honestly, not faked); the real EventLog round-trip and a real installed
service's Error 1053 repro both remain genuinely unverified, named as such in the retrospective rather
than assumed.

Updated 2026-09-14 (earlier than the note above, same day): **133 is done and removed** — the full `Backfill` → `BulkLoad`
rename, plus the `BulkLoadConfig` pipeline it was for: a reader (defaulting to `BatchReload`), a cache
and a writer, at the replication with a per-mapping override, resolved through `PipelineResolution`
independently of `ChangeProcessingConfig` (cache/writer fall through to it when unset). Save-time
validation (`ParameterCheck.ThrowIfBulkLoadInvalid`) checks the mapping's fully resolved pipeline
unconditionally, not just an override, since even the `BatchReload` default can be unsupported by a
given driver. As decided 2026-09-14 (see below), no migration and no backwards compatibility: existing
state databases and any config carrying the old `backfillDegreeOfParallelism` key must be recreated.
134 is next — expect `BulkLoadBatchStore.GetRecentBulkLoads`, not `GetRecentBackfills` as its own doc
still said; the method was renamed here for full-rename consistency. That also means the audit note
below, written to plan phase 139 before 133 actually landed, needs one correction — see its own note.

Updated 2026-09-14 (later than the note above, same session): **138 and 139 join `todo/`**, found by an
explicit audit — walking recent phase retrospectives for backend work that shipped with no matching SPA
surface, requested by the user while 133/134 were mid-implementation in another session (133 has since
landed, per the note above — 139's own doc was corrected afterward to the endpoint/method names 133
actually shipped, `GET .../bulk-loads` and `GetRecentBulkLoads`, rather than the `.../bulkloads` guess it
was first written with). Two real, still-open gaps found (checked against everything after each: nothing
later closed either one). **138**: phase 125's own retrospective named it directly —
`TableMappingConfig.ReconcileOverride` has been fully supported by config, validation, resolution and
the scheduler since phase 125, and nothing has ever read or written it from the SPA. **139**: phase 107
built `BulkLoadBatches` (then `BackfillBatches`) and its endpoint "to support" a future history screen
and deliberately shipped none; phase 133's own doc reaffirms the gap is still there today. Sequenced
after 134 specifically because it turns every initial load into a bulk load — the volume this screen
needs to handle materially changes the moment 134 ships, which is also why 139's own design leans on
real keyset pagination rather than the flat `limit` phase 107 shipped. A third candidate,
`architecture/planning/todo/run-lag.md` (source/target watermark-age lag), was found and explicitly
**not** turned into a phase doc — it is still genuinely blocked on phase 034 (Postgres logical
replication) providing a second real example before its own "reader declares a lag capability" design
can be written, exactly as that doc's own 2026-08-28 update already says.

Updated 2026-09-14 (later than the note below): **137 is done and removed** — it was never added to the
table above; it was written as a `todo/` doc mid-session (see the 2026-09-14 note below) and picked up
directly, independently of the 133/134/034/035/038 queue. `DbDataSync.Cli.csproj` gained a `Publish`-time
`CopyPrebuiltSpa` copy target, and `ci.yml`/`release.yml` now build the SPA before packing — every
released nupkg to date had shipped with an empty `wwwroot` because `dotnet pack`/`publish` of the CLI
project never triggered the API project's own SPA build target. `ci.yml`'s `package` job also gained two
real assertions (packed nupkg content, running containers' `/` response) so this class of bug fails CI
next time rather than shipping unnoticed again.

Updated 2026-09-14 (later than the note below): **135 is done and removed** — same situation as 137
above: never added to the table, written as a `todo/` doc mid-session, picked up directly. `service
install`'s `GrantDataDirectoryAccess` now takes ownership (`icacls /setowner ... /T /C`, then a
now-recursive `/grant`) for every account including `LocalSystem`, not just named ones — the actual Error
1053 fix. Also picked up, agreed before implementation started: a `ServiceRegistration` marker
(`service-registration.json`, gitignored, at the data directory root) records which account/platform a
service was registered as, consumed by both the `Prepare()` ownership-failure message and a new
`ServiceRegistrationCheck` in `config check`. 136 (Windows service startup diagnostics reaching the Event
Log) is the one phase from that same mid-session doc that is still in `todo/`, untouched.

Updated 2026-09-14: **133 and 134 go to the top**, and the reason they are only being queued now is
worth recording. `planning/done/bulk-load-pipeline-and-the-initial-load-rule.md` was resolved on
2026-09-04 but named its phases A, B and C rather than numbering them — deliberately, to avoid claiming
a number before the file existed. Nothing then pointed at them: no `todo/` file, no row here, and the
only reference anywhere is the 2026-09-04 note below citing the doc as what retargeted phase 101's §1.
Ten days of other work went past, and the design read as shipped because phase 101's retrospective
discusses the Bulk Load pipeline at length while scoping it out. **A resolved planning doc whose phases
are never written is invisible** — every other doc in `planning/done/` names a `phase-NNN` file, and
this one could not.

133 leads because the `Backfill` → `BulkLoad` rename gets more expensive with every phase that ships:
107 made the word a table and a column, 108 made it a lane and a user-facing YAML key. It is a **full**
rename carrying **no migration and no backwards compatibility** — there are no serious installations
yet, so existing state databases and configs are recreated rather than upgraded. That is what makes the
full rename cheap today and is precisely why it should not wait; the same decision is unavailable the
moment anyone is running this for real. 134
carries the behaviour change and the deletion, and is the first thing ever to call `IPositionCapturing`
— built by phase 101, implemented by four readers, and with no caller since.

Updated 2026-09-11 (latest of all): **127** is done and removed — nuget.org Trusted Publishing works
end to end: a real stable release (`2026.9.11.532`) and two betas all shipped correctly through
`release.yml`, which moved mid-phase from a pushed tag to a manual `workflow_dispatch` (a `beta`
checkbox) that tags the released commit itself once nuget.org confirms the publish — no more
delete-and-recreate-a-tag cycle to retry a failed run. A `.claude/skills/nuget-release/` skill now
makes cutting a release a documented, one-command procedure (`scripts/release.sh [--beta]`). Six real
bugs/findings along the way, all in the phase doc — worth reading before assuming a release pipeline
"should just work" on a first real run against a public feed.

Updated 2026-09-10 (later than the note below): **126** is done and removed — the project's GitHub
hosting moved to `github.com/DbDataSync/DbDataSync` (public, full history, the old
`danshryock/DataSync` left untouched). **127** joins `todo/` alongside it — `release.yml` now has the
Trusted Publishing (OIDC) steps to push `DbDataSync` to nuget.org on a release tag, but it stays in
`todo/` rather than `done/` until the one thing this repo's automation cannot do for itself — creating
the Trusted Publishing policy on nuget.org — happens and a real tag exercises the whole path
end-to-end. See `architecture/planning/done/nuget-org-publishing-and-github-hosting-move.md`.

Updated 2026-09-10 (latest of all): 125 is done and removed — `ReconcileConfig` (replication-level, with
a per-mapping override), a scheduled cadence and an `AfterChangeStrategy` (`None`/`AfterAny`) for phase
124's delete-diff sweep, both persisted to YAML via new hand-written converters
(`DeleteGuardYamlConverter`/`AfterChangeStrategyYamlConverter` — phase 124 only ever needed JSON, this is
the first thing to actually save a guard/strategy to config). `SchedulerService.TickReconcileAsync` runs
every tick; a real logic bug was caught by the new scheduler tests and fixed before this shipped: the
plan's own "after-change floored by the cadence" wording, taken literally, made `AfterChangeStrategy`
observably a no-op (its own due-check was a strict subset of the cadence's), fixed by making `Every`
unconditional only under `NoAfterChangeStrategy`. A new `WorkQueue.DeleteGuardJson` column (closing a gap
phase 124's own plan left open) carries a scheduled sweep's resolved guard onto its work item. New "Delete
reconciliation" card on the replication's Pipeline tab; no mapping-level override editor in the SPA yet
(the config model and backend already support it) and no Playwright coverage added (flagged, not silently
skipped) — both explicit, scoped gaps. This closes the `watermark-delete-detection.md` arc (124 → 125).

Updated 2026-09-09 (earlier): 124 is done and removed — a keys-only delete-diff sweep
(`KeyReconcile` reader + `KeyReconcileDelete` writer, both engine-neutral, registered on MsSql and
Postgres), a `DeleteGuard` (`None`/`Ratio`, default 50%) protecting it, `RunKind.ReconcileDeletes` on the
backfill lane, and an on-demand trigger (`POST .../reconcile-deletes` + a "Reconcile deletes…" chrome
button). Two real gaps in the original plan resolved during implementation: `RunExecutor` needed a small
Kind-gated (not RunKind-gated) fix so the staging table it builds agrees with what a keys-only reader
actually projects, and a new `WorkQueue.DeleteGuardJson` column was needed as the channel the plan's own
"WithGuard mirroring WithSegment" line presupposed but nothing had built yet. A real writer bug (reusing
one `SegmentScope`'s parameters across two `DbCommand`s) was caught and fixed by the new segmented
integration test. **125** builds on it — `ReconcileConfig`, a scheduled cadence, an after-change
strategy — done too now, see above.

Updated 2026-09-09 (latest): 121 is done and removed — a second image, `docker build --target runtime`,
on `mcr.microsoft.com/dotnet/aspnet:10.0` with no SDK. `KnownLibraries` entries now carry a
`PinnedVersion`; a new hidden `dbdatasync internal build-catalog-cache` restores all seven into the
build stage, copied into both final images. `LibraryInstaller.InstallOrDeferAsync` (now what both API
controllers and `config library install` call) copies a pinned catalog version from that cache with no
SDK present, or defers anything else as a new `PendingRestore` state (`config library sync` finishes it
later) — surfaced on the Libraries screen and in `config library list`. Verified against real Docker
builds of both images on this sandbox, including a live MySQL round trip through the runtime-only
image's cache-copied driver. Independent of 122; nothing else changes as a result.

Updated 2026-09-09 (earlier): 122 is done and removed — a non-catalog `config library install`/`POST
/api/libraries` no longer requires `--factory-type`/`factoryType` up front: after the package restores,
`FactoryTypeReflector` scans the closure (inspection-only, via `MetadataLoadContext`) for a single public
`DbProviderFactory` subclass and uses it if found, only requiring one explicitly when the scan is
ambiguous or finds nothing. Independent of 121; nothing else changes as a result.

Updated 2026-09-09 (earlier): 123 is done and removed — `dbdatasync tool install`/`tool uninstall`
wire a `dotnet tool install --tool-path` copy of the CLI into the system `PATH` (a `/usr/local/bin`
symlink on Linux, an `/etc/paths.d` entry on macOS, the Machine `PATH` on Windows), and
`service install` now warns (or, Linux with the hardened default root, hard-refuses) when its own
executable is still sitting in a user profile. New `docs/install.md`; `tools/install-local-tool` for
CLI dev-loop iteration. Was independent of everything else in `todo/`; nothing else changes as a
result.

Updated 2026-09-09 (later than the note below): 120 is done and removed — the whole 116–120 arc is
now shipped. The container's default image moved to the .NET SDK base (`LibraryInstaller` shells out to
`dotnet publish`), `POST /api/libraries`/`DELETE /api/libraries/{id}`/`POST
/api/drivers/from-catalog` install and remove for real (both endpoints update the live in-process
registries immediately, not just on disk), and a real server-side restart-required flag replaced the
Configuration screen's old client-only one. **121** (a slim runtime-only image) and **122**
(`factoryType` reflection-assist) were the two follow-ons this arc always deferred — both now done, see
above.

Updated 2026-09-09 (earlier): 119 is done and removed — `GET
/api/libraries/search` proxies the public NuGet index (a new `DbDataSync:NuGetSearchEnabled` key
gates it), and the Libraries screen's search box, quick-add chips, and copyable install command all
build on it. 120 (the actual Install/Add buttons) is what's left of this arc.

Updated 2026-09-09 (earlier): 118 is done and removed — `GET /api/drivers` now
reports a driver's bound library and kind-name capabilities, a new `GET /api/libraries` (and the two
`known-*` catalog endpoints) back a new Admin → Drivers/Libraries tab pair, all read-only. 119–120
build on it in order, same as before.

Updated 2026-09-09 (earlier): 117 is done and removed — `KnownLibraries` is a
7-entry catalog now (a stable id, a real package id, a factory type, a display name and description),
and `KnownDrivers` (new, one `mysql.generic` entry) supplies `config driver install --from`'s bodies as
embedded YAML instead of a hardcoded CLI switch. 118–120 build on it in order, same as before.

Updated 2026-09-09: 116 is done and removed — `DbDataSync.Providers` is `DbDataSync.Libraries`
throughout, and a `driver.yaml` descriptor now references its library by id (`library: <id>`) instead
of carrying its own `factoryType`/`packages` block. 117–120 build on it in order, same as before.

Updated 2026-09-09: phases **116–120** join `todo/` as one arc — drivers and libraries visible and
manageable from the web console (`architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`).
They are internally sequential (116 → 120) and must be built in order; **116** (the `provider` →
`library` rename plus the descriptor's `library:` reference) is the gate for the rest. The arc is
independent of the 034/035/038 queue above and of the phase-109g–109i dependency-removal series —
relative priority against those is an open call. **121** and **122** are follow-ons, deferred until
116–120 are in production. (Both are now done — see above.)

Updated 2026-09-04 (latest of all): 105 is done and removed — the Overview → Provisioning tab (retitled
from "Target provisioning") now aggregates every table mapping's plan, grouped by connection and
database, deduplicated by statement, ordered database-scope before table-scope, individually selectable,
copyable per group and runnable as one batch. Nothing else in the ordering rationale below changes,
since 105 was already independent of everything above it.

Updated 2026-09-04 (later than everything below): 104 is done and removed — the run history endpoint
now takes `kind`, `mappingName`, `status` and an opaque keyset `cursor`, `watermark-times` takes the
identical four so the two never resolve to different pages, and the panel's old client-side
`all | failed | backfills` filter is gone in favor of the three server-side ones plus paging controls.
105 moves up to take 104's old spot; nothing else in the ordering rationale below it changes, since 105
was already independent of everything above it.

Updated 2026-09-04 (latest): 102 is done and removed — the Monitoring tab's rows now show and manage
every mapping's intent and hold, over phase 100's endpoints and phase 101's capability declarations.
104 moves up to take 102's old spot; nothing else in the ordering rationale below it changes, since 104
was already independent of 100–102 and only ever depended on 103 for where the panel lives.

Updated 2026-09-04 (yet later): 103 is done and removed — Runs is a Monitoring sub-tab now, Schedule
is on Overview, and the three `RefreshCountdown`s live in the header of the card or pane each one
describes rather than in the now-deleted `ShellActions` portal. 102 stays exactly where the queue jump
above put it, still pointed at the Current Status sub-tab 103 built — but 102's own doc still describes
the pre-103 tab layout, and correcting that description is 102's job when it lands, not something this
update reached into its file to fix.

Updated 2026-09-04 (even later): 101 is done and removed alongside 100, which had already landed but
was left in this table — an oversight, corrected here rather than left for 102 to notice. 101's own
scope shifted mid-implementation: `planning/done/bulk-load-pipeline-and-the-initial-load-rule.md`
retargeted its §1 partway through (see phase 101's own doc for the amendment), which is why its
implementation doesn't match its original design verbatim. 102 stays exactly where 100–101's queue jump
put it — a hold can currently only be set and cleared over the API, which 102 is what makes visible.

Updated 2026-09-04 (later still): 105 joins the list below 104. It depends on nothing above it and
nothing above it depends on it — placed here rather than higher because it eases a setup-time burden
rather than fixing anything broken, and 100–102 are still what an operator hits when a position
expires.

Updated 2026-09-04 (later): 103 goes above 100–102, and 104 below them. Not a judgement that the UX
work matters more than the intent/hold chain — 103 is small and entirely presentational, and 102 puts
new controls on the very tab 103 restructures. Building 102 first would mean designing those controls
against a layout that then moves under them, and 100 and 101 have not started, so the ordering costs
nothing. 104 is independent of all four and sits below them; it depends on 103 only for where the
panel it changes happens to live.

Updated 2026-09-04: 100–102 go to the top, in that order — they are one design split three ways and
have to land in sequence. What earns the queue jump is 101's half: a mapping whose source position
expires today fails on *every* scheduled tick, indefinitely, because there is no backoff or quarantine
anywhere in the codebase — another failed run and another notification each interval, burying every
other failure in that replication's history. The only remedy offered is a full reload, which on a large
table is hours to recover from a source that usually still holds most of what was missed.

Updated 2026-09-03: 096 is done and removed — the three daily-use defects are fixed, and both layout
ones now have assertions that were seen to fail against the broken code. It found a fourth defect on
the way out, which is *not* fixed and is not a phase yet: a target table created by the Setup card's
Apply button never gets its shape cached, so the mapping fails its first run
(`planning/todo/apply-button-does-not-cache-provisioned-columns.md`). That one is server-side and
blocks an ordinary flow, so it is likely to jump this queue once it is agreed.

Updated 2026-09-03: 095 went in at the top and is already done and removed — a mapping the bulk screen
creates now arrives with its columns cached and mapped, so it runs without anyone opening it. It jumped
the queue because it was much smaller than the three below it and it fixed a shipped screen that was
producing mappings which could not run at all. Those three engine phases are again what is left.

Set 2026-09-01; 083 done and removed — the admin-screen/config/certificate arc (079, 081, 082, 083) is
now fully shipped, end to end. What's left below 096 is the three engine phases that have been waiting
since distribution and auth moved above them — with 032 and 033 done there were three change-tracking
mechanisms and no way for anyone outside this repo to install any of them, and nothing guarding the
port. A phase moving up or down is an ordinary decision and only this table changes.

## What counts as a phase

A phase is a coherent, independently describable unit of delivered (or to-be-delivered) work — a
vertical slice of the system, a cross-cutting rework, or (as with Phase 0 of this project) a pure
design/architecture pass that produces documents rather than code. **Documentation and architecture
work is real work and gets a real phase document** — the test for "is this a phase" is "did we decide
or build something," not "did we write code."

**A backlog — a list of things deliberately *not* being done — is not a phase**, and must never be
numbered or labeled as one. `architecture/implementation-plan.md`'s "Backlog" section is the place for
that list; it intentionally has no phase number and no corresponding file in this folder, because
numbering it would create a numbered slot in this folder's sequence with nothing behind it. (This
folder's numbering had exactly that problem once — a "Phase 8" label on the backlog section with no
`phase-008-*.md` file to match, which was confusing and got fixed by dropping the number from the
backlog and renumbering the next real phase into that slot instead of leaving a gap.)

## File naming

`phase-NNN-short-title.md`, zero-padded to **three digits** (`phase-000-...` through
`phase-999-...`) so the sequence sorts correctly by filename indefinitely, regardless of how many
phases the project eventually has. Headings and prose inside a doc, and casual references elsewhere
(commit messages, code comments, conversation), can still say "Phase 8" naturally — the zero-padding
is a filename/sorting convention, not how the phase is spoken or written about.

**Add-on work — a small, discrete follow-up to a specific phase that doesn't warrant its own phase
number — gets a lowercase letter suffix directly on that phase's number**: `phase-007a-...`,
`phase-007b-...`, and so on, in the order the add-on work happened. Use this when the work is clearly
*of* a specific already-numbered phase (a rename, a small correction, a follow-up requested by the
user shortly after that phase landed) rather than new, independent scope — independent scope,
however small, gets the next full phase number instead. When unsure which it is, ask: "does this
depend on / only make sense in the context of one specific prior phase, or could it stand alone?" —
the former is a lettered add-on, the latter is its own numbered phase.

## Workflow

1. **Before implementation begins** on anything non-trivial, produce a complete design — via plan mode,
   a Plan subagent, or direct design work — and write it into `todo/` as one file per phase, in the
   same structure the `done/` docs use (see "Phase doc structure" below), adapted to be forward-looking
   (`**Status**: Planned, not started`, "What this phase will build," "How to verify when built,"
   "Open questions to resolve during implementation" instead of the past-tense equivalents). Number
   phases sequentially, continuing from the highest number in `done/` — never reuse a number, never
   leave a gap for a section (like the backlog) that isn't itself a phase.
2. **While implementing**, the `todo/` file is the plan of record — update it if the design changes
   materially during implementation (a rejected approach, a newly-discovered constraint), the same way
   any other part of the repo gets updated when reality diverges from an earlier design.
3. **Once a phase is implemented, verified (tests green, manual verification done where called for),
   and committed**, rewrite its `todo/` file as a retrospective — what was actually built, how it was
   verified, real bugs found and fixed, decisions made, what's explicitly still not built — and `git mv`
   it into `done/` in the same commit as the implementation. A phase's document and its implementation
   land together; there is no state where a phase is "done" in the repo's code but its plan still sits
   in `todo/`, or vice versa.
4. If a planned phase turns out to be bigger than expected mid-implementation, split it — write
   additional `todo/` phase files for the remaining scope (numbered after the current highest, same as
   any new phase) rather than silently absorbing unplanned scope into what was originally described as
   one phase.

## Follow-up work gets its own doc, not a paragraph

Adopted 2026-09-15, after real cost from not doing it: phase 134's own retrospective named two
follow-up items in a "Known follow-up / not done here" section, and a "worth a decision... in a
follow-up round" for a third. All three sat there, correct and unactioned, until phase 141 — reading
that doc for an unrelated reason — rediscovered them by chance. Between those two points, nobody
looking at `implementation/README.md`'s own build-order table (the thing this whole convention exists
to make "the order to work through" legible from) could see that work existed at all, because it was
never a `todo/` file — it was prose, inside a file that this folder's own structure says means *done*.

**A `done/` doc is a retrospective, not a backlog.** If, while writing one (or while finishing any
piece of work — a bug fix, an investigation, a code review), real follow-up work is identified —
something that should genuinely get done, not merely a boundary that was considered and deliberately
left alone — it does not stay as a paragraph in that doc. It becomes:

- **`architecture/planning/todo/follow-up-phase-NNN-short-title.md`**, if it's a diagnosed-or-not problem
  with no agreed plan yet (a bug with a known cause but no chosen fix, a "here's what I found" with real
  open questions) — see that folder's own README for the shape. The `follow-up-phase-NNN-` prefix names
  the phase whose own work surfaced this, zero-padded to three digits the same way phase doc filenames
  are — `follow-up-phase-134-...`, not `follow-up-phase134-...` — so it sorts and reads the same way. A
  follow-up that came out of more than one phase's own work (the same gap named in two different docs,
  say) takes every phase number it traces to: `follow-up-phase-136-140-...`. Every other planning doc
  keeps its plain descriptive name — the prefix is specifically for the "found while finishing phase
  N" case this section exists to stop losing.
- **`architecture/implementation/todo/phase-NNN-*.md`**, a new numbered phase, if the scope is already
  clear enough to build from directly — this one gets the next phase number in sequence, same as any
  other new phase, not the originating phase's own number.

Either way, the `done/` doc keeps only a short pointer to where the work now lives — a sentence, not
the elaboration — the same "a one-line 'see phase-012-...' is enough" rule
`architecture/planning/README.md` already states for its own `done/` folder, extended to this one. This
is not optional polish: a `done/` doc with real, unextracted follow-up work in it is exactly the state
that let phase 134's items go unseen for a full session's worth of unrelated work.

**This applies retroactively, not just going forward.** Reading an older `done/` phase doc — for any
reason, not only while working on a new phase in the same area — and finding a stated follow-up that
is still valid is the moment to extract it, the same as if it had just been written. Leaving it for
whoever reads that doc next is how it gets missed again.

Adopted 2026-09-14, for a problem this project ran into directly: a phase big enough to span more than
one working session (a large rename, a schema change) used to mean either one session blocking for
however long the full Docker-backed integration suite takes to run locally, or trusting a session's own
"tests passed" self-report and committing straight to `main`. Neither holds up — the first wastes a
session's whole turn babysitting `dotnet test` (and, tried by hand once, produced redundant polling
loops against the same log file instead of one session cleanly waiting on the other), and the second
means a broken `main` is discovered by whoever pulls next, not by the session that broke it.

So, for any phase substantial enough to risk spanning more than one session:

1. **One branch per phase**, `phase-NNN-short-title` (same slug as the doc), cut from `main` when work
   starts. Nothing about a phase in progress touches `main` until it merges.
2. **A session implements, runs the fast local checks only** (build, unit tests — not the Docker-backed
   integration suite; running that to completion is CI's job, not a session's), and before ending:
   - appends a `## Handoff — <date>` section to the phase's `todo/` doc: what's done, what's left, any
     decisions or deviations made, the branch name, and current CI status (or "not yet pushed"). This
     is the one file a later session — with no memory of this one — needs to read to pick the phase
     back up; there is no separate in-progress folder or ticket system, because the doc already is that
     record (see "Why this exists" below).
   - commits the doc and the code together, pushes the branch, and opens a PR if none exists yet (a
     draft PR is fine mid-iteration).
   - ends its turn. It does not wait on CI.
3. **CI is watched asynchronously** by whichever session is orchestrating — polled or subscribed to,
   never by a session blocking on a local `dotnet test` run against the Docker containers, which is the
   pattern that produced the redundant polling loops above.
4. **On a red run**, a session (the same one resumed, or a fresh one) reads the Handoff section plus the
   actual CI failure output, fixes it, updates Handoff, commits, pushes, and ends. Repeat.
5. **On green**, merge the PR, and move `todo/` → `done/` in that same merge — exactly step 3 of
   "Workflow" above, just gated on CI's answer instead of a session's own say-so.

A phase small enough to implement, verify locally, and commit within one session does not need any of
this — it is for the case this section exists to name: a phase that will outlive the session that
started it.

## Phase doc structure

Match the existing `done/` docs' shape:

- `# Phase N — Title`
- `**Status**` (`Planned, not started` in `todo/`; `Complete` in `done/`) and `**Plan reference**`
  (pointing at `implementation-plan.md` and/or the design source, e.g. a plan-mode file's content
  transcribed here, or a prior phase doc's "Design history").
- What this phase builds (or built) — concrete enough to name real types, files, endpoints, schema.
- How it will be (or was) verified — specific tests, specific manual checks.
- Decisions made, and real bugs found (for `done/` docs — these are often the most valuable part of a
  retrospective; don't skip them for the sake of brevity).
- What's explicitly out of scope / not built, so a later phase doesn't have to rediscover the boundary.
  **A deliberate boundary — considered and rejected, or deferred on purpose — is fine as prose here.**
  Anything that is actually *outstanding work* (a known bug, a gap, a "worth a decision in a follow-up
  round") is not: see "Follow-up work gets its own doc, not a paragraph" below.
- Open questions, for `todo/` docs where something is genuinely undecided and expected to be resolved
  during implementation rather than before it.

## Why this exists

Design work that only lives in a chat session or a plan-mode scratch file disappears the moment that
session ends — it can't be reviewed, referenced by a future phase, or picked up by anyone (human or
AI) who wasn't in that specific conversation. Treating "planned but not yet built" as a real, versioned
artifact in the repo — not a temporary file, not something reconstructed from memory — is what makes
the plan for any given piece of work as durable and inspectable as the code that eventually implements
it.
