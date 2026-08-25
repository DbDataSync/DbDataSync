# Phase 2 — Central SQLite State Store

**Status**: Complete
**Plan reference**: `architecture/implementation-plan.md` § Phase 2

## What was built

All in `src/DataSync.State`, the single access point every process (API and every TaskRunner
instance) is meant to use for run/log/watermark persistence — per the confirmed decision to use one
central SQLite database rather than per-task files.

**Schema** (`Migrations.cs`), applied via `PRAGMA user_version` versioning (no external migration
framework — five tables is not enough to justify one): `Tasks`, `TaskRuns`, `ChangeWatermarks`,
`Logs`, `RunLocks`, matching the sketch in `architecture/detailed-design.md` §3.7.

**`StateDatabase`**: owns the connection string and runs migrations once at construction. Every
connection it opens gets `PRAGMA journal_mode = WAL` and `PRAGMA busy_timeout = 5000` — the two
concurrency mitigations the architecture doc calls out as required for choosing a central database.

**`SqliteRetry`**: a thin retry-with-backoff wrapper (5 attempts) around any write, catching
`SQLITE_BUSY`/`SQLITE_LOCKED` specifically. Defense-in-depth on top of `busy_timeout` for the case
where contention outlasts the timeout under real load.

**Stores**, each a thin class over `StateDatabase`, all writes going through `SqliteRetry`:
- `TaskRunStore` — upsert a task; start/complete a run; fetch one run, a task's run history, or all
  runs still marked `Running` (for the API-startup orphan-reconciliation described in
  `detailed-design.md` §3.1).
- `ChangeWatermarkStore` — get/set a per-(task, source table) cursor.
- `RunLockStore` — `TryAcquire`/`Release`/`IsLocked`, backed by an `INSERT ... ON CONFLICT DO
  NOTHING` so concurrent acquire attempts for the same task resolve to exactly one winner at the
  database level, not via application-side locking.
- `LogWriter` — batches log lines in an in-memory queue, flushing in one transaction at a 50-line
  threshold, on a 2-second `PeriodicTimer`, or on `Dispose()`. Also serves reads (`GetLogs`), flushing
  first so a caller never misses its own just-buffered lines.

## Decisions made this phase

- **`PRAGMA user_version` instead of a migration library/framework.** Five tables, no cross-database
  portability requirement (SQLite only, per the architecture), and one array of SQL scripts is enough
  to track and apply schema changes in order. Reaches for something heavier (EF Core migrations,
  DbUp, etc.) only if schema evolution actually gets complicated later — not preemptively.
- **`LogSeverity`, not `LogLevel`, as the enum name.** `Microsoft.Extensions.Logging.LogLevel` will be
  in scope once `DataSync.Api` and `DataSync.TaskRunner` wire up standard .NET logging (later
  phases); picking a distinct name now avoids a same-name-different-type collision at every call site
  that ends up needing both.
- **Concurrency was validated with concurrent in-process writers against the same SQLite file**,
  rather than literally spawning separate OS processes in a test. `Microsoft.Data.Sqlite` opens a
  genuine separate native `sqlite3*` handle per `SqliteConnection`, so file-level locking, WAL, and
  `busy_timeout` all behave identically to the multi-process case — a real OS-process-based version
  of this test is deferred to Phase 7 (end-to-end validation with actual spawned `TaskRunner`
  processes), where it's a natural byproduct rather than something to construct artificially now.

## Verified

- `dotnet build` / `dotnet test` — full solution green, 38 tests total (21 new in
  `DataSync.State.Tests`).
- Schema/migration: tables created on first open; re-opening an existing database file doesn't
  attempt to re-run `CREATE TABLE` (would otherwise fail on the second open); WAL mode is confirmed
  active via `PRAGMA journal_mode`.
- `TaskRunStore`: start→complete round-trip (status, row counts, error summary, timestamps);
  run-history ordering (newest first); `GetRunningRuns` correctly excludes completed runs; **50
  concurrent run start/complete pairs from parallel threads all persist correctly** with no lost
  writes or unhandled exceptions.
- `ChangeWatermarkStore`: set/get round-trip; overwrite semantics; correctly scoped per
  (task, source table) pair.
- `RunLockStore`: acquire/release lifecycle; **20 concurrent `TryAcquire` calls for the same task
  resolve to exactly one winner**.
- `LogWriter`: lines are invisible until flushed (explicitly, or via the 50-line auto-flush
  threshold); `Dispose()` flushes any remaining buffered lines; **8 concurrent writers × 200 lines
  each (1,600 total) all land in the database with none lost**.

## Notes / things to revisit later

- Nothing calls into `DataSync.State` from `DataSync.Api` or `DataSync.TaskRunner` yet — that's Phase
  5 and Phase 4 respectively, same pattern as Phase 1's `ConfigRepository`.
- `LogWriter`'s periodic-flush timer means a `LogWriter` that's never disposed leaks a background
  task; every real caller (API host, TaskRunner's `Program.cs`) needs to own its lifetime properly
  (e.g. a `using` in TaskRunner's short-lived process, DI-managed disposal in the API) — flagged here
  so it isn't missed when Phase 4/5 wire it in.
- Real multi-*process* contention (not just multi-thread) gets exercised naturally once Phase 7 spins
  up actual concurrent `TaskRunner` processes against the shared state file; if that surfaces
  contention this design didn't anticipate, `detailed-design.md` §8 already names the fallback
  (per-task SQLite files behind the same store interfaces).
