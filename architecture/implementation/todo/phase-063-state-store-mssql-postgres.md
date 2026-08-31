# Phase 63 — Supporting SQL Server and PostgreSQL as the state store (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/state-store-mssql-postgres.md`

## What this covers

Move the dialect abstraction (and the canonical-type system it depends on) into `DataSync.Core`, then
rebuild `DataSync.State`'s eight store classes and its migrations against that abstraction instead of
`Microsoft.Data.Sqlite` directly — so the state store can run on SQLite (still the default), MSSQL, or
Postgres. Cross-engine migration tooling is explicitly a separate follow-up, not part of this phase.

## 1. Relocate the dialect and canonical-type types into `DataSync.Core`

- `SqlDialect`, `MsSqlDialect`, `PostgresDialect` move from `DataSync.Drivers.Generic`/
  `DataSync.Drivers.MsSql`/`DataSync.Drivers.Postgres` into `DataSync.Core`.
- `CanonicalType`, `CanonicalTypeKind`, `RenderedColumnType` move from `DataSync.Drivers.Abstractions`
  into `DataSync.Core` alongside them — both already sit downstream of `Core` in the project graph, so
  this is a clean move with no new dependency edges anywhere.
- `IDialectProvider` (a driver exposing "the SQL dialect I speak") is unchanged in shape — it now points
  at a `Core`-defined type rather than a `Drivers.Generic`-defined one. No behavior change for any
  existing replication code; this is a relocation, not a redesign.
- Update every existing `using DataSync.Drivers.Generic;`/`using DataSync.Drivers.Abstractions;` reference
  to `SqlDialect`/`CanonicalType`/etc. across the driver projects and their tests.

## 2. `DataSync.State` against the dialect abstraction

- `StateDatabase` gains an engine-neutral connection-opening path — a `DbConnection` factory keyed by
  engine kind (SQLite/MsSql/Postgres), each opened via the matching ADO.NET provider
  (`Microsoft.Data.Sqlite`, `Microsoft.Data.SqlClient`, `Npgsql` — the latter two already dependencies of
  this solution via the existing drivers).
- `Migrations.cs`'s hand-written SQLite DDL becomes per-engine, rendered through the relocated
  `SqlDialect` (identity/serial column syntax, type differences) — full table inventory: `Tasks`,
  `TaskRuns`, `ChangeWatermarks`, `Logs`, `RunLocks`, `WorkQueue`, `Users`, `UserCredentials`, `Sessions`,
  `Invites`, and any others present at implementation time.
- Each of the 8 store classes (`TaskRunStore`, `WorkQueueStore`, `RunLockStore`, `ChangeWatermarkStore`,
  `UserStore`, `SessionStore`, `InviteStore`, `VerificationResultStore`, plus `LogWriter`,
  `RunMetricsStore`) moves its raw SQL through the dialect (quoting, parameter naming, upsert syntax)
  instead of hardcoded SQLite text.
- `SqliteRetry`'s busy-retry pattern stays **SQLite-only** — confirmed not needed for MSSQL/Postgres,
  whose concurrency model doesn't produce the same file-lock contention. No equivalent workaround is
  built for the other two engines unless real testing surfaces one.

## 3. Engine selection

`ApiOptions` gains an engine kind + connection string, alongside `StateDbPath` (which stays meaningful
only for the SQLite case). Chosen once at deployment, not a runtime-switchable setting. **SQLite remains
the default** — an unconfigured deployment behaves exactly as it does today.

## What this phase does not build

- Cross-engine migration (moving an existing SQLite state store to MSSQL/Postgres, or between the other
  two) — tracked as its own follow-up task.
- Any change to `IDialectProvider`'s contract or to replication-side dialect behavior — this is a
  relocation of existing types, not a redesign of what they do.
- A deadlock/contention retry pattern for MSSQL/Postgres, unless investigation during implementation
  finds a real need for one.

## How to verify when built

- A fresh deployment with no state-engine configuration still uses SQLite, unchanged from today's
  behavior — full existing state-store test suite green with zero configuration changes.
- The same state-store test suite passes against a real MSSQL instance and a real Postgres instance
  (`tools/dev-harness`'s existing containers are the natural fixture), each configured via the new
  `ApiOptions` setting.
- `Migrations.cs`'s DDL creates equivalent, correctly-typed schemas on all three engines (identity/serial
  columns behave as expected, e.g. `TaskRuns.RunId` round-trips correctly as each engine's key type).
- Every existing replication-side test (which exercises `SqlDialect`/`CanonicalType` through their new
  `Core` location) stays green — confirms the relocation didn't change behavior for the drivers.
- Full suite green across all three configurations.

## Open questions

- Exact `ApiOptions` shape for engine selection (a single connection-string-with-provider-hint, or
  separate engine-kind + connection-string fields).
- Whether any store's query needs an engine-specific rewrite beyond what `SqlDialect` already smooths
  over (e.g., an upsert pattern that isn't just parameter/quoting syntax) — expect at least one surprise
  here; `Migrations.cs`'s full inventory needs auditing table by table during implementation, not assumed
  uniform.
