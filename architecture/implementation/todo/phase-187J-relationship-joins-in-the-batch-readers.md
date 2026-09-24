# Phase 187J — Relationship joins in the batch (reload) readers

**Status**: Planned, not started. Depends on `phase-186J-relationship-config-and-shared-reader-plumbing.md`
(needs `RelationshipConfig`, `ColumnMapping.Relationship`, and the `relationships` parameter on
`IChangeReader.ReadChangesAsync`/`IStatementPreview.DescribeAsync` to exist first).
**Plan reference**: `phase-185J-declared-relationships-and-foreign-column-lookups.md` (superseded — this
doc carries forward its batch-reader-specific content essentially unchanged).

## Why this is the first reader phase

`BatchReloadReader` (`src/DbDataSync.Drivers.Generic/BatchReloadReader.cs`) and its MsSql-specific twin
`MsSqlBatchReloadReader` (`src/DbDataSync.Drivers.MsSql/MsSqlBatchReloadReader.cs`) both already build a
plain `SELECT <projection> FROM table WHERE <predicate>` with a swappable projection
(`SourceProjection.Render`) and no existing join or table alias to work around. They're the simplest
possible target for "one `JOIN` per relationship" — no base-table aliasing to retrofit, no change-table
join to compose with (that's 188J's problem). Confirmed only Postgres/MySQL/Oracle/JDBC-descriptor drivers
have **no** driver-specific reload reader of their own — they all use the shared `Drivers.Generic`
implementation directly (checked via `grep -rl "BatchReload\b"` across each driver project) — so the
`Drivers.Generic.BatchReloadReader` change in this phase covers every one of those engines automatically;
only MsSql needs a second, parallel edit because it has its own specialized reload reader.

## What this phase builds

### `SourceProjection.Render` — `src/DbDataSync.Drivers.Generic/SourceProjection.cs`

`RenderColumn` needs to alias a relationship-sourced `ColumnMapping` (`mapping.Relationship is not null`)
to that relationship's own join alias (e.g. `r0.[Region]`) instead of calling the primary table's
`reference` callback. This is the same seam the class already has for the Change Tracking reader's own
aliased, joined statement — its own doc comment already describes `reference` as "how a source column is
written *in this statement*... the Change Tracking reader, whose statement joins the table under an alias,
passes something that produces `base.[Region]`." No shape change to `Render`'s public contract or its
`reference` callback parameter — only new branching inside `RenderColumn`, keyed on `mapping.Relationship`,
that needs to know each relationship's assigned alias (`r0`, `r1`, ...; see below for where that mapping is
decided).

`Render`'s existing signature takes `IReadOnlyList<ColumnMapping> columnMappings` — this phase adds a
`relationships` parameter so it can assign and remember each referenced relationship's alias consistently
between the projection and the `FROM`/`JOIN` clause built alongside it (both need the same `r0`/`r1`
numbering for the same relationship name).

### `BatchReloadStatement.BuildRead` — same file

Gains a `relationships` parameter. For each `RelationshipConfig` **actually referenced by at least one
`ColumnMapping`** (a relationship declared but never mapped from renders no join at all — no wasted work for
an unused declaration), renders:

```sql
LEFT JOIN <dialect.QualifyTable(schema, table)> AS r0
  ON base.[<LocalColumn>] = r0.[<ForeignColumn>] AND base.[<LocalColumn2>] = r0.[<ForeignColumn2>] ...
```

— `LEFT JOIN` per 185J's confirmed no-match decision (row kept, looked-up columns `NULL`), one join per
relationship, join keys ANDed within a relationship. The primary table itself needs an alias
(`AS base`, matching Change Tracking's existing convention) the moment any relationship is present, so
every column reference in the statement is unambiguous; with zero relationships in use the statement is
byte-for-byte what it is today (no incidental alias added to the common case). `BatchReloadStatement.BuildRange`
(used only by `ExpandAutoSegmentsAsync` for auto-segmenting) is **not** touched — segmenting always reasons
about the primary table's own column extent, never a joined one, so it has no reason to know about
relationships at all.

### `BatchReloadReader` / `MsSqlBatchReloadReader`

Both thread the new `relationships` parameter (received from 186J's new `IChangeReader` parameter) into
both `BatchReloadStatement.BuildRead` and `SourceProjection.Render`, in `ReadChangesAsync` **and**
`DescribeAsync` alike — the preview surface has to show the real statement, joins included, matching 185J's
resolved "inline in the existing `SourceRead` stage" decision (no new preview stage; the rendered JOIN(s)
just become part of that stage's SQL text, the same way a `ColumnMapping.Transform` already alters it
inline today).

## What this phase does not build

- No config shape, validation, or `IChangeReader` signature change — all of that is 186J, a hard
  prerequisite.
- No incremental/CDC/Change Tracking/Watermark reader changes — see 188J.
- No frontend — see 189J.

## How to verify when built

- Unit tests for `BatchReloadStatement.BuildRead`'s new `JOIN` rendering — no live server needed, matching
  that class's own doc comment ("separated so it can be asserted without a live server"): single join key,
  multiple join keys (ANDed), multiple relationships (aliased `r0`/`r1`/...), a relationship declared but
  not referenced by any `ColumnMapping` (renders no join), and a self-join relationship (the primary table
  appears twice in the statement, once as `base` and once as the joined alias — confirm no ambiguous-column
  or self-reference issue in the generated SQL).
- An integration test against a real SQL Server source proving `LEFT JOIN` behavior end to end: a primary
  row whose foreign key matches gets the looked-up value; a primary row whose foreign key has no match is
  still present in the read, with the looked-up column `null` — not silently dropped. Run against both
  `BatchReloadReader` (a Postgres or MySQL source, proving the shared generic path) and
  `MsSqlBatchReloadReader` (proving the MsSql-specific twin independently, since it is not the same code
  path).
- Existing `BatchReloadReader`/`MsSqlBatchReloadReader` tests (including their `DescribeAsync`/preview
  tests) still pass unchanged for a mapping with zero relationships — this phase must be behaviorally
  invisible to every mapping that doesn't declare any.
