# Supporting SQL Server and PostgreSQL as the state store

**Status: draft, 2026-08-31 — scoping against the current state-store implementation before writing an
implementation phase. Several real open questions, including one architectural layering question worth
resolving before committing to a shape.**

## The ask

Support MSSQL and Postgres as storage engines for the state database, alongside (or instead of) SQLite.

## What exists today, and how big this actually is

`DataSync.State` is **entirely SQLite today**, not abstracted behind any provider-neutral layer — this is
a bigger lift than it might first look:

- **8 store classes** (`TaskRunStore`, `WorkQueueStore`, `RunLockStore`, `ChangeWatermarkStore`,
  `UserStore`, `SessionStore`, `InviteStore`, `VerificationResultStore`, plus `LogWriter`,
  `RunMetricsStore`) call `Microsoft.Data.Sqlite`/`SqliteConnection` directly — ~2,400 lines total in
  this project.
- **`Migrations.cs`** hand-writes SQLite DDL directly (`INTEGER PRIMARY KEY AUTOINCREMENT`, SQLite's
  loose column typing) — every `CREATE TABLE` needs a per-engine rendering (MSSQL's `IDENTITY(1,1)`,
  Postgres's `GENERATED ALWAYS AS IDENTITY` or `SERIAL`).
- **`SqliteRetry`/`StateDatabase`'s `busy_timeout`** exist specifically to paper over SQLite's
  single-writer-file-lock behavior under concurrent access. **This machinery becomes moot, not just
  swapped out, for a real client-server engine** — MSSQL/Postgres handle concurrent readers/writers via
  normal locking/MVCC without an application-level busy-retry loop. Worth stating plainly: this isn't
  "port the same concurrency workaround to two more engines," it's "two of the three engines don't need
  this workaround at all."

This is the same *shape* of problem the replication side already solved — one engine-neutral surface,
several engine-specific renderings underneath — via `SqlDialect` (`DataSync.Drivers.Generic`), already
mature for both MsSql and Postgres. That precedent is the reason this is very buildable, not a reason to
assume it's small.

## The layering question — resolved 2026-08-31: dialects move to `DataSync.Core`

**`SqlDialect`, `MsSqlDialect`, and `PostgresDialect` relocate into `DataSync.Core`.** Not reused by adding
a new dependency edge (option A) and not duplicated into a state-specific concept (option C) — moved, so
that both `DataSync.State` and `DataSync.Drivers.*` sit downstream of the same abstraction instead of one
depending on the other. `DataSync.State` already depends on `DataSync.Core` today, so this needs **no new
project reference** for the state store to reach dialects at all.

This also settles the framing the doc had wrong: a dialect isn't inherently *a driver's* thing that the
state store would be borrowing — **a driver may provide a dialect, but a dialect isn't necessarily tied to
a driver.** `IDialectProvider` (a driver exposing "the SQL dialect I speak") stays exactly as it is; what
moves is the dialect type itself, which was only ever living in `Drivers.Generic` because that was the
first and, until now, only consumer.

### A real dependency this move surfaces: `CanonicalType`

`SqlDialect.ToCanonicalType`/`RenderColumnType` are typed against `CanonicalType`/`RenderedColumnType`
(`DataSync.Drivers.Abstractions`) — the cross-engine column-type-mapping system built for *replication*
(translating a source's native column type to a target's). The state store has no use for this; its
schema is fixed and internal, not mapping arbitrary customer columns across engines. Moving the whole
`SqlDialect` class as one piece means `CanonicalType` and friends need to move to `DataSync.Core` too
(`DataSync.Drivers.Abstractions` already sits downstream of `Core`, so that direction is also clean) —
otherwise `SqlDialect` in `Core` would need to reference a type living in a project *above* it, which
recreates the exact layering problem this move exists to avoid.

**Resolved 2026-08-31: `CanonicalType`/`CanonicalTypeKind`/`RenderedColumnType` move to `DataSync.Core`
too**, alongside the dialects — they're general type-system concepts, not really driver-specific either,
and this keeps `SqlDialect` whole rather than splitting it into a lean Core piece plus a
replication-specific extension. `DataSync.Drivers.Abstractions` (currently where these types live) already
sits downstream of `Core`, so this is a clean move in the same direction as the dialects themselves —
nothing above `Core` needs to keep a definition `Core` now owns.

## Resolved: SQLite stays the default

MSSQL/Postgres are additional options, not a replacement. An unconfigured deployment keeps behaving
exactly as it does today.

## Resolved: cross-engine migration is a follow-up, not part of this task

Moving an existing SQLite state store to MSSQL/Postgres (or between the two others) is explicitly **out of
scope here** and tracked as its own follow-up task once this lands. This phase only needs to support
*starting* on any of the three engines, not moving between them later.

## Other real questions, not yet answered

1. **Is engine choice a runtime setting or fixed at deployment?** Presumably a connection string + engine
   kind in `ApiOptions` (matching `StateDbPath` today), chosen once when a deployment is stood up.
2. **Does `SqliteRetry`'s retry-with-backoff pattern have any equivalent need on MSSQL/Postgres**, or does
   the question simply not apply there (their concurrency model doesn't need it)? Leaning "doesn't apply,"
   worth confirming there's no analogous case (e.g., a deadlock-retry pattern) before assuming zero
   workaround code is needed for the other two engines.
3. **Full DDL inventory**: `Migrations.cs`'s tables (`Tasks`, `TaskRuns`, `ChangeWatermarks`, `Logs`,
   `RunLocks`, `WorkQueue`, `Users`, `UserCredentials`, `Sessions`, `Invites`, and whatever else has landed
   since) all need per-engine DDL — a full accounting belongs in the phase doc.

## What this is not

Not a reopening of `state-store-concurrency.md`/phase 39's DuckDB question — that was about an
analytical-engine tradeoff for a *specific* concurrency model (single-writer, remote-apply). This is a
different, simpler ask: support two mature, already-integrated relational engines as the backing store,
for operational reasons (infrastructure an operator already runs), not a performance rearchitecture.

---

# Outcome

Agreed, as `implementation/todo/phase-063-state-store-mssql-postgres.md`.

**Next step**: confirm the `CanonicalType` move (question 4), then this is ready for a proper
implementation phase doc — likely a large one given the ~2,400 lines of SQLite-specific code in
`DataSync.State` alone, before any new engine-specific store code is even written.
