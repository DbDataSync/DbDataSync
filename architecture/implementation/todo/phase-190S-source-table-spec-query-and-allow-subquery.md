# Phase 190S — Query-shaped sources: `SourceTableSpec.Query`, `AllowSubquery`, and validation

**Status**: Not built.
**Plan reference**: none — raised directly in conversation, as a follow-on to
`phase-185J-declared-relationships-and-foreign-column-lookups.md` through
`phase-189J-relationships-and-foreign-columns-in-the-mapping-editor.md`. Those four phases gave the mapping
model a first-class join to a real foreign table; this phase (and its siblings 191S–194S) give it a
first-class *query-shaped primary source*, designed so the two compose instead of remaining separate,
mutually-exclusive escape hatches.

## Why

Today a custom source query is a distinct reader Kind (`RawQueryReader`/`DuckDbQueryReader`, Kinds
`Query`/`DuckDbQuery`) with its own token-substitution mechanism (`QuerySegmentTokens`) for segmenting, no
watermark support, and no relationship support — an operator who needs a hand-written `SELECT` gives up
every other reader capability a table-backed mapping gets. Meanwhile 185J–189J's relationship/lookup
feature only ever targets a mapping whose primary source is a real table.

Both gaps have the same fix: stop treating "the source is a query" as a reader Kind, and make it a property
of the source itself. Once a query is wrapped, byte-for-byte unmodified, as a derived table
(`(<query>) AS base`), it's an ordinary FROM-clause source — every existing table-oriented reader
(`BatchReloadReader`, `WatermarkReader`) can compose around it exactly as it already does for a real table,
with no new reader class. Tokens are rejected outright: they make a full reload impossible, make a
preview impossible without specialized substitution, and mean an operator can't run the same query outside
this tool. A query that can't be used as a subquery just means the features that need one don't work for
that source — not that they don't work at all.

## Decisions made (asked, not guessed)

1. **`SourceTableSpec`-level, not a new reader Kind.** `TableSpec.Table` relaxes from `required string` to
   `string?`; `SourceTableSpec`/`TableRef`/`SourceTableRef` each gain `string? Query`, mutually exclusive
   with `Table` (exactly one of the two must be set — the same "exactly one" shape
   `ConfigValidation.ValidateRelationships` already enforces for a `ColumnMapping.Relationship` reference).
2. **No tokens.** The wrap is always `(<query>) AS base` — a fixed alias, matching the existing Change
   Tracking / relationship-join convention, so no new "does `base` need qualifying" case is needed anywhere
   downstream (see 191S).
3. **Conditional wrapping for reload-style reading; unconditional for watermark.** A reload-style pass
   (`BatchReloadReader`) wraps only when something actually needs it: a real (non-null, non-`Full`) segment,
   a relationship join, or a `sqlColumnExpression`-generated transform. Absent all three, the query runs
   completely unwrapped. Watermark reading needs `ORDER BY` for its tie-safe bounded-read guarantee on
   essentially every pass, including the first — so once the primary source is query-shaped, wrapping is
   effectively always in effect for `WatermarkReader`, not conditional. This is safe unconditionally *because*
   of decision 5 below: a query-shaped source can never be saved with `Watermark` as its reader unless
   `AllowSubquery` is `true`.
4. **Relationships are allowed on a query-shaped primary source.** A relationship's own foreign side is
   still always a real table (185J/186J's own constraint, unchanged); what's changing is that the *primary*
   side can now be a query too, and the two compose — a relationship just adds its `LEFT JOIN` onto whatever
   the primary `FROM` clause already is, wrapped or not.
5. **`AllowSubquery: bool` (new, on `SourceTableSpec`, default `true`)** — an explicit, persisted,
   operator-controlled value, never silently inferred from a preview's success or failure, and never reset
   when the query text is edited. It gates two different enforcement levels:
   - **Hard validation, always, at save**: a mapping cannot be saved if its effective reader
     (`PipelineResolution.Reader(task, mapping)`) resolves to `Watermark` while its query-shaped source has
     `AllowSubquery = false` — there is no degraded mode for watermark. This applies regardless of how an
     operator's UI navigation arrived at that combination; the UI does not need to prevent every invalid
     intermediate state (picking a table, then Watermark, then switching to a query source is fine to allow
     transiently — it's caught here, at save).
   - **Soft/silent unavailability for reload-style readers**: segmenting, relationships, and
     `sqlColumnExpression` transforms simply don't apply when `AllowSubquery = false` — the plain query still
     runs, just without those features. Frontend graying-out is a courtesy only, not the enforcement
     mechanism.
6. **Metadata for a query-shaped source comes only from the existing preview flow, unchanged.** No new
   backend "describe the query" mechanism. `ScriptTestService.PreviewQueryAsync` → the frontend's
   `queryColumns` state → `TableMappingForm.tsx`'s existing `captureFor`/save logic is the only path that
   populates `mapping.SourceColumns` for a query-shaped source. Without a preview, there is no metadata — an
   accepted, normal state, not an error.
7. **A reconciling writer requires the segmented/watermarked column to resolve to a real `ColumnMapping`.**
   Independent of query-shaped sources specifically, but validated here alongside the other save-time rules:
   `IChangeWriter.SupportsReconciliation` needs a real target column to bind its delete-scope predicate
   against (see 192S for why), so a segment/auto-segment/watermark column that isn't mapped to any
   `ColumnMapping` is rejected at save whenever the resolved writer reconciles.

## What this phase builds

- `src/DbDataSync.Core/Config/TableMappingConfig.cs`: `TableSpec.Table` → `string?`; `SourceTableSpec`,
  `TableRef`, `SourceTableRef` each gain `string? Query` and `bool AllowSubquery { get; set; } = true`.
- `src/DbDataSync.Core/Config/ConfigValidation.cs`: a new rule, shaped like `ValidateRelationships`,
  rejecting a source with both `Table` and `Query` set, or neither.
- `src/DbDataSync.Core/Config/ConfigRepository.cs`'s `ValidateTableMapping` (already has `task` in scope and
  already calls `ConfigValidation.ValidateRelationships`/resolves `PipelineResolution.Reader`/`.Writer`
  nearby — the exact call site): the new hard "Watermark + `AllowSubquery = false`" rejection, and the new
  "reconciling writer needs the segment/watermark column mapped" rejection (decision 7).
- `src/DbDataSync.Web/src/pages/replication-detail/querySource.ts`: `isQuerySource(task, pipeline)` stops
  checking `effectiveReader(...)?.kind` against `QUERY_READER_KINDS` and instead checks
  `SourceTableSpec.Query` directly.

## What this phase does not build

- The actual subquery-wrapping SQL generation inside the readers, the shared FROM/JOIN builder, and
  retiring `RawQueryReader`/`DuckDbQueryReader`/`QuerySegmentTokens`/`MsSqlBatchReloadReader` themselves —
  all **191S**.
- The relationship/transform-consistent predicate expressions inside segmenting and watermarking — **192S**.
- The preview max-rows/failure-recovery/stale-metadata UI — **193S**.
- The `sqlColumnExpression` cache-only fix — **194S**, an independent pre-existing bug found along the way.

## Open questions

1. **Migration for mappings already saved with the `Query`/`DuckDbQuery` Kind.** Unlike a brand-new field,
   these mappings exist today with their query text living in a reader Kind's `options["query"]`, not in
   `SourceTableSpec.Query`. This repo's general stance is no migration path for config shape changes (see
   phase 133's retrospective — "no serious installations yet"), but a Kind actually being retired (191S) is
   a stronger case than a field being added, since the mapping becomes unloadable rather than merely
   differently-shaped. Whether that calls for an explicit one-time converter, or whether "no serious
   installations yet" still covers this, isn't decided here.
2. **The `MsSqlDriverKinds.BatchReload` Kind string**, used by the now-redundant `MsSqlBatchReloadReader`
   (see 191S) — same class of concern as above, tracked there.

## How to verify

- Unit tests for the mutual-exclusivity validation (`Table`+`Query` both set, both unset, exactly one set).
- Unit tests for the hard Watermark+`AllowSubquery=false` rejection, including the "arrived here by
  switching settings in an odd order" scenario the decision explicitly allows as an intermediate state.
- Unit tests for the reconciling-writer+unmapped-segment-column rejection.
- A round-trip test confirming an existing table-shaped mapping's saved config is byte-for-byte unaffected
  by the new optional fields (`Query`/`AllowSubquery` both absent, `Table` still required-in-practice for
  every mapping written before this phase).
