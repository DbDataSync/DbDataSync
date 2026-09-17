# Phase 148 — Oracle driver, with trigger-audit and Flashback Version Query change tracking

**Status**: Built and verified 2026-09-17. See the Retrospective below.
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

# Retrospective

Built as `DbDataSync.Drivers.Oracle` (`OracleDriver`, `OracleCatalog`, `OracleValueBinding`,
`OracleProvisioner`, `OracleTriggerAudit`, `OracleFlashbackReader`) plus `OracleDialect` in
`DbDataSync.Core/Sql`, registered in all three composition roots and the solution file. `DriverIds.Oracle`
added. Verified against a real `gvenzl/oracle-free:23-slim` instance throughout the build — every design
decision below that turned out to differ from the plan was caught by running real SQL against a real
server, not by re-reading documentation. This phase produced far more real findings than phase 147, in
proportion to how much further this plan's own assumptions (ANSI `OVERRIDING SYSTEM VALUE`, sequence
semantics inside `INSERT ALL`, bind-variable naming) turned out to be wrong.

## Finding 1: `OVERRIDING SYSTEM VALUE` does not parse on Oracle at all

The plan assumed Postgres's own mechanism (`RenderInsertInto`'s `OVERRIDING SYSTEM VALUE` clause,
ANSI SQL:2003, and what Oracle's own SQL Language Reference documents for identity columns) would carry
over. It does not: confirmed via `sqlplus` directly (ruling out an ODP.NET client-side quirk) that
`INSERT ... VALUES`, `INSERT ... SELECT`, and a `MERGE`'s `INSERT` clause all reject the clause outright
with `ORA-00926`. A `GENERATED ALWAYS AS IDENTITY` column has no working override mechanism through
ordinary DML at all — the one DDL-based workaround (`ALTER TABLE ... MODIFY ... GENERATED BY DEFAULT`
around the write) is unsafe because Oracle DDL commits implicitly, silently ending whatever transaction
the caller is in. What does work, confirmed directly: a column declared `GENERATED BY DEFAULT ON NULL AS
IDENTITY` accepts an explicit value through a completely plain `INSERT`, no override machinery needed at
all. `OracleDialect.WriteWithGeneratedColumnOverrideAsync` stays a pass-through, with this stated as a
real, unresolved limitation for a target whose identity column is `GENERATED ALWAYS` rather than
`BY DEFAULT ON NULL` — a provisioning/documentation recommendation, not something fixed in code.

## Finding 2: `INSERT ALL` cannot generate a unique value per branch — a sequence is evaluated once per statement, full stop

The most consequential finding. `RenderMultiRowInsert`'s first implementation used `INSERT ALL` (the
form the phase doc specified), which reliably produced a unique-constraint violation on the staging
table's own ordinal column the moment a batch carried more than one row. Confirmed, systematically, that
this is not an identity-column quirk specifically: `GENERATED ALWAYS AS IDENTITY`, a plain column
`DEFAULT sequence.NEXTVAL`, and an *explicit* `sequence.NEXTVAL` written into every `INTO` branch all
produced the identical duplicate value across branches. Oracle evaluates a sequence at most once per SQL
statement, however many times it is referenced within it — a genuine, documented-if-obscure Oracle
semantic, not a bug in any of the three mechanisms tried. Fixed by abandoning `INSERT ALL` for this hook
entirely: `INSERT INTO t (cols) SELECT * FROM (SELECT … FROM dual UNION ALL SELECT … FROM dual …)` is an
ordinary `INSERT ... SELECT` whose source happens to be one literal row per branch, and identity
generation fires once per row exactly as it does for any other multi-row `INSERT ... SELECT` — confirmed
generating distinct sequential values for every row in one statement.

## Finding 3: bind-variable naming has two Oracle-specific rules neither other engine has

- **No colon in `OracleParameter.ParameterName`.** `:schema` in SQL text is correct, but the same name
  passed to the parameter object itself (`ParameterName = ":schema"`) raises `ORA-01745`. SQL Server and
  Postgres/MySQL's providers tolerate the sigil either way; Oracle's does not — exactly the case
  `SqlDialect.ParameterName`'s own doc comment already anticipated ("some providers want the bare name").
  `OracleDialect.ParameterName` now strips it.
- **Reserved words are rejected as bind-variable names outright, even though they are fine as quoted
  identifiers.** `table` and `trigger` both raise `ORA-01745`; `schema` and `name` do not. Renamed to
  `tableName`/`triggerName` throughout `OracleCatalog`/`OracleProvisioner`/`OracleDriver`. Not documented
  anywhere obvious — found because a query that worked in every other position suddenly didn't.

## Finding 4: two more Oracle SQL-grammar rejections, both fixed in shared `DbDataSync.Drivers.Generic` code

Both genuinely portable fixes — dropping something that bought nothing on any other engine, not an
Oracle-only special case:

- **`AS` before a table or subquery alias is invalid** (`ORA-03048`) — Oracle allows `AS` for column
  aliases but rejects it for table/derived-table aliases. `TriggerAuditStatement.BuildRead` (phase 33) and
  `HistorizedStatement`'s own several table aliases (`AS s`, `AS t`, `AS dup`) all carried it. Fixed by
  removing `AS` before every table alias in both files — valid, unchanged behavior on Postgres, SQL
  Server and MySQL, since `AS` there was always optional.
- **A bind-variable name cannot start with an underscore** (`ORA-00911`) — `BatchInsertStagingProvider`'s
  own `__s{row}_{value}` and `SegmentScope`'s `__seg{i}`/`__segMin`/`__segMax` both violated this. Renamed
  to `s{row}_{value}`/`seg{i}`/`segMin`/`segMax` — again valid and unchanged on every other engine.

Both fixes needed corresponding updates to hardcoded-literal test assertions across
`DbDataSync.Drivers.Generic.Tests`, `DbDataSync.Drivers.MsSql.Tests` and this phase's own suite — the full
solution test run (2000+ tests, every project) confirms nothing else depended on the old spellings.

## Finding 5: Flashback Version Query cannot see history for a table created too recently — regardless of the SCN window requested

The one finding this phase does **not** consider resolved. `VERSIONS BETWEEN SCN` against a table whose
`CREATE TABLE` happened moments earlier reliably raises `ORA-01466` ("table definition has changed"),
*even when the requested SCN window starts strictly after the table's creation*. Individually ruled out
as the cause: bind vs. literal SCN bounds, a delay of up to 90 seconds, a same-table DML "warm-up" before
capturing the window, a fresh connection for the query, the presence of a primary key/index, and explicit
`COMMIT`s. The only thing that reliably avoided it was a table that had simply existed since before the
test process started. This is a genuine Oracle engine characteristic this phase could not fully diagnose
within scope — plausibly related to how flashback/undo machinery tracks per-object incarnation
boundaries, not confirmed. **Practically irrelevant for a real deployment** (a real source table has
existed for days or years before Flashback is ever pointed at it), but real enough that
`OracleFlashbackReaderTests` needed `docker/oracle-init/20-flashback-probe-table.sql` — a table
provisioned once at container start, specifically so it has real age by the time tests run against it,
rather than one created fresh per test. Worth a line in the eventual phase 149 docs update: an operator
enabling Flashback on a table they just created should expect this for some (unquantified) initial
period.

## Verified rather than assumed, resolving open questions from the plan

- **`UseDatabaseAsync` cannot validate anything, and that's correct, not a gap.** `OracleConnection.Database`
  is confirmed (by reflection and by a live connection) to always be an empty string; the real value
  (`ServiceName`) is Oracle-specific and reaching it would mean `DbDataSync.Core` taking a package
  dependency no dialect there has today. The override is a genuine no-op — not a corner cut, the honest
  answer given what's reachable from that layer.
- **`OracleConnectionStringBuilder` has no `Host`/`Port`/`Database` properties at all** — confirmed by
  reflection — only `DataSource`. Host-mode addressing (`CreateConnection`) builds an EZConnect string,
  `host:port/database`, resolving the plan's own open question about service-name-vs-SID: `Database`
  supplies the service name.
- **`Oracle.ManagedDataAccess.Core` has no Windows Integrated Security support at all** — confirmed by
  reflection (no such property anywhere on the connection string builder). `AuthMode.IntegratedAuth`
  throws a clear, explicit error for Oracle rather than silently doing nothing.
- **`FLASHBACK` privilege is real, but narrower than the plan assumed.** Only needed when the connecting
  user does not own the table being read — an owner already has full rights on its own objects, confirmed
  by testing. `EXECUTE ON DBMS_FLASHBACK` is the one grant every deployment needs regardless of ownership,
  and it is not granted by default on any tested instance (`ORA-00904` without it).
- **`SELECT CURRENT_SCN FROM V$DATABASE`** needs a dictionary-view-class grant an ordinary app user lacks
  (`ORA-00942` — Oracle's own way of hiding an object rather than naming the privilege problem).
  **`TIMESTAMP_TO_SCN(SYSTIMESTAMP)`** needs no grant at all but was rejected as the position-capture
  mechanism after testing showed its SCN-to-timestamp mapping is too coarse for the purpose — two captures
  milliseconds apart returned the *identical* SCN. `DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER()` is the
  mechanism that shipped: needs its own grant, but is precise and monotonic once granted.
- **`ALL_TAB_COLUMNS.DATA_TYPE` is inconsistent across type families** — `TIMESTAMP` already embeds its
  own precision (`"TIMESTAMP(6)"`), unlike every bare-name type (`NUMBER`, `VARCHAR2`, `CLOB`); `CHAR_LENGTH`
  (not `DATA_LENGTH`, which is meaningless for a LOB) is the right figure for a character column.
  `ALL_USERS.ORACLE_MAINTAINED` (12c+, already within this driver's own floor) cleanly separates an
  operator's own tables from Oracle's SYS/SYSTEM/CTXSYS/... schemas.
- **The pre-12c identity-column question is resolved as planned**: floored at 12c+, `GENERATED ALWAYS AS
  IDENTITY` used unconditionally for the trigger-audit shadow table's own sequence column (unaffected by
  Finding 1/2, since nothing ever writes an explicit value into it).

## What the plumbing actually cost

Genuinely more than phase 147, as the plan predicted — but for a different reason than expected. The
*design* cost matched the plan (`OracleCatalog` built from scratch, ~180 lines; `OracleFlashbackReader` a
real new reader, ~210 lines). The *unplanned* cost was the five findings above, each requiring a live
server to discover and each needing a genuinely different fix than the ANSI-standard or Postgres-precedent
assumption the plan carried in. Two of those fixes (Findings 3 and 4) turned out to live in shared
`DbDataSync.Drivers.Generic` code, not in the Oracle driver project at all.

## Still not built

- LogMiner — deliberately deferred, unchanged from the plan.
- Pre-12c Oracle support — floor set at 12c+, as planned.
- A `CHAR(1)`/`'Y'`-`'N'` boolean convention — `NUMBER(1)`/`1`-`0` is the one built.
- Oracle as a target with a reconciling incremental writer — same generic-writers-only limitation as
  every other non-MsSql driver.
- A real fix for Finding 5 (Flashback vs. a freshly-created table) — worked around at the test level,
  not understood well enough to fix or even fully explain.
- `tools/dev-harness` scenario — not added this pass.
- `CrossEngineReplicationTests` extension to include Oracle — not done this pass; only Oracle↔Oracle is
  verified.

## Verification

- `OracleDialectCanonicalTypeTests`, `OracleTriggerAuditStatementTests`, `OracleProvisionerTests` — 37
  unit tests, no live server, all green.
- `OraclePipelineTests` (full reload, delete reconciliation, segment modes, watermark, the
  `GENERATED ALWAYS` vs. `BY DEFAULT ON NULL` identity finding, `UseDatabaseAsync`'s no-op confirmed),
  `TriggerAuditReaderTests` (the fourth engine now proven, after SQL Server, Postgres, MySQL/MariaDB), and
  `OracleFlashbackReaderTests` (insert/update/delete detection, the full-row-on-delete divergence from
  `TriggerAuditReader`, `IPositionCapturing`, `ChangesFromLatest`) — 21 integration tests against a real
  `gvenzl/oracle-free:23-slim` container, all green.
- Full solution build: 0 warnings, 0 errors. Full test run across every project in the solution — driver
  suites and every consumer (Api, Cli, TaskRunner, State, Scripting, Verification, Core, Certificates,
  Libraries, Descriptor, DuckDb) — 0 failures, confirming the fifth driver's registration and the two
  shared-code fixes (Findings 3, 4) broke nothing already relying on the old parameter-naming or `AS`
  spellings.
- `ORA-01555`/`ORA-30052` → `PositionExpiredException` mapping: not empirically triggered — reproducing
  genuine undo-retention exhaustion needs infrastructure-level conditions (filling real undo over real
  time) outside this phase's practical reach, the same class of gap phase 147 named for its own
  version-gated trigger-stacking check before deciding to build it anyway. The mapping code itself is
  reviewed, not proven against a real `ORA-01555`.
