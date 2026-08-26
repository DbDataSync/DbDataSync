# Phase 7 — End-to-End Validation

**Status**: Complete
**Plan reference**: `architecture/implementation-plan.md` § Phase 7

## What was built

- **`docker-compose.yml`** — two genuinely separate SQL Server containers, `mssql-source`
  (`localhost,14330`) and `mssql-target` (`localhost,14331`), each with its own named volume and
  healthcheck. Deliberately two real server *instances*, not two databases sharing one instance —
  every prior phase's automated tests had only ever exercised source and target as different
  databases on the same server; this is the first setup that proves replication actually works across
  distinct database engines with no shared state between them, which is the whole point of "end-to-end
  validation." `mssql-source` keeps the same port (`14330`) and SA password
  (`DataSync_Test_Pw1`) every existing test already defaulted to, so nothing else needed to change —
  `mssql-target` is purely additive. The old ad hoc `datasync-mssql` container (started by hand
  earlier in this project, no compose file backing it) was retired in favor of this.
- **`tests/DataSync.Api.Tests/CrossInstanceEndToEndTests.cs`** — the plan's "manual end-to-end test"
  encoded as a durable, automated `Category=Integration` test: an initial full load of 3 rows across
  the two real containers, then **two** separate insert/update/delete cycles (touching distinct
  primary keys each cycle, per the Change Tracking semantics noted below), with every stage confirmed
  by querying the real target database directly — not by trusting the API's reported row counts.
- **`tests/DataSync.Api.Tests/ConcurrentRunsIntegrationTests.cs`** — a stress test answering
  `detailed-design.md` §8's open "central SQLite contention in practice" question with real data:
  triggers 8 independent replications at once (8 real spawned `DataSync.TaskRunner` child processes
  writing to the shared central state database concurrently), confirms every one succeeds with
  correct row counts, and directly verifies every target table's actual content.
- **CI** (`.github/workflows/ci.yml`) gained a `dotnet-integration` job — the existing `dotnet` job
  had an explicit comment deferring "standing up a real SQL Server instance in CI" to this phase. It
  now stands up the same two-container topology as `docker-compose.yml` via GitHub Actions service
  containers and runs the full `Category=Integration` suite, closing that gap.
- **`README.md`** (new) — prerequisites, `docker compose up`, build, run the API, run the SPA, define
  and run a replication through the UI, run the test suites. Written to be followed by someone who
  has never seen this codebase before, per the phase's exit criteria.
- **`architecture/detailed-design.md` §8** updated with the real findings below, replacing the two
  "should be validated" open items with what was actually found.

## How this was verified

Ran the new integration tests directly against the real, compose-managed containers (not just
against `TestApiFactory`'s in-memory host abstractions) — `CrossInstanceEndToEndTests` passed on
first try after the delete-propagation semantics were confirmed (see Decisions);
`ConcurrentRunsIntegrationTests` found a real bug on its first run (see below), was fixed, then run
several more times back-to-back with no flakiness. Reran the full non-integration suite (81 tests),
the full integration suite together (6 tests, all passing including the two new ones), and the
Phase 6 Playwright golden-path suite (7 tests) against the new containers — everything green,
including its `globalTeardown` cleanup.

## A real bug found by the concurrency stress test

`ConcurrentRunsIntegrationTests` failed its very first run — not on the state store this item was
originally worried about, but on **config writes**: creating 8 replications' configs concurrently
(each a `PUT` that triggers `GitCommitService.CommitChanges`) threw
`LibGit2Sharp.LockedFileException: the index is locked; this might be due to a concurrent or crashed
process`. libgit2 holds a real lock file (`.git/index.lock`) for the brief instant it writes the
index, but two genuinely simultaneous `Commands.Stage`/`repo.Commit()` calls from the same process
can still collide inside that window — nothing before this phase had ever exercised truly concurrent
config saves (every earlier test, including all of Phase 6's, saved one thing at a time). Fixed with
an in-process `lock` around `GitCommitService.CommitChanges`'s write path: `GitCommitService` is
registered as a DI singleton and the API process is the *only* writer of the config repo
(`DataSync.TaskRunner` only reads config), so serializing writes within that one process fully closes
the bug with no cross-process coordination needed. Full details and the resulting update to
`detailed-design.md` §8's "multi-user concurrent config editing" open question are in that section.

The central SQLite state store, by contrast, showed **no contention failures** at 8 concurrent runs,
run repeatedly — the `busy_timeout`/`SqliteRetry`/self-healing-schema mitigations already in place
(Phase 2, hardened in Phase 6) held up fine at this scale. See `detailed-design.md` §8 for the full
writeup of both findings.

## Decisions made this phase

- **Two containers, not two databases-on-one-instance, for the new cross-instance test** — the
  existing single-instance pattern (used by every earlier phase's tests) already proves the driver
  logic works; what hadn't been proven yet is that nothing in the system silently assumes source and
  target share a server (e.g. a connection string reused by accident, a metadata query that leaks
  cross-database). Using genuinely separate containers is the only way to actually rule that out.
- **Change cycles touch distinct primary keys, never reusing one within the same cycle** —
  confirmed via investigation into `MsSqlChangeTrackingReader`/`MsSqlMergeWriter` before writing the
  test: SQL Server Change Tracking reports one *net* change per distinct PK since the last sync
  version (two updates to the same row collapse into one; an insert-then-delete of the same row
  within one window can vanish from the change set entirely). Touching the same PK twice in one
  cycle would make a naive "N operations ⇒ N rows" assertion fragile for reasons that have nothing to
  do with a bug — so the test avoids it, matching the existing precedent in
  `MsSqlPipelineTests.FullLoad_ThenIncrementalChanges_ReplicatesCorrectly` and
  `RunExecutorIntegrationTests.FullPipeline_FullLoadThenIncrementalRun_ReplicatesAndRecordsState`.
- **`GitCommitService`'s fix is a plain in-process `lock`, not a retry-with-backoff** (unlike
  `SqliteRetry`'s approach to the analogous SQLite problem) — because the only realistic writers of
  this repo are concurrent requests within the single API process, a lock fully serializes and
  eliminates the race rather than just reducing its probability, and git commits are fast enough that
  blocking a request thread briefly is a non-issue at this scale. A retry approach would make sense
  only if writers could span multiple processes, which they don't here.
- **CI integration job runs only the `.NET Category=Integration` suite, not the Playwright suite** —
  standing up two SQL Server service containers plus a headless browser plus `npm ci` in the same CI
  job was judged more complexity than this phase's exit criteria required (a documented, working
  *local* setup). Running the Playwright suite in CI is reasonable future work, not blocking here.

## Notes / things to revisit later

- **Multi-user concurrent config editing**'s remaining open question (documented in
  `detailed-design.md` §8) is now purely a UX/policy one: concurrent saves to the *same* replication
  no longer crash, but still resolve last-write-wins with no warning to the editor who lost. Not
  addressed this phase.
- Central SQLite contention was validated at 8 concurrent runs, a realistic v1 scale, not at
  significantly higher concurrency. The per-task-SQLite-files fallback described in
  `detailed-design.md` §3.7 remains available if a real deployment ever needs it.
- No auth/authz model exists yet (`detailed-design.md` §8, unchanged — still open, still explicitly
  out of scope for v1).
