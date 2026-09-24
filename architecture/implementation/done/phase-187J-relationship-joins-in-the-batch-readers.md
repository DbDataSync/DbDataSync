# Phase 187J — Relationship joins in the batch (reload) readers

**Status**: Built. See Retrospective.
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

## Retrospective

Built essentially as designed, with two real deviations found while implementing and testing, not
assumed up front:

- **`RelationshipAliases.Assign`** (new, `src/DbDataSync.Drivers.Generic/RelationshipAliases.cs`) is the
  shared alias-numbering helper the doc's own "both need the same r0/r1 numbering" requirement implied
  but didn't name — computed once per statement from `relationships` + `columnMappings`, and handed to
  both `SourceProjection.Render` and `BatchReloadStatement.BuildRead` so the two can never disagree on
  which alias names which relationship. Only a relationship actually referenced by a `ColumnMapping` gets
  one; a declared-but-unmapped relationship renders no `JOIN` at all, per the doc.
- **`SourceProjection.Render`'s SELECT-list dedupe had to key on `(Relationship, SourceColumn)` together,
  not `SourceColumn` alone.** The existing dedupe (`seen.Add(mapping.SourceColumn)`, for the legitimate
  "one source column feeds two target columns" case) would otherwise treat a relationship's column and
  the primary table's column of the same name as *the same physical column* — a foreign lookup table
  sharing a name with the primary table (both having an "Id", say) is the ordinary case, not a contrived
  one, and the bug this would cause is silent: the second column's value would come from the wrong
  source, not an error. Caught by writing the unit test for it, not by reasoning alone.
- **A real, live "ambiguous column" SQL error, found only by the integration tests, not the unit tests —
  and it was common, not an edge case.** The doc's own design left the primary table's *non*-relationship
  columns referenced unqualified (bare `dialect.QuoteIdentifier`) even once a relationship's `JOIN`
  introduces a second table into the statement. That is fine as long as no joined table shares a primary
  column's name — but a foreign lookup table's own primary key is almost always called "Id" (or "id"),
  which is *also* the near-universal name for the primary table's own key. Both the MsSql and Postgres
  integration tests below failed for real, on the first run, with exactly that: `Ambiguous column name
  'Id'` / `column reference "id" is ambiguous`. Not a hypothetical — the doc's own worked example ("a
  primary row whose foreign key matches gets the looked-up value") is precisely the shape that triggers
  it. **Fixed**: once any relationship is actually joined, the primary table is aliased `base` (as the
  doc already said), and *every* primary-table reference in the statement now goes through that alias —
  not just the ones the doc named. That reaches two places the doc's own plan didn't call out:
  - `SourceProjection.Render`'s default `reference` for non-relationship columns, via a new
    `PrimaryTableReference` helper (one copy in `BatchReloadReader`, a small MsSql-specific twin in
    `MsSqlBatchReloadReader` since it uses `MsSqlDialect.Instance` directly rather than an injected
    `SqlDialect`).
  - `SegmentScope.Build`/`BuildList`/`BuildRange` (`src/DbDataSync.Drivers.Generic/SegmentScope.cs`), which
    renders the segment predicate's own column reference — a segmented reload with a relationship whose
    foreign key also happens to be the segmented column (again, "Id" is the obvious case) would hit the
    identical ambiguity in the `WHERE` clause otherwise. `MsSqlSegmentScope.Build` (a thin per-driver
    binding, not a copy) picked up the same optional `reference` parameter and threads it through.
  Both are optional parameters defaulting to `null`/bare quoting, so every other caller (every reader with
  no relationships, which is every reader today except these two) renders exactly as before — the doc's
  own "byte-for-byte identical with zero relationships" invariant holds for both the SELECT list and the
  predicate, not just the `FROM`/`JOIN` clause. This class of ambiguity in the `BatchReloadStatement.BuildRange`
  auto-segment-sampling query (a *different* method, scoped only to the primary table, no `JOIN` in it at
  all) does not apply and was correctly left untouched, per the doc's own note.
- **Verified for real, not just built**: the doc's own three unit-test shapes (single/multiple join keys,
  multiple relationships aliased `r0`/`r1`, a declared-but-unreferenced relationship, a self-join) plus
  the dedupe/ambiguity cases above — 8 new `SourceProjectionTests`/`PipelineStatementTests` in
  `DbDataSync.Drivers.Generic.Tests` (0 failures). Both required integration tests, run against real
  containers: `MsSqlBatchReloadTests.Reader_WithARelationship_LeftJoinsTheForeignTable_KeepingUnmatchedRows`
  (MsSql-specific reader) and `PostgresPipelineTests.Reader_WithARelationship_LeftJoinsTheForeignTable_KeepingUnmatchedRows`
  (proving the shared `Drivers.Generic` path a Postgres/MySQL/Oracle/JDBC-descriptor source actually
  takes) — both assert a matched foreign key returns the looked-up value and an unmatched one (`NULL` or
  pointing at nothing) still returns the row with the looked-up column `null`. Full suites re-run clean
  after the ambiguity fix: `DbDataSync.Drivers.MsSql.Tests` 262/262, `DbDataSync.Drivers.Postgres.Tests`
  119/133 (the 14 failures are the pre-existing `PgLogicalSlotTests`/`wal_level = replica` container
  misconfiguration 186J's own retrospective already names — confirmed again via `SHOW wal_level`, nothing
  this phase's diff touches), `DbDataSync.Drivers.MySql.Tests` 62/62, `.Oracle.Tests` 58/58, `.Jdbc.Tests`
  32/32, `.DuckDb.Tests` 33/33, `.Generic.Tests` 221/221 (up from 213 — the 8 new ones), `.Descriptor.Tests`
  33/33, and a full solution build clean.
- **Not built**: the frontend (189J) and the incremental/CDC/Change Tracking/Watermark readers (188J), per
  this doc's own stated scope. The SELECT-list/predicate naming-collision fix above is the *only* place
  this phase's own scope was widened beyond the original plan, and only because the integration tests it
  already called for surfaced a real, common-case failure the plan hadn't anticipated — not a speculative
  addition.
