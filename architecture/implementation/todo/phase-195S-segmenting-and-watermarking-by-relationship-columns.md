# Phase 195S — Segmenting and watermarking by a relationship column

**Status**: Not built.
**Plan reference**: split out of `phase-192S-segmenting-and-watermarking-by-relationship-columns.md`'s own
decisions 1–3 (and the relationship half of decision 5) once implementing that doc's transform-consistency
fix surfaced that this half needs a materially bigger interface change — see 192S's own Retrospective for
exactly what was found and why it didn't fit in the same pass.

## Why

Segmenting and watermarking today can only ever name a column on the mapping's own primary source, even
though relationships (185J–189J) and query-shaped primary sources (190S–192S) are otherwise fully composable
with these readers. There's no way to say "segment by the customer's own region" or "watermark by when the
linked customer row last changed." The latter is a genuinely useful, distinct capability: a watermark
defined on a relationship's own column re-emits every primary row whose *linked* row changed — the correct
way to propagate a foreign-side change forward, something primary-column watermarking structurally cannot
do (a row whose own columns are untouched never re-triggers just because something it looks up did).

## The finding that split this off from 192S

A segment/watermark column's parameter *type* (needed for `ISegmentValueBinder.CreateParameter` — picking
the right `SqlDbType`/`NpgsqlDbType`/etc. for a bound range/list value) comes from cached column metadata.
For a primary-sourced column that's `mapping.SourceColumns`, already reachable by every reader. For a
relationship-sourced column it would need to be `mapping.RelationshipColumns[relationship]` — a cache field
**no reader currently receives at all**. `IChangeReader.ReadChangesAsync`/`IStatementPreview.DescribeAsync`
would need a new parameter carrying it, threaded through every implementation across every driver project —
the same shape 185J's own `relationships: IReadOnlyList<RelationshipConfig>` parameter took, but that one's
two real consumers (`BatchReloadReader`, `WatermarkReader`) were known from the start. This phase needs to
actually confirm the same is true here (or find a third consumer) before making the change, not assume it.

## Decisions carried forward from 192S, unchanged

1. `BatchReloadSegment`'s column-naming subtypes — `ListSegment`, `RangeSegment`, `AutoSegment`, and
   `CustomSegment`'s optional `Column` — each gain an optional `Relationship` field, mirroring
   `ColumnMapping.Relationship` exactly: `null` means the primary source, non-null names a
   `RelationshipConfig.Name`.
2. The watermark reader's options gain a second, optional parameter, `watermarkRelationship`, alongside
   `watermarkColumn` — there's no typed watermark config today (just an options dictionary), so this is a
   second `ParameterDescriptor` entry, not a new field on an existing class.
3. `RelationshipAliases.Assign` unions in these two new references, so a relationship used *only* for
   segmenting or watermarking — projecting no column at all — still gets a join and an alias rendered for
   it.
4. Parameter/type binding for a manually-entered `Range`/`List` segment's bound values comes from the
   relationship's own cached column metadata when the segment is relationship-sourced. Auto-segment
   discovery needs no equivalent decision — it runs `SELECT MIN(<expr>), MAX(<expr>)` and reads back
   whatever type the engine reports for that result column, the same way it already does for a primary one.
5. Filtering or ordering by a relationship-joined expression is a performance concern (very likely
   non-sargable, unable to use an index), not a correctness one — surfaced as a warning, never a block. Same
   undecided surface as 192S's own deferred decision 7; resolve both together.

## What this phase needs to trace that 192S didn't

1. **The actual interface change.** Add `relationshipColumns` (or similar — exact shape and name not decided)
   to `IChangeReader.ReadChangesAsync` and `IStatementPreview.DescribeAsync`, confirm every implementer
   across every driver project (not just the hand-written five — the descriptor/compiled path too) still
   compiles by ignoring it, and confirm `RunExecutor`/`PreviewService`/every other caller has
   `mapping.RelationshipColumns` in scope to pass. This is the bulk of the new work.
2. **`SegmentScope.Build`'s own column-list dispatch.** Given a segment's own `Relationship` field, it needs
   to pick `mapping.SourceColumns` or `mapping.RelationshipColumns[relationship]` — converted to
   `ColumnMetadata` — for `ResolveColumn`'s lookup, mirroring how `ScriptedColumnTransforms.Apply` (194S)
   already picks between the two caches by `ColumnMapping.Relationship`.
3. **The reference-function composition.** `BatchReloadReader`/`WatermarkReader` need to build the
   segment's/watermark's `reference` using `SourceProjection.ResolveReference`-shaped logic (currently
   `private` inside `SourceProjection`, keyed off a whole `ColumnMapping`) generalized to take a bare
   `(relationship, column)` pair, composed with 192S's own `TransformAwareReference` so a relationship
   column with *also* a transform renders correctly — a relationship-sourced segment column is not
   mutually exclusive with a transform on that same column.
4. **`BatchReloadStatement.BuildRange`'s join support.** Unlike `BuildRead` (already relationship-aware since
   187J) and unlike the query-wrapping 191S added, `BuildRange` has never rendered a `JOIN` at all — auto-
   segmenting by a relationship column needs it to, via the same `RelationshipJoins.Render` `BuildRead`
   already uses.
5. **The tie-group-size question** 192S's own Open Question 1 raised: whether the existing tie-safe
   bounded-read mechanism tolerates an arbitrarily large tie group correctly (every row sharing one foreign
   row moves together on that foreign row's watermark tick) — needs answering before this ships, since a
   wrong answer here is a correctness gap in the *existing* bounded-read code, not something this phase
   introduces.

## What this phase does not build

- Any mapping-editor UI for picking a relationship on a segment or watermark column — a real, separate
  frontend task in the shape of 189J's `RelationshipsCard`/`DefaultSegmentingCard` work.
- Any change to `CustomSegment`'s own script-driven predicate.
- Any change to `ScriptedQueryReader`'s `SourceQueryContext` (out of scope per the original mutual-exclusivity
  ruling for that reader).

## How to verify

- Unit tests for `SegmentScope`/`BuildRange`/`WatermarkStatement`'s predicate rendering across every
  (relationship present/absent × transform present/absent) combination, extending 192S's own tests.
- A reconciling-writer integration test with a relationship-sourced segmented column, confirming correctness
  holds when the predicate expression is a joined reference rather than a transform.
- A save-time validation test extending `ReconcileScopeColumnValidationTests` for a relationship-sourced
  scoping column.
- A load/stress test (or at minimum a reasoned check against the existing tie-safe implementation) for the
  large-tie-group concern.
