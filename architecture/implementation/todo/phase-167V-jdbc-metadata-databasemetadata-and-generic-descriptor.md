# Phase 167V — JDBC metadata: `DatabaseMetaData` default, `driver.yaml` escape hatches, phase 166V dropped

**Status**: Building.
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

(filled in as this phase is built)
