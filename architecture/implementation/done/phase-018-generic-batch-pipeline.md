# Phase 18 — Generic Batch Pipeline

**Status**: Built
**Plan reference**: `architecture/planning/done/additional-database-drivers.md`; builds directly on
phase 17's `SqlDialect` and `DataSync.Drivers.Generic`.

## Why this phase

Phase 17 establishes the pattern with one reader. This one completes the set, so that a new engine's
driver is a dialect, a connection factory and a catalog query — and nothing else — before it can run a
batch replication end to end. It is still proven against MSSQL; no new engine appears until phase 20.

## What this phase will build

Four generic implementations, all dialect-driven, all with bare unprefixed Kinds:

**`Generic.BatchReloadReader`** (Kind `"BatchReload"`) — a full or segment-scoped `SELECT`, composing
the segment predicate with the mapping's own `Filter`, yielding every row as an insert. Shape-identical
to `MsSqlBatchReloadReader`; differs only through the dialect. Implements `ISegmentExpandingReader`,
with the `MIN`/`MAX` round-trip and the bucket arithmetic reused from `MsSqlSegmentExpansion` — that
arithmetic is pure and already engine-neutral, so it moves rather than being reimplemented.

**`Generic.BatchInsertStagingProvider`** (Kind `"StagingTable"`) — stages into a real table via batched
parameterised multi-row `INSERT`. Deliberately the lowest common denominator: no temp-table syntax, no
bulk-load API, no provider-specific loader. Every engine in the plan can run it, including ODBC and
JDBC which have no bulk path at all. Engines with something faster get their own prefixed provider
later — Postgres `COPY` is phase 21.

Two things this has to get right that `MsSqlStagingTableProvider` gets for free from `SqlBulkCopy`:
- **Parameter count limits.** SQL Server caps a request at 2100 parameters; other engines have their
  own caps. Rows per statement must be derived from the column count against a dialect-supplied
  maximum, not fixed. (This is the same defect measured in the benchmark work — 500 rows × 5 columns
  overflowed — so it is a known trap rather than a hypothetical.)
- **Cleanup.** `IStagingProvider.CleanupAsync` exists (phase 12) and must drop the staging table; a
  real table does not disappear when the connection does, unlike `#temp`.

**`Generic.DeleteInsertWriter`** (Kind `"DeleteInsert"`) — `DELETE … WHERE scope` then
`INSERT … SELECT` from staging, in one transaction. **The portable writer**: no primary key, nothing
joined row by row, expressible identically on every engine in the plan. `SupportsReconciliation` is
true. Identity/generated-column handling is a dialect hook, since `SET IDENTITY_INSERT`,
`OVERRIDING SYSTEM VALUE` and `AUTO_INCREMENT` have nothing in common but their purpose.

**`Generic.InformationSchemaQueries`** — `ListTablesAsync`/`ListColumnsAsync` over `information_schema`,
which Postgres, MySQL and SQL Server all provide. Oracle does not and will supply its own; ODBC and
JDBC have provider metadata APIs instead. So this is a *shared helper a driver may use*, not part of
the generic contract.

## What this phase does not build

Any new driver or `ConnectionDriverType` member — still MSSQL only, still proving the generic layer
against a working engine. No engine-specific staging or upsert writer. No change to MSSQL's own
prefixed implementations.

## How to verify when built

- Unit tests on generated SQL per dialect, no server: segment predicates, the multi-row insert's
  batching arithmetic at several column counts, and the delete+insert pair.
- **`Category=Integration` against MSSQL, driven by `MsSqlDialect`** — the generic pipeline running
  reader → staging → writer end to end and landing the same rows the MSSQL-specific pipeline does. The
  existing `MsSqlPipelineTests` is the model.
- A reconciliation test: rows deleted at the source are removed from the target by a generic
  delete+insert reload, which is the whole reason this writer matters for watermark-mode engines.
- A parameter-limit test at a column count high enough to prove the batching arithmetic, since that is
  the failure this design is specifically guarding against.
- Full suite green; `dotnet build` clean.

## Open questions

- Where the generic staging table lives, and what it is called. `#temp` is not portable; a real table
  needs a schema, a name that cannot collide across concurrent runs, and cleanup that survives a
  crashed worker. The work queue already gives every unit of work a `RunId` — likely the name.
- Whether `DeleteInsert` should chunk its `DELETE` for very large scopes, or leave that to the segment
  size the operator chose. Leaning towards the latter: segments already exist to bound exactly this.

---

# Retrospective

All four generic implementations exist and the pipeline runs end to end against a real SQL Server.
Both open questions are answered below. The claim this phase set out to establish — **a new engine's
driver is a dialect, a connection factory and a catalog, and nothing else** — is now evidenced by
`GenericPipelineTests`, in which none of the three components has a line of SQL Server in it.

## The catalog turned out to be the third thing, not the second

The plan named "a dialect, a connection factory and a catalog query". Building it made the catalog an
explicit interface — `ITableCatalog` — rather than an assumed `information_schema` query, because both
the staging provider and the writer need target column types and neither can assume how to get them.
`InformationSchemaQueries` implements it for the engines that have one; Oracle, ODBC and JDBC will
supply their own. That is exactly the "shared helper a driver may use, not part of the generic
contract" the plan called for, made concrete.

SQL Server has an `information_schema` and deliberately does **not** use it: `MsSqlCatalog` goes to
`sys.columns`, because that is where IDENTITY lives. `information_schema.columns.is_identity` is
standard but unevenly populated, and a generic writer that cannot see identity columns fails at apply
time on precisely the tables that need the flag. `InformationSchemaQueries` therefore reports
`IsIdentity: false` and says so rather than guessing.

## Four dialect hooks, each earned by a real divergence

`SqlDialect` gained `MaxParametersPerStatement`, `OperationMarkerColumnType`, `RenderMultiRowInsert`,
`RenderDropTableIfExists`, `ClassifyForBucketing` and `WriteWithGeneratedColumnOverrideAsync`. None is
speculative — each is a line the generic code could not write without knowing the engine:

- `RenderMultiRowInsert` is a hook rather than a format string because Oracle's `INSERT ALL` is a
  different statement, not different punctuation.
- `WriteWithGeneratedColumnOverrideAsync` is "run this write" rather than "give me a clause" because
  `SET IDENTITY_INSERT` (session state, bracketing), `OVERRIDING SYSTEM VALUE` (part of the statement)
  and MySQL (nothing at all) have their purpose in common and nothing else.
- `MaxParametersPerStatement` defaults to **2100** — the lowest of the engines in scope, not the
  highest — so a dialect that forgets to state its own batches too conservatively rather than emitting
  a statement the server rejects.

`MsSqlSegmentExpansion` moved to `Generic.SegmentExpansion` with its arithmetic untouched; only the
type-name switch became `SqlDialect.ClassifyForBucketing`. `MsSqlDialect` overrides it with SQL
Server's own spellings (`money`, `datetime2`, `datetimeoffset`) and defers to the base for the rest, so
`MsSqlSegmentExpansionTests` — thirteen tests on the boundary maths — pass with only the call updated.

`MsSqlIdentityInsert` now takes `(quotedTarget, requiresIdentityInsert)` instead of an
`MsSqlTargetShape`, which is what let the MSSQL dialect reuse it for the generic writer without the
generic layer knowing what a target shape is.

## Open question 1: where the staging table lives

**In the target's own schema, named `DS_STG_{guid:N}`.** There is no portable scratch namespace —
`#temp` is SQL Server's spelling and its session-scoped semantics, which nothing else shares — so a
real table in a schema the writer already has rights to is the only answer available. The GUID is what
keeps concurrent runs, and concurrent segments of one run, from colliding; `RunId` was considered and
rejected because one run stages once per segment, so the run alone does not identify a staging table.

The name is 39 characters, within every engine in scope (Postgres 63, MySQL 64, SQL Server 128, Oracle
12c+ 128). Oracle 11g and earlier, at 30, is out of scope.

Because the table is real, cleanup is load-bearing rather than tidiness. `CleanupAsync` drops it, and
`StageAsync` also drops it itself if staging fails part-way — the caller only knows to clean up a
change set it was handed, so a mid-stage failure would otherwise leak a table into the operator's
schema on every retry. `Staging_LeavesNoTableBehind` asserts none survive.

## Open question 2: chunking the DELETE

**Left to the segment size the operator chose**, as the plan was leaning. Segments already exist to
bound exactly this, and a second, invisible chunking mechanism underneath them would make the size an
operator picked stop meaning what it says.

## The generic implementations are registered, not just tested

`MsSqlDriver` registers `BatchReload`, `StagingTable` and `DeleteInsert` alongside its own prefixed
ones. Two reasons: "proving the generic layer against a working engine" means running it through the
real `DriverRegistry` that `RunExecutor` resolves Kinds against — a parallel test-only driver would
exercise a path production never takes — and the portable staging provider is a genuine fallback on an
instance where bulk insert is not permitted.

The cost is picker noise: MSSQL replications now see seven readers where they saw three. The prefixed
ones remain faster and remain what the UI defaults to. If the pickers become unwieldy once more
drivers exist, the fix is grouping in the SPA, not withholding a working implementation.

## The parameter-limit trap, measured rather than assumed

`StagingStatement.RowsPerStatement` derives rows-per-statement from the column count against the
dialect's cap. The defect it guards against is the one measured in the benchmark work — a fixed
500-row batch of a 5-column table binds 3000 parameters against SQL Server's 2100. Derived, it is 419.
`Staging_BatchesWithinTheEnginesParameterLimit` runs 1500 rows through it against a real server, which
is three statements rather than one, and `RowsPerStatement_NeverDropsBelowOne` covers the degenerate
case where returning 0 would spin without ever flushing.

## Verification

- `DataSync.Drivers.Generic.Tests` — 33 tests: segment predicates and watermark SQL from phase 17,
  plus the staging DDL/insert rendering, the batching arithmetic at four column counts, and the
  reader's and writer's statements, each asserted through two deliberately different dialects.
- `GenericPipelineTests` (`Category=Integration`) — 7 tests against a live SQL Server: a full reload
  lands every row; a reload removes rows deleted at the source (the reason this writer matters for
  engines with no change data capture); a segmented reload leaves rows outside its range alone even
  when they have drifted; a filter composes with a segment rather than being replaced by it; 1500 rows
  cross the parameter-limit boundary; no staging table survives; and auto segments expand into buckets
  that tile the range so every row lands exactly once.
- `ConnectionsControllerTests` updated: two segmentable readers now, three reconciling writers, and a
  new assertion that Change Tracking is the *only* reader reporting deletes.
- Full .NET suite green: 253 tests across seven projects. Playwright: 12 green.

## Still not built

No new driver, no new `ConnectionDriverType` member — still MSSQL only. No engine-specific staging or
upsert writer beyond the ones that already existed. Postgres `COPY` remains a later phase.
