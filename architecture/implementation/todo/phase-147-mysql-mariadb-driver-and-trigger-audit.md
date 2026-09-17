# Phase 147 — MySQL/MariaDB driver, with trigger-audit change tracking

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/additional-database-drivers.md` (the general driver
shape), `architecture/planning/todo/change-tracking-mysql-and-mariadb-triggers.md` (the trigger-audit
design this phase implements almost verbatim), `architecture/planning/todo/change-tracking-mysql.md`
(the binlog alternative this phase deliberately does not build), `architecture/implementation/done/phase-020-postgres-driver.md`
(the structural template — read that first; this doc assumes it), `architecture/implementation/done/phase-033-trigger-audit-change-tracking.md`
(the generic mechanism this phase's only new reader-adjacent code plugs into), `architecture/implementation/done/phase-031-connection-addressing.md`
(connection-string/EZConnect-shaped addressing, already built and reused unmodified).

## Why bundle trigger-audit into the driver phase, against `additional-database-drivers.md`'s own scoping

That doc's own rule was "initial scope for every driver is batch or watermark mode only — no
change-data-capture reader," deliberately excluding CDC from a first driver phase. This phase overrides
that rule for MySQL/MariaDB specifically, and the reason is concrete, not a general relaxation: phase 33
already built the *entire* generic half of trigger-audit change tracking (`TriggerAuditReader`,
`IPositionAcknowledging`, `TriggerAuditPlan`) and proved it against two engines. What MySQL/MariaDB need
is the one per-engine DDL class phase 33 deliberately left for them — genuinely small, not a new design
exercise. Deferring it to a second phase would mean standing up the driver project twice for no real
risk reduction.

## What this phase will build

### `MySqlDialect` (`src/DbDataSync.Core/Sql/MySqlDialect.cs`)

`sealed class MySqlDialect : SqlDialect`, singleton `Instance`, mirroring `PostgresDialect`'s shape:

- `QuoteIdentifier` → `` `identifier` `` (backtick, doubled-backtick escaping)
- `ParameterReference` → `@name` (MySqlConnector supports named parameters)
- `OperationMarkerColumnType` → `"char(1)"`, `ChangedAtColumnType` → `"timestamp(6)"`
- `UseDatabaseAsync` — **a genuine, worth-stating difference from both Postgres and Oracle**: MySQL can
  change database on a live connection (`ChangeDatabase`/`USE`), unlike either. Whether to actually
  switch, or to validate-and-refuse for consistency with the other two engines' posture, is an open
  question below rather than assumed.
- `RenderStagingOrdinalColumn` → `AUTO_INCREMENT`, the same role `BIGSERIAL`/`GENERATED ALWAYS AS IDENTITY`
  play for Postgres.
- `WriteWithGeneratedColumnOverrideAsync`/`RenderInsertInto` — **MySQL needs neither**, per `SqlDialect`'s
  own doc comment ("MySQL needs nothing") — simpler than both Postgres and Oracle here.
- `RenderTieSafeRowLimit` — **the one real open risk in this dialect.** The ANSI default
  (`FETCH FIRST n ROWS WITH TIES`) has no MySQL equivalent before 8.0.13's window-function-based
  workaround, and MariaDB's own support lags further. Needs a real decision — see open questions.
- `ToCanonicalType`/`RenderColumnType` — the full native-type map: `TINYINT`/`SMALLINT`/`MEDIUMINT`/`INT`/`BIGINT`,
  `DECIMAL`, `FLOAT`/`DOUBLE`, `VARCHAR`/`CHAR`/`TEXT` family, `DATETIME`/`TIMESTAMP` (both with `(6)`
  fractional precision), `JSON`, `BLOB` family. `ENUM`/`SET` fall through to `Unmappable`, matching phase
  20's own answer to type mapping: no coercion layer, a mismatch fails loudly at apply time against the
  target's real catalog, never silently guessed.

### `MySqlCatalog` (`src/DbDataSync.Drivers.MySql/MySqlCatalog.cs`)

`internal sealed class : ITableCatalog`, wrapping the shared `InformationSchemaQueries` — MySQL has a
real `information_schema`, unlike Oracle, so this follows `PostgresCatalog`'s exact shape rather than
Oracle's from-scratch one. Overrides `IsIdentity` via `information_schema.columns.EXTRA = 'auto_increment'`
(the generic layer deliberately leaves this false — see `InformationSchemaQueries`'s own doc comment).

### `MySqlValueBinding` (`src/DbDataSync.Drivers.MySql/MySqlValueBinding.cs`)

`internal sealed class : ISegmentValueBinder`, typing a segment/watermark bound parameter as the
column's real `MySqlDbType`. **Not optional** — phase 20's own Finding 1 is the record of exactly this
bug (an untyped text bound broke on Postgres despite silently working on SQL Server via implicit
conversion). Built from the start here, not discovered later.

### `MySqlDriver` (`src/DbDataSync.Drivers.MySql/MySqlDriver.cs`)

`sealed class : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider, IProvisioner, ITableRowEstimator`,
the same five-interface shape `PostgresDriver` implements:

- `DriverType => "MySql"` (new `DriverIds.MySql` constant, `src/DbDataSync.Core/Config/ConnectionConfig.cs`)
- `DefaultPort => 3306`
- `RequiredLibraryId => "mysql-connector"` — already a real `KnownLibraries` entry
  (`MySqlConnector.MySqlConnectorFactory, MySqlConnector`, pinned 2.4.0); nothing new to add there.
- `Readers => [WatermarkReader, TriggerAuditReader, BatchReloadReader, KeyReconcileReader]` — all
  generic, reused unmodified, the same list Postgres registers.
- `StagingProviders => [BatchInsertStagingProvider]` — the generic one is enough; MySQL's own
  `LOAD DATA`/multi-row-insert bulk paths are a later optimization, not blocking (Postgres shipped with
  no engine-specific staging provider either — "an engine-specific `COPY` provider is a later phase" is
  the exact precedent).
- `Writers => [DeleteInsertWriter, KeyReconcileDeleteWriter, KeyReconcileScd2CloseWriter, SnapshotWriter, Scd2Writer]` —
  the same generic set every non-MsSql driver registers today. **MySQL as a target still inherits the
  "no generic upsert writer" gap phase 20 found** — a reconciling incremental writer only exists as
  `DeleteInsert` (full-segment reload), never a native `INSERT ... ON DUPLICATE KEY UPDATE`-based merge.
  Not fixed here; named so it isn't rediscovered as new.
- `CreateConnection` — `MySqlConnectionStringBuilder`, following `PostgresDriver.CreateConnection`'s
  exact shape: base from `ConnectionString` or `Host`, `Database`/`Port`/timeout applied on top,
  `AuthMode` switch (`None` → nothing added; `IntegratedAuth` → MySQL has no true integrated-auth
  concept, so this likely maps to a plugin-based auth left to `Properties` — see open questions;
  `SqlAuth` → username + password), `Properties` merged last, `WithCommandTimeout`.
- `ListDatabasesAsync`/`ListTablesAsync`/`ListColumnsAsync` — delegate to `MySqlCatalog`.
- `EstimateRowCountAsync` — `information_schema.tables.table_rows` (an estimate, like Postgres's
  `pg_class.reltuples` — not `COUNT(*)`, and explicitly allowed to be stale/approximate per
  `ITableRowEstimator`'s own contract).
- `TestAsync` — round-trip `SELECT 1`, catch `DbException` only.
- `SupportedActions`/`PlanAsync` — delegate to `MySqlProvisioner`.

### `MySqlProvisioner` (`src/DbDataSync.Drivers.MySql/MySqlProvisioner.cs`)

`CreateTargetTable`/`AlterTargetTable` following the established shape. `EnableSourceChangeCapture`
keyed on `request.ReaderKind`: `Satisfied`/zero-steps for `Watermark`/`BatchReload` (no source
cooperation needed, matching Postgres exactly), and for `TriggerAudit`, delegates to
`MySqlTriggerAudit`'s generated DDL through `TriggerAuditPlan.Build` — the same wiring
`PostgresProvisioner.PlanEnableSourceChangeCaptureAsync` already does, described in full in
`change-tracking-mysql-and-mariadb-triggers.md`.

### `MySqlTriggerAudit` (`src/DbDataSync.Drivers.MySql/MySqlTriggerAudit.cs`)

Built exactly as designed in `change-tracking-mysql-and-mariadb-triggers.md` — one shadow table
(`AUTO_INCREMENT` seq, `CHAR(1)` op, key columns, `TIMESTAMP(6)`), **three** triggers (MySQL cannot
combine `INSERT OR UPDATE OR DELETE` in one definition, unlike Postgres and Oracle), inline bodies. That
doc's own compatibility analysis is reused directly, not re-derived: the DDL is identical for MySQL and
MariaDB.

**The version-gated multiple-triggers-per-event check**, left as an open question in that doc, is
resolved here as: build it. Before generating `CREATE TRIGGER`, check whether the table already has a
trigger on the same `(timing, event)` — if the source's version predates stacking support (MySQL
<5.7.2, MariaDB <10.2.1) and a conflicting trigger exists, the provisioning plan reports `Unsupported`
with a plain reason, the same posture `TriggerAuditPlan`'s own no-primary-key case already takes, rather
than surfacing MySQL's own raw DDL error.

### Registration (all three composition roots, per phase 20's own confirmed list)

- `src/DbDataSync.Api/DbDataSyncHost.cs` — `registry.RegisterWithScripting(new MySqlDriver(), scriptHost)`
- `src/DbDataSync.TaskRunner/Program.cs` — the identical call in the worker's own registry build
- `src/DbDataSync.Cli/BuiltInDrivers.cs` — `new MySqlDriver()` added to `BuiltInDrivers.All`

### Project and environment

- `src/DbDataSync.Drivers.MySql/DbDataSync.Drivers.MySql.csproj` — `PackageReference` to `MySqlConnector`
  2.4.0 with `ExcludeAssets="runtime"` (phase 109h's pattern — the managed assembly loads lazily through
  `LibraryRegistry` once installed, exactly like `MsSql`/`Postgres` today), `ProjectReference`s to
  `Core`/`Drivers.Abstractions`/`Drivers.Generic`, `InternalsVisibleTo` for the test project.
- A MySQL service (and, for the trigger-compatibility claim to mean anything, a MariaDB service too) in
  `docker-compose.yml`, plus a `tools/dev-harness` scenario.

## What this phase does not build

- **The binlog-based native alternative.** See `change-tracking-mysql.md` — deliberately deferred until
  real demand for its latency/no-DDL trade shows up.
- **A MySQL-native bulk staging provider** (`LOAD DATA`, multi-row `INSERT` tuning) — the generic one is
  enough for a first phase, matching Postgres's own precedent exactly.
- **A reconciling incremental writer for MySQL as a target** — inherits the same generic-writers-only
  gap every non-MsSql driver has today; not this phase's to fix.
- **Pre-5.7.2/pre-10.2.1 trigger-stacking support** — refused with a reason (see above), not worked
  around.

## Open questions

- **`RenderTieSafeRowLimit` for MySQL.** No `WITH TIES` before 8.0.13's window-function workaround, and
  MariaDB support lags further. Needs a concrete fallback (a windowed subquery reproducing tie-safety
  manually) or an explicit, stated version floor — not left to silently misbehave near a tie boundary.
- **`AuthMode.IntegratedAuth` for MySQL** — MySQL has no real "integrated security" concept the way SQL
  Server does; auth plugins (`caching_sha2_password`, LDAP, PAM) are the closer analogue, and probably
  belong in `Properties` rather than a driver-level branch. Worth a deliberate decision, not an assumed
  mapping.
- **`UseDatabaseAsync`: switch, or validate-and-refuse?** MySQL genuinely can change database on a live
  connection, unlike Postgres/Oracle — worth deciding whether to use that capability or stay consistent
  with the other two engines' posture for its own sake.
- **MariaDB's own `information_schema` compatibility for catalog reading specifically** — the trigger
  DDL compatibility was checked closely; general catalog-reading compatibility (column metadata shapes,
  any MariaDB-specific `information_schema` quirks) was not checked with the same rigor and shouldn't be
  assumed identical without verifying against a real MariaDB instance during the build.
- **Target MySQL/MariaDB versions for the docker-compose/dev-harness environment** — decides which of
  the version-gated behaviors above (trigger stacking, `WITH TIES`) are actually reachable in CI at all.

## How to verify when built

- `MySqlPipelineTests` — the same shape `PostgresPipelineTests` already established (full reload, delete
  reconciliation, range/list/auto segments, watermark, identity, change-database behavior per whatever
  the `UseDatabaseAsync` decision above lands on).
- `TriggerAuditReaderTests` extended to run against MySQL — same reader, same test body, the third engine
  after SQL Server and Postgres; this is the test that actually proves the "one implementation covers
  both MySQL and MariaDB" claim, so it should run against both engines, not just one.
- `CrossEngineReplicationTests` extended to include MySQL as a source and/or target.
- A statement-level test asserting the version-gated trigger-stacking check refuses cleanly rather than
  surfacing a raw MySQL DDL error.
- A real `tools/dev-harness` run against both MySQL and MariaDB, with concrete numbers reported in the
  retrospective, matching phase 20's own verification shape.
- Full suite green.
