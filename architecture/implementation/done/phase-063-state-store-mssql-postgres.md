# Phase 63 — Supporting SQL Server and PostgreSQL as the state store

**Status**: Complete
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

---

# Retrospective

The plan doc predicted "expect at least one surprise here" about engine-specific rewrites. There were
five, all found by running against real servers, and none of them findable any other way — every one
passes on SQLite.

## The relocation was the easy half, and the compiler found what the plan missed

Moving `SqlDialect` and `CanonicalType` into `Core` went as the plan described. Two types it did not
name came with them, both surfaced immediately by the compiler and both pure SQL helpers with no
driver dependency of their own:

- **`CanonicalTypeSpec`**, which every dialect's `ToCanonicalType` parses through.
- **`MsSqlIdentityInsert`**, which `MsSqlDialect` calls directly. It was `internal` to the MsSql
  driver and is now `public` in `Core` — a widening, worth naming rather than doing quietly.

The check that mattered was that the whole suite passed afterwards **with exactly the counts it had
before**. A relocation that changes behaviour is a rewrite wearing a relocation's commit message.

## `StateDialect` is separate from `SqlDialect`, on `SqlDialect`'s own instruction

The obvious reading of the plan is "the state store uses `SqlDialect`". Its class doc says otherwise,
and says it plainly: anything differing *structurally* — it names bulk loading, upsert syntax and
identity handling — belongs in an engine-specific implementation, and if generalising something there
would need a flag per engine, that is the signal it does not belong there.

Upsert, identity DDL and row limiting are that list almost exactly. So `StateDialect` composes a
`SqlDialect` for the mechanical parts it genuinely does share (quoting, parameter spelling) and owns
the structural ones. That also keeps the two honest about their different scopes: `SqlDialect` serves
replication, where the schema belongs to somebody else and type mapping has to be general; the state
store's schema is ten tables this project wrote, and what it needs is smaller and far more
opinionated.

## The port kept the SQL, and that was the point

Every store still writes one readable SQL string with `$name` placeholders, and
`StateDatabase.Command` rewrites them per engine. The alternative — assembling each query from dialect
calls — was rejected because this was a port of ~2,400 lines whose entire claim is that nothing
changed in meaning, and interpolating a call per parameter would have made every one of those lines a
new thing to verify.

`Migrations.cs` stayed one file for the same reason, in tokens rather than three copies. The engines
disagree about four things in DDL and nothing else; three copies would triple the cost of every future
migration and guarantee one eventually drifts.

## The five findings

Every one of these passes on SQLite, which is why the cross-engine tests run on all three engines
rather than only the two new ones.

1. **Partial unique indexes, the biggest one.** The work queue's `UX_WorkQueue_InFlight` is unique
   only over the in-flight statuses, and the log's `UX_Logs_SourceKey` only where the key is not null.
   An insert-or-ignore that omits that predicate is not a syntax error — it is a silently wrong
   answer, and the queue's version means a mapping that had ever run could never be queued again. The
   predicate now travels with the conflict target, and becomes part of the `MERGE` match on SQL
   Server, where partial indexes have no equivalent at all.
2. **The MERGE predicate qualifier rewrote words inside string literals.** `Status IN
   ('Pending','Claimed','Running')` became `target.Pending`. Caught by the queue test; it is the kind
   of bug that would otherwise have surfaced months later as a mapping that mysteriously never runs
   twice.
3. **Integer width.** `GetInt32` against a `BIGINT` column throws rather than widening, on both new
   engines. Which width the DDL chose is an implementation detail of each engine, so insisting on it
   at every call site would make ten readers depend on it — the conversion now lives in one reader
   helper.
4. **SQL Server requires a derived table to be named.** The pruning subquery was anonymous, which
   SQLite allows.
5. **Postgres will not plan a statement when a parameter is null and appears only in an `IS NULL`
   test** — it has nothing to infer a type from, and both pruning caps are exactly that on the
   ordinary path. Typed explicitly, which the other two engines do not care about.

## Decisions the phase doc left open

- **Engine selection is two fields, not one connection string with a provider hint.** A connection
  string's shape already differs per provider, so a hint embedded in it would have to be parsed back
  out before the string could be used — and a mistake would surface as a driver-level parse error
  rather than as "you named an engine that does not exist".
- **An unrecognised engine name falls back to SQLite** rather than refusing to start. A typo should
  not take down an API that has a perfectly good store already; a missing connection string for a
  *valid* non-SQLite engine does throw, because there is nothing to fall back to that the operator
  would want.
- **SQLite keeps its own constructor and its own `StateDbPath` setting.** A deployment that has never
  heard of this phase reaches exactly the code it always did, which is the strongest available form
  of "the default is unchanged".
- **`SqliteRetry` is gone, replaced by `StateDatabase.Retry` asking the dialect.** Only SQLite ever
  says yes — confirmed rather than assumed, per the plan's second open question. Its loop exists
  because one writer holds a lock on a *file* and everyone else is refused; a client-server engine
  queues that contention internally and the caller simply waits.
- **`{{key}}` versus `{{text}}` is stated in the schema, not inferred.** It exists for SQL Server
  alone, which cannot index `NVARCHAR(MAX)`. Inferring it would mean asking "is this column indexed
  anywhere in the whole file, including three migrations later", which is not a question a template
  should be answering at render time.
- **`NVARCHAR(450)`** for those columns — safe for both a clustered (900-byte) and non-clustered
  (1700-byte) key at two bytes per character, and the same number ASP.NET Core Identity settled on for
  the same reason. Every value this schema keys on is a name, an id, a GUID or a hash.
- **`GENERATED ALWAYS AS IDENTITY` rather than `SERIAL`** on Postgres: the standard spelling, and the
  one that refuses an explicit value instead of quietly accepting one and desynchronising the
  sequence. Nothing here ever supplies its own id.
- **`UserStore.Any()` became an `EXISTS` rather than a `LIMIT 1`.** SQL Server's row limiting requires
  an `ORDER BY`, and there is no meaningful order in which to ask whether anybody exists at all —
  `EXISTS` is the question actually being asked.
- **A whole scratch database per cross-engine test run.** The DDL names its tables unqualified, which
  is how a real deployment runs it; a fixture using a schema or a table prefix would be testing
  something the product does not do.

## Verification

- `CrossEngineStateTests` (13 cases × 3 engines = 39) — schema creation on every table; reopening not
  reapplying migrations; upsert overwriting rather than duplicating; a lock granted once and refused
  after; **the queue's partial index, including the half a naive port gets wrong** (the same work
  queueable again once the first is done); row limiting and its ordering; generated keys increasing;
  a run round-tripping with all seven timing columns; an untraced run keeping them null; logs written
  and read back including a 5,000-character message; a replayed log line stored once while two live
  ones stay two; pruning through its window function; and a user, credential and lookup.
- The existing state suite (100) unchanged and green on SQLite — the port's real check.
- Every replication-side test green with unchanged counts after the relocation, which is the check
  that moving the dialects changed nothing for the drivers.
- Full suite: 839 unit, 192 integration (153 + the 39 new).

## Open questions

- ~~**`ApiOptions` shape for engine selection.**~~ Two fields; see above.
- ~~**Whether any store needs an engine-specific rewrite beyond quoting and parameters.**~~ Five did;
  see the findings.
- ~~**Whether MSSQL/Postgres need a retry pattern.**~~ They do not. The hook exists on `StateDialect`
  and returns false for both, so if a real deadlock case ever turns up there is a place to put it.
- **Cross-engine migration is still not built**, per the plan — starting on an engine is supported,
  moving between them is not, and pointing an existing deployment at a new engine silently starts an
  empty store. The README says so; the product does not stop anyone.
- **The API and TaskRunner are only exercised against SQLite end to end.** The cross-engine tests
  drive the stores directly. A full Playwright or integration run against a SQL Server state store
  would exercise the journal, the supervisor and the SPA on top of it, and would probably find
  something — the `StateHost`/`RemoteRunnerState` path in particular has never seen another engine.
- **No connection-string validation at startup.** A wrong one fails on first use with whatever the
  provider says, rather than at boot with something about configuration.
- **`SqliteSqlDialect` implements only half of `SqlDialect`**, throwing from the two column-type
  methods, because SQLite is a state engine here and never a replication source or target. That is
  honest but it does mean the class cannot be handed to anything expecting a full dialect — worth
  knowing if SQLite ever becomes a replication target.
