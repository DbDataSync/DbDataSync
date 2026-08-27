# Additional database drivers — Oracle, Postgres, MySQL, ODBC, JDBC

Add source/target support for Oracle, PostgreSQL, MySQL, ODBC and JDBC. The first four come from
their vendors' own ADO.NET providers; JDBC comes from the **`ClrKernel.Database.Provider.Jdbc`**
package.

**Initial scope for every one of them is batch or watermark mode only** — no change-data-capture
reader. Expands `implementation-plan.md`'s backlog line "Additional source/target database engine
drivers (Postgres, MySQL, Oracle, etc.)" into something concrete enough to plan against.

## What the abstraction already gives us for free

Worth stating first, because it bounds the work: the engine-neutral layer built over phases 3–14 was
built for exactly this, and most of it needs no change.

- `IDriver` is a small surface — `DriverType`, three Kind lists, `CreateConnection`, and three
  metadata queries. A driver is a self-contained project; nothing outside it needs to know it exists.
- **Capability discovery is already live and engine-neutral.** `GET /api/connections/{name}/capabilities`
  reports each driver's readers/staging/writers plus `supportsSegmentation`/`supportsReconciliation`,
  and since phase 10 the SPA's every Kind picker is driven by it. A new driver appears in the UI
  without a line of SPA change.
- **Segments are engine-neutral** (`BatchReloadSegment`, phase 9). Only the *rendering* of a segment
  into a predicate is per-engine.
- `ChangeRow`/`ChangeSchema` (phase 14), `ReadResult`, `IStagingProvider`, `IChangeWriter`, the work
  queue, the run model, `SecretStore` and the config store are all already engine-agnostic.

So the SPA needs essentially nothing, and the API needs one enum extended.

## What each driver actually has to supply

Per engine, mirroring `DataSync.Drivers.MsSql`:

| piece | MSSQL today | what a new driver needs |
| --- | --- | --- |
| identifier quoting | `SqlIdentifier` → `[x]` | `"x"` (Oracle, Postgres), `` `x` `` (MySQL), engine-dependent for ODBC/JDBC |
| parameter placeholders | `@p` | `:p` (Oracle), `@p`/`$n` (Npgsql), `@p` (MySQL), positional `?` (ODBC, JDBC) |
| watermark reader | `MsSqlWatermarkReader` | same shape — `SELECT … WHERE col > @prev ORDER BY col` |
| batch reload reader | `MsSqlBatchReloadReader` + `MsSqlSegmentScope` + `MsSqlSegmentExpansion` | same shape; per-engine predicate rendering and typed parameter binding |
| staging | `MsSqlStagingTableProvider` (SqlBulkCopy → `#temp`) | Postgres `COPY`, Oracle array binding/direct path, MySQL multi-row INSERT or `LOAD DATA`, ODBC/JDBC batched parameterised INSERT |
| writer | `MsSqlMerge`, `MsSqlMergeReconcile`, `MsSqlDeleteInsert` | see below |
| catalog metadata | `MsSqlSchemaQueries` (`sys.*`) | `information_schema` (Postgres/MySQL), `ALL_TAB_COLUMNS` (Oracle), `DatabaseMetaData` (JDBC), driver-dependent (ODBC) |
| identity handling | `MsSqlIdentityInsert` (`SET IDENTITY_INSERT`) | Postgres `OVERRIDING SYSTEM VALUE`, Oracle identity vs sequences, MySQL `AUTO_INCREMENT` |

**`MsSqlDeleteInsert` is the template for the portable writer.** It is plain
`DELETE … WHERE scope` + `INSERT … SELECT` in one transaction, needs no primary key, and joins
nothing row-by-row — it expresses identically on every engine listed. The MERGE-based writers do not:
Oracle has `MERGE`, Postgres has `MERGE` only from 15 (and `INSERT … ON CONFLICT` before it), MySQL
has `INSERT … ON DUPLICATE KEY UPDATE`, and ODBC/JDBC have whatever the underlying engine has.

## The consequence of watermark-only that needs to be said out loud

`MsSqlWatermarkReader`'s own documentation already says it: a watermark reader **cannot detect
deletes**. A row removed at the source is simply never seen again; it is not reported as a change.

So for all five engines, initial support means **the target accumulates rows the source has deleted**,
indefinitely, until something reconciles it. The only thing that reconciles it is a batch reload with
a *reconciling* writer (phase 9). That inverts the emphasis these engines get relative to MSSQL:
where Change Tracking makes reload an occasional repair, watermark mode makes a scheduled reconciling
reload part of normal operation.

Worth deciding deliberately rather than discovering: whether a watermark-mode replication should be
*able* to be configured without a periodic reconciling reload at all.

## Cross-cutting questions to settle before the first driver

- **Are Kinds engine-scoped or shared?** Note the existing oddity: every MSSQL Kind is prefixed
  (`MsSqlMerge`, `MsSqlBatchReload`) except the watermark reader, whose Kind is the bare string
  `"Watermark"` — engine-neutral in name, T-SQL in implementation. With six engines this has to be
  decided: does each advertise `"Watermark"` (so a table mapping is portable across engines) or
  `"PgWatermark"`/`"OraWatermark"` (so a Kind names one implementation)? The capability endpoint works
  either way; config portability does not.
- **Where does SQL-dialect knowledge live?** Six copies of quoting, placeholder and predicate
  rendering is the obvious smell, but a premature shared dialect abstraction is the other failure
  mode. Suggest: build Postgres second, entirely standalone, and extract only what is *demonstrably*
  identical — the same discipline `MsSqlTargetShape` came from.
- **`ConnectionConfig` does not fit ODBC or JDBC.** It models Host/Port/Database. ODBC wants a DSN or
  a full connection string; JDBC wants a URL. `Properties` (which gained a UI in phase 15) may be
  enough, or the shape may need a genuine alternative.
- **`AuthMode` is `SqlAuth | IntegratedAuth`.** Oracle wallets, Postgres certificate/SSL modes, MySQL
  auth plugins and JDBC's URL-embedded credentials do not fit. Needs extending, or the per-engine
  detail pushed into `Properties` + `SecretStore`.
- **JDBC brings a JVM.** `ClrKernel.Database.Provider.Jdbc` presumably bridges ADO.NET to a JDBC
  driver, which means a Java runtime and a jar on the deployment host, and a second failure surface
  in the connection path. That is a deployment decision, not just a dependency.
- **Metadata browsing is per-engine and user-visible.** The SPA's cascading connection → database →
  table pickers assume `ListDatabases` means something. Oracle's "database" is a service/schema;
  Postgres separates database from schema; MySQL conflates schema and database. The picker may need
  to be told what an engine's levels are called.

## This is the trigger the columnar item is waiting on

`planning/todo/columnar-change-batches.md` records that column-oriented batches buy nothing through
`SqlBulkCopy` — which re-boxes every cell — but allocate **nothing at all** against a sink that takes
typed values, and names "any non-MSSQL driver (a Postgres binary COPY takes typed values)" as the
trigger to revisit. **Postgres `COPY` is that sink.** Whoever builds the Postgres staging provider
should read that item first: the decision about how it consumes rows is the decision that item is
waiting for, and its unmeasured question — whether a reader can supply typed values without boxing
them on the way out of the source — is answerable there for the first time.

## Likely shape of the work

Probably splits into a cross-cutting phase and one phase per engine, but that is a guess until the
first driver is built. Suggested order, and the reasoning:

1. **Postgres first.** Best-documented ADO.NET provider (Npgsql), a genuinely different staging path
   (`COPY`), and the columnar question rides on it.
2. **MySQL or Oracle second** — whichever has a real user waiting. Oracle exercises the most
   divergence (quoting, placeholders, identity, catalog, wallets), so it is the better test of any
   abstraction extracted after Postgres.
3. **ODBC and JDBC last.** Both are *meta*-drivers: they reach an arbitrary engine, so neither can
   assume a dialect, and both will want the batched-parameterised-INSERT staging path and the
   delete+insert writer rather than anything engine-specific. Building them after two real engines
   means the generic path already exists.
