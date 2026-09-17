# Phase 148 — Oracle driver, with trigger-audit and Flashback Version Query change tracking

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/additional-database-drivers.md`, `architecture/planning/todo/change-tracking-oracle-triggers.md`
(the trigger-audit design this phase implements), `architecture/planning/todo/change-tracking-oracle.md`
(Option 1, Flashback Version Query, folded into this phase per the recommendation in its own "Open
questions"; Option 2, LogMiner, deliberately not built here), `architecture/implementation/done/phase-020-postgres-driver.md`
(structural template), `architecture/implementation/done/phase-033-trigger-audit-change-tracking.md`,
`architecture/implementation/done/phase-031-connection-addressing.md` (EZConnect/TNS/wallet addressing,
already built and reused unmodified), `architecture/implementation/done/phase-147-mysql-mariadb-driver-and-trigger-audit.md`
(the sibling phase — read that first; this one repeats less of the shared reasoning).

## Why Oracle now, and why Flashback ships in the same phase as the trigger option

Same reasoning as phase 147 for bundling trigger-audit into the driver phase: phase 33 already built the
generic half, Oracle needs only its own DDL class. Flashback is folded in too, on the recommendation
`change-tracking-oracle.md` itself already reached: unlike MySQL's binlog (deliberately deferred — a real
protocol client, worth building only against real demand), Flashback Version Query is *cheap* — a
`SELECT ... VERSIONS BETWEEN SCN`, no DDL, no shadow table, no pruning hook, no `IPositionAcknowledging`
at all. Building the driver's connection/dialect/catalog layer once and getting two change-tracking
options out of it, rather than one, is a better return on that shared cost than deferring Flashback to a
third phase. **LogMiner is not part of this phase** — its own doc calls it "real work, worth doing only
when Flashback provably isn't enough," and nothing here changes that.

## What this phase will build

### `OracleDialect` (`src/DbDataSync.Core/Sql/OracleDialect.cs`)

- `QuoteIdentifier` → `"identifier"` (double-quote, doubled-quote escaping)
- `ParameterReference` → `:name` — genuinely different punctuation from every other engine here
  (`@name` for MsSql/Postgres/MySQL), worth getting right in the statement builder from the start, not
  patched in once a test fails on it.
- `UseDatabaseAsync` — validate-and-ignore. This is the one hook `SqlDialect`'s own doc comments already
  anticipated by name ("engines where a connection cannot change database (Oracle, where the schema is
  the unit)") — no new design here, just filling in the case the abstraction was built for.
- `RenderStagingOrdinalColumn` — `GENERATED ALWAYS AS IDENTITY` (see version-floor decision below).
- `RenderMultiRowInsert` — **needs a real override**, unlike MySQL (which can reuse the ANSI multi-row
  `VALUES (...), (...)` form). Oracle has no such syntax; the multi-row shape is `INSERT ALL INTO t (...)
  VALUES (...) INTO t (...) VALUES (...) ... SELECT * FROM dual`.
- `TrueLiteral`/`FalseLiteral`/`RenderLiteral` for a boolean column — **a real decision, not a default.**
  Oracle has no native boolean type before 23c. This phase adopts `NUMBER(1)` with `1`/`0`, matching the
  convention Oracle's own `BOOLEAN` PL/SQL-to-SQL bridging tools use most often, over the `CHAR(1)`
  `'Y'`/`'N'` alternative — stated here as a deliberate choice so it isn't silently picked differently by
  whoever implements it.
- `ToCanonicalType`/`RenderColumnType` — the full type map: `NUMBER(p,s)`, `VARCHAR2`, `NVARCHAR2`, `CLOB`/`NCLOB`,
  `DATE` vs `TIMESTAMP(6)` (Oracle's `DATE` carries time-of-day but no sub-second precision — a real
  mapping decision against .NET's `DateTime`), `RAW`/`BLOB`. Same no-coercion-layer posture as every other
  driver: a mismatch fails loudly at apply time, never silently guessed.

### `OracleCatalog` (`src/DbDataSync.Drivers.Oracle/OracleCatalog.cs`)

**Built from scratch, not wrapping `InformationSchemaQueries`** — Oracle has no `information_schema`.
Confirmed directly rather than assumed: this is the one place this phase costs genuinely more than
phase 147's MySQL catalog. Queries `ALL_TAB_COLUMNS`/`USER_TAB_COLUMNS` for columns, `ALL_CONSTRAINTS`
joined to `ALL_CONS_COLUMNS` (`CONSTRAINT_TYPE = 'P'`) for primary keys, and `ALL_TAB_IDENTITY_COLS`
(12c+) for identity detection — the Oracle-specific equivalent of MySQL's `EXTRA = 'auto_increment'`
check.

### `OracleValueBinding` (`src/DbDataSync.Drivers.Oracle/OracleValueBinding.cs`)

`ISegmentValueBinder`, typing bound parameters via `OracleDbType`. Mandatory from day one, per phase 20's
Finding 1 — same as every other driver, not repeated in full here.

### `OracleDriver` (`src/DbDataSync.Drivers.Oracle/OracleDriver.cs`)

`sealed class : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider, IProvisioner, ITableRowEstimator`:

- `DriverType => "Oracle"` (new `DriverIds.Oracle` constant)
- `DefaultPort => 1521`
- `RequiredLibraryId => "oracle-managed-data-access"` — already a real `KnownLibraries` entry
  (`Oracle.ManagedDataAccess.Client.OracleClientFactory, Oracle.ManagedDataAccess.Core`, pinned 23.9.1);
  nothing new to add there.
- `Readers => [WatermarkReader, TriggerAuditReader, BatchReloadReader, KeyReconcileReader, OracleFlashbackReader]` —
  the first four generic and reused unmodified; the fifth is this phase's one genuinely new reader (see
  below), unlike phase 147 which adds none.
- `StagingProviders => [BatchInsertStagingProvider]` — generic, same posture as MySQL; Oracle's own
  array-binding/direct-path bulk paths are a later optimization.
- `Writers => [DeleteInsertWriter, KeyReconcileDeleteWriter, KeyReconcileScd2CloseWriter, SnapshotWriter, Scd2Writer]` —
  the same generic set, inheriting the same "no reconciling incremental writer for a non-MsSql target"
  gap phase 20 found and phase 147 names again. Not fixed here either.
- `CreateConnection` — `OracleConnectionStringBuilder`. `AuthMode.None` + `ConnectionString` is **already
  fully settled by phase 31** for wallets (EZConnect/TNS names, `TNS_ADMIN`-relative wallet config) —
  confirmed directly against `ConnectionConfig`/`ConfigValidation.ValidateAddressing`, no new `AuthMode`
  member needed, contrary to what an earlier draft of `change-tracking-oracle.md` once assumed was still
  open.
- `ListDatabasesAsync` — Oracle has no real multi-database-per-connection concept; returns the connected
  schema/service as the sole entry, matching `UseDatabaseAsync`'s validate-and-ignore posture. Whether
  `ConnectionConfig.Database` maps to Oracle's *service name* or a *SID* is a real, stated open question
  below rather than an assumption baked silently into the connection-string builder.
- `ListTablesAsync`/`ListColumnsAsync` — delegate to `OracleCatalog`.
- `EstimateRowCountAsync` — `ALL_TABLES.NUM_ROWS` (populated by `DBMS_STATS`, an estimate — the same
  contract every other `ITableRowEstimator` implementation already has, stale by design).
- `TestAsync` — `SELECT 1 FROM DUAL`.
- `SupportedActions`/`PlanAsync` — delegate to `OracleProvisioner`.

### `OracleFlashbackReader` (`src/DbDataSync.Drivers.Oracle/OracleFlashbackReader.cs`)

The one genuinely new reader in this phase — Oracle-specific, not generic, since Flashback Version Query
syntax belongs to Oracle alone (unlike trigger-audit's shared read side).

- `IChangeReader`, `DetectsDeletes => true` (a deleted row's final version carries its column values,
  confirmed in `change-tracking-oracle.md`'s own Option 1 write-up).
- **Implements `IPositionCapturing`.** Flashback can fix a window's end with `SELECT CURRENT_SCN FROM
  V$DATABASE` (or the smaller-grant `DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER`) without reading a single
  row — cheap enough to support the phase-134 initial-load-position-capture pattern the same way
  `WatermarkReader`/`TriggerAuditReader`/`MsSqlChangeTrackingReader` already do. Not an
  `ISegmentExpandingReader` — matches `change-tracking-strategies.md`'s own guidance that a CDC-shaped
  reader generally isn't one.
- The statement: `SELECT VERSIONS_OPERATION, VERSIONS_STARTSCN, VERSIONS_XID, <columns> FROM <table>
  VERSIONS BETWEEN SCN :fromScn AND :toScn WHERE VERSIONS_OPERATION IS NOT NULL ORDER BY
  VERSIONS_STARTSCN`, with `VERSIONS_OPERATION` (`I`/`U`/`D`) mapped directly to `ChangeOperation` — no
  inference needed, unlike readers that have to derive the operation from row shape.
- **Non-consuming by construction** — no `IPositionAcknowledging` implementation, unlike both trigger-audit
  options. Confirmed directly from the source doc: there is nothing here to prune.
- Maps `ORA-01555`/`ORA-30052` (undo retention exceeded / invalid lower-bound SCN) to the shared
  `PositionExpiredException`, the same "recovery is a reload, which already exists" posture every other
  position-expired case takes.

### `OracleProvisioner` (`src/DbDataSync.Drivers.Oracle/OracleProvisioner.cs`)

`CreateTargetTable`/`AlterTargetTable` following the established shape, using the boolean-literal and
type-mapping decisions above. `EnableSourceChangeCapture` applies the exact same `request.ReaderKind`
branch every other provisioner already uses — confirmed against `PostgresProvisioner`'s real code
(`if (request.ReaderKind != GenericDriverKinds.TriggerAudit) return Satisfied`), just with two non-trigger
cases instead of one:

- `Watermark`/`BatchReload` → `Satisfied`, zero steps, same as every other engine.
- `TriggerAudit` → delegates to `OracleTriggerAudit`'s DDL through `TriggerAuditPlan.Build`, exactly as
  designed in `change-tracking-oracle-triggers.md`, naming the `FLASHBACK`/`LOGMINING`-adjacent
  privileges it does *not* need (ordinary `CREATE TRIGGER` rights only) the same way
  `TriggerAuditPlan.Costs` already states costs for Postgres.
- `OracleFlashback` (a new, Oracle-only Kind name — not `GenericDriverKinds`, since nothing about this
  reader is shared) → `Satisfied`, zero DDL steps, but the plan's `Costs` names the real requirement:
  `FLASHBACK` privilege on the table (or `FLASHBACK ANY TABLE`) plus `SELECT`, and the relationship
  between poll interval and `UNDO_RETENTION` — stated at the point of choice, per
  `change-tracking-oracle.md`'s own stated intent, not left for an operator to discover as `ORA-01555`
  days later.

### `OracleTriggerAudit` (`src/DbDataSync.Drivers.Oracle/OracleTriggerAudit.cs`)

Built as designed in `change-tracking-oracle-triggers.md`: one shadow table, one trigger (Oracle can
combine `AFTER INSERT OR UPDATE OR DELETE` in a single definition, like Postgres and unlike MySQL),
inline PL/SQL body branching on `INSERTING`/`UPDATING`/`DELETING`, `:NEW`/`:OLD` row references.

**The pre-12c identity-column question, left open in that doc, is resolved here: floor the driver at
Oracle 12c+.** `Oracle.ManagedDataAccess.Core` 23.9.1 (the pinned `KnownLibraries` package) is the modern
.NET-Core-targeted ODP.NET client line, which in practice is exercised almost exclusively against 12c+
databases — a pre-12c sequence-based `seq` fallback would be real, untested-in-practice work for a
combination unlikely to exist. `GENERATED ALWAYS AS IDENTITY` is used unconditionally; a pre-12c source
is out of scope for this phase, named explicitly rather than silently unsupported.

### Registration (all three composition roots)

`DbDataSyncHost.cs`, `TaskRunner/Program.cs`, `Cli/BuiltInDrivers.cs` — the identical pattern phase 147
uses, `new OracleDriver()` added to each.

### Project and environment

- `src/DbDataSync.Drivers.Oracle/DbDataSync.Drivers.Oracle.csproj` — `PackageReference` to
  `Oracle.ManagedDataAccess.Core` 23.9.1, `ExcludeAssets="runtime"`, `ProjectReference`s to
  `Core`/`Drivers.Abstractions`/`Drivers.Generic`.
- An Oracle service in `docker-compose.yml`. **Worth naming as a real cost, not hand-waved**: Oracle's own
  official container images require accepting a license and are not on a registry CI can pull from
  without authentication. `gvenzl/oracle-free` (the realistic modern free, unauthenticated option) is the
  practical choice, named here so whoever builds this doesn't rediscover the licensing wall mid-phase.
- A `tools/dev-harness` scenario.

## What this phase does not build

- **LogMiner.** Stays in `change-tracking-oracle.md`, deferred until Flashback's lookback window
  genuinely proves insufficient somewhere real.
- **Pre-12c Oracle support** (sequence-based identity fallback) — floor set at 12c+, stated above.
- **A `CHAR(1)`/`'Y'`-`'N'` boolean convention** — `NUMBER(1)`/`1`-`0` is the one built; not both.
- **Oracle as a target with a reconciling incremental writer** — same generic-writers-only limitation as
  every other non-MsSql driver.
- **RAC-aware log enumeration, GoldenGate/XStream, `DBMS_CDC_PUBLISH`, audit-trail-based tracking** — all
  already ruled out in `change-tracking-oracle.md`'s own "Ruled out" section; this phase doesn't revisit
  them.

## Open questions

- **`ConnectionConfig.Database` → Oracle service name or SID?** Needs a real decision before
  `CreateConnection` is written, not an assumption baked in silently — ties to phase 31's own
  still-open "browsing with no `Database`" question for Oracle.
- **Flashback across a restart** — undo doesn't survive an instance restart the way archived redo does;
  worth confirming what happens to a stored SCN across one and treating it as position-expired if it
  invalidates, per `change-tracking-oracle.md`'s own open question.
- **`gvenzl/oracle-free` version pinning** for the docker-compose/dev-harness environment, and whether its
  behavior is representative enough of a licensed Enterprise/Standard Edition target to trust the
  verification numbers this phase produces.
- **`RenderMultiRowInsert`'s `INSERT ALL` shape under a large batch** — worth checking there's a sane
  statement-size ceiling (parameter count, statement length) before assuming it scales the same way the
  ANSI multi-row form does for the other engines.

## How to verify when built

- `OraclePipelineTests` — full reload, delete reconciliation, segment modes, watermark, identity, the
  boolean-literal convention round-tripping correctly.
- `TriggerAuditReaderTests` extended to Oracle — the third engine after SQL Server and Postgres, same
  reader, same test body.
- A new `OracleFlashbackReaderTests` — statement-shape tests plus a real integration test exercising
  insert/update/delete detection, position capture via `IPositionCapturing`, and the
  `ORA-01555`→`PositionExpiredException` mapping.
- `CrossEngineReplicationTests` extended to include Oracle.
- A real `tools/dev-harness` run against `gvenzl/oracle-free`, with concrete numbers reported in the
  retrospective.
- Full suite green.
