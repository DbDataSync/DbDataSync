# Phase 167V — JDBC metadata: `DatabaseMetaData` default, `driver.yaml` escape hatches, phase 166V dropped

**Status**: Built — items 1, 2 (partial — see Retrospective), 3, 5, 6. Item 4 (JDBC's own `typeMap`,
converting `JdbcDriver` to a `GenericDriverSpec`) deliberately deferred — see Retrospective.
**Plan reference**: `architecture/planning/todo/jdbc-metadata-catalog.md` (the full design, worked through
in planning conversation — this phase carries it out), `architecture/implementation/done/phase-165V-jdbc-reader-spike-ikvm-postgres.md`
(the driver this phase changes), `architecture/implementation/todo/phase-166V-pipeline-metadata-override-for-preview-and-segmentation.md`
(**superseded** — its goal is met here by a different, smaller mechanism; its own proposed
`ConditionalWeakTable` override is not built), `architecture/implementation/done/phase-029-scripted-metadata-providers.md`,
`architecture/implementation/done/phase-109d-the-yaml-descriptor.md`.

## What this phase builds

1. **`JdbcConnection` becomes public**, with two public members named for the Java type each returns
   (not for their role in this project): `JavaSqlDriver` (`java.sql.Driver`) and `JavaSqlConnection`
   (`java.sql.Connection`, replacing the `internal` `Underlying`). A `metadataProvider` script compiled
   against `DbDataSync.Drivers.Jdbc` can then `is`-check `MetadataContext.Connection` and reach the real
   JDBC objects directly — no DbDataSync-authored wrapper API.
2. **`JdbcCatalog` defaults to `java.sql.DatabaseMetaData`**, not `InformationSchemaQueries`. Replaces the
   Postgres-only SQL reuse from phase 165V with the standard JDBC metadata API
   (`getCatalogs`/`getSchemas`/`getTables`/`getColumns`/`getPrimaryKeys`), which works across engines
   without assuming `information_schema` exists.
3. **`GenericDriverSpec`/`driver.yaml` gets a third catalog strategy: `query`.** `GenericDriverSpec.Catalog`
   widens from the concrete `InformationSchemaQueries` to an abstraction with three implementations
   (`informationSchema`, `databaseMetaData`, `query`). `query` is backed by two new YAML properties,
   `metadataQueries.tableQuery`/`columnQuery`, deserialized into `QueryTableRow`/`QueryColumnRow` — full
   row contract (required fields, null defaults, missing-column handling) specified in the planning doc.
4. **JDBC's type-name parsing moves into `driver.yaml`'s `typeMap`**, per vendor, replacing the single
   hardcoded, Postgres-scoped `JdbcDialect.ToCanonicalType`. A JDBC-backed engine becomes a
   `GenericDriverSpec` descriptor (one per vendor, e.g. a `postgres-via-jdbc.driver.yaml`), not a
   hand-written compiled `IDriver` — phase 165V's `JdbcDriver : IDriver` was a spike's shape.
5. **Phase 166V's mechanism is dropped.** Its goal — Preview honoring a bound `metadataProvider` script —
   is met by `PreviewService` calling `ScriptedMetadata` itself (it already has the connection, driver,
   and dialect in scope) and passing the result down `PreviewRequest` as a plain field, the same
   "resolve where the context is, hand it down as data" shape `ReadChangesAsync`'s own `sourceColumns`
   parameter already uses. No `ConditionalWeakTable`, no per-driver decorator.
6. **`ExpandAutoSegmentsAsync`'s column-type lookup threads through `mapping.SourceColumns`** (the cache
   `RunExecutor` already has in scope at its one call site) instead of asking the catalog live — it's on
   the real run path, not just Preview, so it never gets Preview's "show live truth" justification.
   `GetRangeAsync`'s `SELECT MIN/MAX` stays live — it's a data query, unrelated to any of this.

## What this phase does not build

- A real, second JDBC-backed vendor descriptor (e.g. an Oracle-via-JDBC `driver.yaml`) — this phase makes
  the mechanism possible; standing up a second engine is separate work.
- Anything about `TYPE_NAME` vs. a real read's `ResultSetMetaData` agreement — documentation note only,
  not resolved in code.
- ODBC's equivalent of any of this.

## How to verify when built

- `DbDataSync.Drivers.Jdbc.Tests`' existing parity tests (`BatchReloadReaderAsync`, `WatermarkReaderAsync`)
  still pass unchanged — none of this touches the read path.
- A new test exercising `JdbcCatalog` against the live Postgres container, asserting its answer now comes
  from `DatabaseMetaData` rather than `information_schema` (observable via a column type spelling
  `DatabaseMetaData` reports differently than `information_schema` would, or by asserting the SQL text
  `InformationSchemaQueries` would have run is never issued).
- A `GenericDriverSpec` loaded from a YAML descriptor with `catalog: query` and a `metadataQueries` block,
  asserting the row-mapping rules from the planning doc: required fields missing → a named failure at
  first use; optional fields missing or `NULL` → the documented default, per field.
- `PreviewService`'s existing tests still pass; a new one binds a `metadataProvider` script to a
  connection and asserts a preview reflects the script's answer, not the driver's native catalog.
- Full existing suite green.

---

# Retrospective

## What shipped

- **`JdbcConnection` public**, with `JavaSqlDriver`/`JavaSqlConnection` replacing the `internal Underlying`.
- **`JdbcCatalog` reads `java.sql.DatabaseMetaData`** instead of reusing `InformationSchemaQueries`.
  `InformationSchemaQueries.FormatType` widened to `public` so `JdbcCatalog` (and now `QueryCatalog`)
  reuse the same length/precision/scale assembly. Found by running the new tests against the live
  container, not assumed: pgJDBC's `DatabaseMetaData` correctly flags a `nextval(...)`-backed default
  (`serial`) as `IS_AUTOINCREMENT` — better than `InformationSchemaQueries`, which hardcodes
  `IsIdentity: false` unconditionally — and reports `TYPE_NAME` as the synthesized pseudo-type
  `"serial"` rather than the underlying `"int4"`, live evidence for why type-name parsing belongs in a
  per-engine `typeMap` (item 4, deferred) rather than one hardcoded dialect.
- **`ExpandAutoSegmentsAsync` reads the auto-segment column's type from `sourceColumns`** (the mapping's
  phase 91 cache) instead of a live `catalog.GetColumnsAsync` call — across every implementer
  (`BatchReloadReader`, `KeyReconcileReader`, `MsSqlBatchReloadReader`) and every caller
  (`RunExecutor`, `ReconcileService`, `BulkLoadService`). `GetRangeAsync`'s own `SELECT MIN/MAX` stays
  live — a data query, never part of this problem despite living in the same method.
- **Preview honors a bound `metadataProvider` script** on the source side: `PreviewRequest` gained
  `SourceColumns`/`TargetColumns`, resolved once by `PreviewService` via `ScriptedMetadata` (the same
  script-aware path browsing and mapping refresh already use) before any `DescribeAsync` runs.
  `BatchReloadReader`/`WatermarkReader`/`KeyReconcileReader`'s `DescribeAsync` read `request.SourceColumns`
  instead of calling their own `catalog` — which, once nothing called it anymore (this and the
  `ExpandAutoSegmentsAsync` fix together), became a dead constructor parameter, removed across every
  compiled driver and `GenericDriverSpec`'s construction (10 files), including two tests whose
  `ThrowingTableCatalog` double now pins a structural guarantee rather than a runtime-tested one. Proven
  end to end, not just argued from the code: a new test binds a script reporting a column's type as
  `varchar(50)` when the real column is `int`, and asserts an incremental preview's declared parameter
  reflects the script's answer.
- **`driver.yaml`'s `catalog: query` escape hatch**: `GenericDriverSpec.Catalog` widened from the
  concrete `InformationSchemaQueries` to a new `IDescriptorCatalog` (kept separate from `ITableCatalog`
  itself, since not every catalog — `MsSqlCatalog`, notably — can list tables); a new `QueryCatalog`
  class runs an operator's own `tableQuery`/`columnQuery`, matched to `QueryTableRow`/`QueryColumnRow` by
  column name. Caught before any test ran against it: the first draft substituted `{{schema}}`/`{{table}}`
  as a quoted *identifier*, which is wrong SQL for the `WHERE table_schema = {{schema}}` shape these
  queries actually need (a value comparison, not an identifier reference) — fixed to a quoted string
  literal, which also meant `QueryCatalog` needs no `SqlDialect` at all.

## What was deliberately not built

- **`databaseMetaData` as a third `driver.yaml` catalog strategy.** Would need
  `DbDataSync.Drivers.Descriptor` to reach `JdbcCatalog`, which is `internal` to `DbDataSync.Drivers.Jdbc`
  — a real cross-project wiring decision, not scoped here.
- **Converting the shipped JDBC driver from phase 165V's hand-written `JdbcDriver : IDriver` into a
  `GenericDriverSpec` descriptor with its own per-vendor `typeMap`.** This is item 4 of the original
  design — decided in planning, not built here. It's an architecture change to phase 165V's actual shape
  (one descriptor per JDBC vendor, e.g. `postgres-via-jdbc.driver.yaml`, rather than one `JdbcDriver` type
  serving every URL), not a small addition, and deserves its own dedicated phase.
- The target-side (writer) equivalent of the `DescribeAsync` fix — `TargetShape.LoadAsync`, used by every
  writer's own preview. Real, same shape as the source-side fix, but not JDBC-blocking: `JdbcDriver` has
  no writers yet. Left as an explicit follow-up rather than folded in here.

## Verification

Full solution builds clean throughout. Against live containers: MsSql (124 + 23 targeted), MySql (28 + 8),
Oracle (21 + 8), Jdbc (8, including 4 new `JdbcCatalogTests`), Postgres (18 touched — 14 unrelated
`PgLogicalSlotTests` failures confirmed pre-existing/environmental, not caused by this phase), Generic
(204, up from 200), DuckDb (33), Descriptor (32, up from 29), Api (13 `PreviewIntegrationTests` including
the new script-honoring test) all green.
