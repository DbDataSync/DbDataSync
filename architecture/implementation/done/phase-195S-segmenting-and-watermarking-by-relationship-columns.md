# Phase 195S — Segmenting and watermarking by a relationship column

**Status**: Built.
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

## Retrospective

Built as designed, with two refinements the tracing surfaced.

**Item 1 (the interface change) confirmed exactly as expected**: nine hand-written `IChangeReader`
implementers total (`BatchReloadReader`, `WatermarkReader`, `KeyReconcileReader`, `TriggerAuditReader`,
`MsSqlCdcReader`, `MsSqlChangeTrackingReader`, `OracleFlashbackReader`, `PgLogicalSlotReader`,
`ScriptedQueryReader`) — no descriptor/compiled-path implementer exists at all (`grep` across
`DbDataSync.Drivers.Descriptor`/`DbDataSync.Drivers.Jdbc` for `IChangeReader` found nothing), so "not just
the hand-written five" turned out to still be exactly nine, all hand-written. `relationshipColumns`'s type
matches `sourceColumns`'s own convention exactly — `IReadOnlyDictionary<string, IReadOnlyList<CachedColumn>>`
on `ReadChangesAsync` (raw cache, so a reader can call `RequireAll`/`RequireColumn` itself and throw
`MetadataNotCachedException` only when it's actually needed), `IReadOnlyDictionary<string,
IReadOnlyList<ColumnMetadata>>` on `PreviewRequest` (pre-converted, matching `SourceColumns`/`TargetColumns`
there, since `PreviewService` already resolves everything eagerly). `PreviewRequest` needed no interface
signature change at all beyond the new record field — every `DescribeAsync` implementer already receives
the whole request object. `ReadChangesAsync`'s widening broke ~124 existing test call sites across every
driver test project; fixing them was pure mechanical work (insert an empty `relationshipColumns` dictionary
at the right position) with zero behavioral risk, since no existing test exercises a relationship-sourced
segment/watermark.

**Refinement 1 — `ExpandAutoSegmentsAsync` needed `relationshipColumns` after all, not just
`relationships`.** Decision 4 as written claimed auto-segment discovery "reads back whatever type the
engine reports for that result column," implying no cache lookup was needed the way a manual List/Range
segment's parameter binding needs one. That's not quite what the existing (pre-195S) code does:
`BatchReloadReader.ExpandAutoSegmentsAsync` already resolves the *primary* column's declared type from
`mapping.SourceColumns` (via `RequireColumn`) before calling `SegmentExpansion.BuildBuckets`, because
`BuildBuckets` needs the column's native type string to pick bucketing arithmetic
(`SqlDialect.ClassifyForBucketing`) — the live `MIN`/`MAX` sample gives back *values*, not the declared type
those values' bucketing should follow. A relationship-sourced auto segment needs the identical lookup
against that relationship's own cache. So `ISegmentExpandingReader.ExpandAutoSegmentsAsync` gained both
`relationships` (for `BuildRange`'s join, as planned) and `relationshipColumns` (for the type lookup,
not planned) — cascading through `RunExecutor`/`ReconcileService`/`BulkLoadService`'s three call sites and a
handful of test call sites, all mechanical. `SegmentExpansion.BuildBuckets` also gained an optional
`relationship` parameter that stamps every produced `RangeSegment` with it — an easy miss, since without it
an expanded auto-segment would silently revert to looking primary-sourced downstream.

**Refinement 2 — the writer/reader split in `SegmentScope.ResolveColumn` had to stay a split, not merge.**
The initial design tried to have `SegmentScope.Build` compose the relationship-alias reference *and* the
transform-awareness internally (mirroring how `ScriptedColumnTransforms.Apply` picks between two caches in
one place), which would have meant passing `columnMappings` into `Build` unconditionally. That collides
with the pre-existing convention that `columnMappings` being null-vs-non-null is exactly how `Build` already
tells a reader's call (direct lookup, no translation) apart from a writer's (translate source name to
target name via the mapping, then look up in the target's own single cache) — passing it unconditionally
would have made a writer's target-side lookup try to consult a relationship cache it never has. Resolved by
keeping the discriminator: `ResolveColumn` picks the writer-translate branch whenever `columnMappings` is
supplied (now relationship-aware in its own matching, so a relationship's column and a same-named primary
one are never conflated) and the reader-direct-lookup branch (primary cache or `relationshipColumns`, by the
segment's own `Relationship`) otherwise. The *reference* (how the column is written in SQL text) stays the
caller's job to pre-compose, exactly as it always was — `Build` never sees `columnMappings` for that
purpose at all, only for the writer-translate lookup.

**Decision 4's writer-side implication, made explicit and tested**: a reconciling writer's delete-scope
predicate for a relationship-sourced segment needs *no* join or relationship cache at all — the segment's
column translates (via the matching `(Relationship, SourceColumn)` `ColumnMapping`) straight to a real,
flat target-side column, which the target already stores the resolved value under. `ConfigValidation.
ValidateReconcileScopeColumn`'s mapped-column check became relationship-aware to match (matching on
`Relationship` too, not just `SourceColumn`, so a same-named mapping on the wrong relationship no longer
silently satisfies it) — the same generalization every other relationship-aware lookup in this phase needed.

**The tie-group-size question (item 5) has a definite answer: no correctness gap.** `WITH TIES`/`FETCH
FIRST … ROWS WITH TIES` is defined at the SQL level to include *every* row sharing the boundary value, with
no group-size limit — the mechanism was already correct for an arbitrarily large tie group before this
phase, primary-sourced or not. What relationship-sourced watermarking changes is how *likely* a large tie
group is to occur in ordinary use: many primary rows sharing one foreign row's watermark value is a
structural, common case (every order under one customer), unlike a primary-column tie (which needs two rows
independently written at the exact same instant). The practical effect is that a bounded read's row cap
becomes approximate rather than exact once a large tie group falls at the boundary — documented directly on
the new `watermarkRelationship` `ParameterDescriptor`'s description rather than fixed in code, since there is
nothing to fix: the guarantee that actually matters (no row is ever split across passes) already holds.

**`CustomSegment.Relationship` is a field with no consumer yet** — added for symmetry with the other three
subtypes (decision 1 asked for all four), but "any change to `CustomSegment`'s own script-driven predicate"
stayed explicitly out of scope, so nothing reads it. A future phase wiring relationship support into
`SegmentingStrategyRunner` would need to actually consume it.

**Verified for real**: full non-integration suite (1000+ tests across every project) green throughout, at
every checkpoint. A new integration test against a real SQL Server
(`MergeReconcile_WithARelationshipSourcedSegment_ScopesByTheForeignRowsOwnColumn`) proves the reconciling
writer's no-join, no-transform, real-target-column scoping is correct end to end: a row resolving into the
segment gets inserted, a row already agreeing with the source is left alone, a stray row within the
segment's scope gets deleted, and a row resolving to a different segment value is never touched — the same
guarantees `MergeReconcile_MakesTheSegmentMatchTheSource_AndLeavesEverythingElseAlone` already proved for a
primary-sourced segment. Writing that test surfaced one more thing worth naming: a reconciling writer's
segment scope column is assumed *stable* between target and source (a row doesn't move from one segment to
another mid-reconciliation) — true for a primary column and no less true for a relationship-sourced one,
but easy to trip over by accident when a test's target fixture data is "stale" on the very column the
segment scopes by, rather than on some other, non-scope column.
