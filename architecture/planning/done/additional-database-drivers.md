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

## Watermark mode and deletes — say it, don't police it

A watermark reader cannot detect deletes: a row removed at the source is simply never seen again. That
is a real property operators need to know, and the UI should say so plainly wherever watermark mode is
selected.

It is **not** a reason to require a reconciling reload. Append-only and append/update-only tables are
common and entirely well served by watermark mode with nothing else attached — event logs, ledgers,
audit trails, immutable fact tables. Making a periodic reload mandatory would tax every one of those
to protect against a misuse that the operator is better placed to judge than we are.

So: state the limitation at the point of choice, and leave the choice alone.

## Cross-cutting questions to settle before the first driver

**Settled — Kind naming.** Keep the engine prefix on anything engine-specific (`MsSqlMerge`,
`PgCopyStaging`). Anything genuinely generic keeps a bare, unprefixed Kind — which makes the existing
`"Watermark"` correct rather than an oversight, and means a table mapping using a generic Kind is
portable across engines unchanged.

**Settled — where dialect knowledge lives.** Generic implementations live in their own namespace and
take a **dialect handler** covering the small variations: identifier quoting, parameter placeholders,
and similar. It is deliberately not an attempt to abstract over major engine differences — those get
an engine-specific implementation with a prefixed Kind. A generic implementation plus a dialect is the
default; a bespoke one is what you write when the generic one genuinely cannot express the thing.

- **`ConnectionConfig` does not fit ODBC or JDBC.** It models Host/Port/Database. ODBC wants a DSN or
  a full connection string; JDBC wants a URL. `Properties` (which gained a UI in phase 15) may be
  enough, or the shape may need a genuine alternative.
- **`AuthMode` is `SqlAuth | IntegratedAuth`.** Oracle wallets, Postgres certificate/SSL modes, MySQL
  auth plugins and JDBC's URL-embedded credentials do not fit. Needs extending, or the per-engine
  detail pushed into `Properties` + `SecretStore`.
- **Metadata browsing is per-engine and user-visible.** The SPA's cascading connection → database →
  table pickers assume `ListDatabases` means something. Oracle's "database" is a service/schema;
  Postgres separates database from schema; MySQL conflates schema and database. The picker may need
  to be told what an engine's levels are called.

**Settled — JDBC needs no JVM on the host.** `ClrKernel.Database.Provider.Jdbc` runs the JDBC driver
through IKVM, a .NET-embedded translator, so there is no Java runtime to install and no separate
process. Already exercised enough to be relied on.

## This is the trigger the columnar item is waiting on

`planning/todo/columnar-change-batches.md` records that column-oriented batches buy nothing through
`SqlBulkCopy` — which re-boxes every cell — but allocate **nothing at all** against a sink that takes
typed values, and names "any non-MSSQL driver (a Postgres binary COPY takes typed values)" as the
trigger to revisit. **Postgres `COPY` is that sink.** Whoever builds the Postgres staging provider
should read that item first: the decision about how it consumes rows is the decision that item is
waiting for, and its unmeasured question — whether a reader can supply typed values without boxing
them on the way out of the source — is answerable there for the first time.

## Outcome — resolved 2026-08-26

Broken into phases. The generic layer comes first and is proven against MSSQL before any new engine
exists, so the first real driver is a thin thing on top of tested foundations rather than a rewrite of
everything at once.

| phase | what | doc |
| --- | --- | --- |
| 17 | `SqlDialect` + generic namespace; the watermark reader moves there and becomes dialect-driven | `implementation/todo/phase-017-sql-dialect-and-generic-watermark.md` |
| 18 | Generic batch-reload reader, staging provider, delete+insert writer, `information_schema` metadata | `implementation/todo/phase-018-generic-batch-pipeline.md` |
| 19 | Connection testing as an opt-in driver capability — placed here so a new driver arrives to an interface that already has it | `implementation/todo/phase-019-connection-testing.md` |
| 20 | **PostgreSQL driver** — the first cross-engine replication | `implementation/todo/phase-020-postgres-driver.md` |
| 21 | Postgres binary `COPY` staging — and with it, the answer `columnar-change-batches` is waiting for | not yet written |
| 22 | Connection model for DSN/URL engines, and `AuthMode` beyond SqlAuth/IntegratedAuth | resolved without its own phase — phase 31 (connection addressing) settled `AuthMode.None` for exactly this (Oracle wallets, Postgres `.pgpass`, MySQL auth plugins) well before any of 23–24 needed it. ODBC/JDBC's own DSN/URL shape is still genuinely open. |
| 23–24 | MySQL, then Oracle — Oracle exercises the most divergence, so it is the better test of what 17–18 extracted | **built as phases 147 and 148, not 23/24** — the intervening phase numbers went to other work first. Order was MySQL-then-Oracle in practice too, matching this row's own plan, not the reversed order `change-tracking-strategies.md`'s own "Suggested order" section had settled on. See `implementation/done/phase-147-mysql-mariadb-driver-and-trigger-audit.md`/`phase-148-oracle-driver-trigger-audit-and-flashback.md` — both name real findings (Oracle's did turn out to exercise the most divergence, exactly as predicted here: five genuine engine-behavior surprises against MySQL's one). |
| 25–26 | ODBC, then JDBC via `ClrKernel.Database.Provider.Jdbc`. Both are *meta*-drivers reaching an arbitrary engine, so neither can assume a dialect; both want the generic paths that 18 establishes | not yet written |

**The "metadata browsing is per-engine" question is also settled for MySQL and Oracle specifically**,
by phases 147/148: MySQL's database *is* its schema (no third level), so a browsed table's schema and
database are the same value by construction; Oracle's database (service name) and schema (`ALL_TAB_COLUMNS.OWNER`)
are genuinely separate, matching `TableRef`'s own three-level shape directly with no special-casing
needed. Still open for ODBC/JDBC, which have no fixed shape to assume at all.

Phase 16 (replication endpoints) sits ahead of all of this — it is a redesign follow-up with no driver
dependency, and it changes the config model, so it is better done before five drivers are reading it.

Phases 21 onward are deliberately not written yet — each should be designed once the phase before it
has landed and changed what we know.
