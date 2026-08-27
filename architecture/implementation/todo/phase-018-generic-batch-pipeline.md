# Phase 18 — Generic Batch Pipeline (planned)

**Status**: Planned, not started
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
