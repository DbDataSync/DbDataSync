# Phase 192S — Expression-consistent segment/watermark predicates (relationship-column segmenting split to 195S)

**Status**: Built, narrower than planned. See Retrospective — the relationship-column-segmenting half of
this doc's original design is split out to
`phase-195S-segmenting-and-watermarking-by-relationship-columns.md`, deferred rather than dropped.
**Plan reference**: `phase-191S-shared-from-join-builder-and-retiring-query-readers.md` (the shared FROM/JOIN
builder and widened qualification this phase's predicates run inside). Corrects an assumption made and then
retracted earlier in this same design conversation: segment/watermark columns were first assumed to always
live on the primary source only, alongside relationships merely for qualification safety; the user corrected
this to mean segmenting and watermarking should genuinely be usable *by* a relationship's own column.

## Why

Two related gaps, found by walking through the same question from both the read side and the write side.

**First**, segmenting and watermarking today can only ever name a column on the mapping's own primary
source. There's no way to say "segment by the customer's own region" or "watermark by when the linked
customer row last changed" — even though relationships are otherwise now fully composable with these
readers (185J–189J, 191S). The latter is a genuinely useful, distinct capability, not a niche one: a
watermark defined on a relationship's own column re-emits every primary row whose *linked* row changed,
which is the correct way to propagate a foreign-side change forward — something primary-column watermarking
structurally cannot do (a row whose own columns are untouched never re-triggers just because something it
looks up did).

**Second**, and found while working out how a segment's bounds reach a *delete*: every reconciling writer
(`DeleteInsertWriter`, `KeyReconcileDeleteWriter`, `MsSqlMergeReconcileWriter`, etc.) re-runs `SegmentScope`
a second time against the target, translating only the column's *name* (`ColumnMapping.SourceColumn →
TargetColumn`) and reusing the segment's literal bound values completely unchanged. That's silently wrong
today whenever the segmented column carries a `ColumnMapping.Transform`: the target stores the transformed
value, but the bounds were computed against the raw one, so the delete predicate compares two different
value-spaces without anyone noticing. This is a real, pre-existing bug independent of relationships or
query-shaped sources — it already misbehaves on `main` for any hand-typed transform on a segmented column.

## Decisions made (asked, not guessed)

1. `BatchReloadSegment`'s column-naming subtypes — `ListSegment`, `RangeSegment`, `AutoSegment`, and
   `CustomSegment`'s optional `Column` — each gain an optional `Relationship` field, mirroring
   `ColumnMapping.Relationship` exactly: `null` means the primary source, non-null names a
   `RelationshipConfig.Name`.
2. The watermark reader's options gain a second, optional parameter, `watermarkRelationship`, alongside
   `watermarkColumn`, with the same semantics — there's no typed watermark config today (just an options
   dictionary), so this is a second `ParameterDescriptor` entry, not a new field on an existing class.
3. `RelationshipAliases.Assign` (191S) unions in these two new references, so a relationship used *only* for
   segmenting or watermarking — projecting no column at all — still gets a join and an alias rendered for it.
4. **The predicate and range-discovery projection use the same expression `SourceProjection.Render` would
   emit for that `ColumnMapping`** — the transform applied on top of the base/relationship-qualified
   reference, never a bare column reference, whenever one is present. Concretely: `WHERE
   UPPER(base.[col]) BETWEEN @min AND @max` instead of `WHERE base.[col] BETWEEN ...`, when the mapped
   column has `Transform = "UPPER({{column}})"`. This is what fixes the reconciliation bug, and it fixes it
   with **zero change on the writer side** — the writer's existing "translate the name, reuse the literal
   bounds" logic is already correct once the source filters in the same value-space the target already
   stores, because both sides are now looking at the transformed value by construction, not by accident.
   Relationship-column segmenting/watermarking rides the identical mechanism: whether the expression is a
   bare qualified reference, a transform, or both, one code path (shared with `SourceProjection.Render`,
   factored out rather than reimplemented a second time here) resolves a `ColumnMapping` to its actual SQL
   expression everywhere it's needed — SELECT list, segment predicate, range-discovery MIN/MAX, watermark
   predicate and `ORDER BY`.
5. Parameter/type binding for a manually-entered `Range`/`List` segment's bound values comes from
   `mapping.TargetColumns`'s metadata when the mapped column carries a `Transform` (the actual shape the
   expression evaluates to), and from the source/relationship cache otherwise. Auto-segment discovery needs
   no equivalent decision — it runs `SELECT MIN(<expr>), MAX(<expr>)` and reads back whatever type the
   engine reports for that result column, the same way it already does for a raw one.
6. **A reconciling writer requires the segment/watermark column to resolve to a real `ColumnMapping`** — not
   because of the transform fix (which needs no such restriction), but because the writer still needs some
   actual target column to bind its predicate against. Validated at save time, alongside 190S's other
   save-time rules. Transform and relationship are both fine; simply not being mapped at all is not.
7. **Filtering or ordering by a transform, and doubly so by a relationship-joined expression, is a
   performance concern, not a correctness one** — very likely non-sargable, unable to use an index. Surfaced
   as a warning (preview or validation response; exact surface not decided here), never a block.

## What this phase builds

- `BatchReloadSegment` subtype field additions; the watermark reader's second options parameter.
- `SegmentScope.Build`/`ResolveColumn` gain the relationship parameter, and switch from quoting a bare
  column name to resolving the shared "render this `ColumnMapping`'s full expression" helper — extracted out
  of `SourceProjection.RenderColumn` so both call sites share one implementation of transform substitution
  and relationship qualification, rather than two.
- `BatchReloadStatement.BuildRange`'s projection switches from a bare column to the resolved expression.
- `WatermarkStatement`'s predicate and `ORDER BY` do the same.
- The new reconciling-writer + unmapped-segment/watermark-column save-time validation rule (190S's decision
  7, implemented here).
- The performance-warning surfacing point.

## What this phase does not build

- Any mapping-editor UI for picking a relationship on a segment or watermark column — a real, separate
  frontend task in the shape of 189J's `RelationshipsCard`/`DefaultSegmentingCard` work, not scoped here.
- Any change to `CustomSegment`'s own script-driven predicate — whether a custom segmenting strategy script
  should receive relationship-aware metadata isn't decided in this phase.
- Any change to `ScriptedQueryReader`'s `SourceQueryContext` (still out of scope, per the original
  mutual-exclusivity ruling for that reader).

## Open questions

1. **Tie-group size under relationship-column watermarking.** A relationship-column watermark can produce
   much larger tie groups than a primary-column one — every row sharing one foreign row moves together on
   that foreign row's watermark tick (e.g., every order for a customer, when the customer's own row ticks).
   Whether the existing tie-safe bounded-read mechanism already tolerates an arbitrarily large tie group
   correctly, or whether it has an implicit assumption that ties are rare/small, was not verified this
   session — worth checking before this ships, since a wrong answer here is a correctness gap, not merely a
   performance one.
2. **Exact surface for the performance warning** (preview response, save-time validation response, or both)
   — not decided.

## How to verify

- Unit tests for `SegmentScope`/`BuildRange`/`WatermarkStatement`'s predicate rendering across every
  (relationship present/absent × transform present/absent) combination.
- A reconciling-writer integration test with a transformed, segmented column: a target row outside the
  *transformed* bounds is correctly deleted, one inside them is not — the regression this phase exists to
  fix, asserted directly rather than inferred.
- A reconciling-writer integration test with a relationship-sourced segmented column, confirming the same
  correctness holds when the predicate expression is a joined reference rather than a transform.
- A save-time validation test for the new reconciling-writer + unmapped-column rejection.
- A load/stress test (or at minimum a reasoned check against the existing tie-safe implementation) for Open
  Question 1's large-tie-group concern.

## Retrospective

**Split, not fully built as designed.** Implementing decision 4 (transform-consistent predicates) surfaced
that decisions 1–3 (relationship-column segmenting itself) need a materially bigger interface change than
this doc anticipated: a segment/watermark column's *type* metadata, when it's relationship-sourced, would
have to come from `mapping.RelationshipColumns` — a cache field no reader currently receives at all.
`IChangeReader.ReadChangesAsync`/`IStatementPreview.DescribeAsync` would need a new parameter threaded
through essentially every reader across every driver project (the same shape 185J's own `relationships`
parameter took, but that one had exactly two real consumers from the start; this would too, but discovering
which two, and confirming no third reader secretly wants it, is real work this pass didn't do). Given that,
relationship-column segmenting/watermarking (decisions 1, 2, 3, and the relationship half of 5) is deferred
to **195S**, written up fresh with this finding folded in. Decisions 4, 6, and 7 — the parts that don't need
new cache plumbing — are built here, now, in full.

**What's actually built:**
- `SourceProjection.RenderExpression` gained a `(string sourceColumn, string? transform, Func<string,string>
  reference)` overload beside its existing `(ColumnMapping, Func<string,string>)` one, so a caller with a
  bare column name and transform — not a full `ColumnMapping` — can render the identical expression.
- `SegmentScope.TransformAwareReference(columnName, columnMappings, reference)` (new): wraps a reference
  function so a segment/watermark column that also happens to be an (unqualified) `ColumnMapping` with a
  `Transform` renders through it. Deliberately only ever matches a *primary*-sourced mapping
  (`Relationship is null`) — confirmed by a test — since relationship-sourced matching is exactly the part
  deferred to 195S.
- `BatchReloadStatement.BuildRange` and `WatermarkStatement.BuildRead`/`BuildMaxWatermark` each gained an
  optional `transform` parameter, applied via `SourceProjection.RenderExpression` instead of a bare
  qualified reference. `BatchReloadStatement.BuildRead` needed **no change at all** — its `scopePredicate`
  is already fully rendered by the caller before reaching it, so the transform-awareness lives entirely in
  what `SegmentScope.Build`'s `reference` parameter is given, not in the statement builder.
- `BatchReloadReader`/`WatermarkReader` compute the segment's/watermark's own transform (a bare lookup
  against `columnMappings` by `SourceColumn`, `Relationship is null`) and thread it through every read,
  preview, range-discovery, and max-watermark call site that has `columnMappings` available.
- **One named, deliberate gap**: `IPositionCapturing.CapturePositionAsync` (used for `ChangesFromLatest`
  adoption) has no `ColumnMapping` list in its signature at all — a *second* interface with its own set of
  implementers (5, per `ChangeReaderFirstPassContractTests`) — so its own `GetMaxWatermarkAsync` call stays
  untransformed, documented in code and here rather than silently accepted.
- `ISegmentExpandingReader.ExpandAutoSegmentsAsync` gained a `columnMappings` parameter (two real
  implementers — `BatchReloadReader`, `KeyReconcileReader`, the latter ignoring it — plus three call sites,
  all of which already had `mapping` in scope) so auto-segment discovery can find the auto-segmented
  column's transform the same way.
- Decision 6 (`ConfigValidation.ValidateReconcileScopeColumn`, new): a reconciling writer's segment/watermark
  column must resolve to a real, primary-sourced `ColumnMapping` — checked against the mapping's own
  statically-configured reader options (a default segment, or `watermarkColumn`); a Bulk Load's own
  per-work-item segment is assigned at enqueue time and isn't reachable from saved config, an accepted,
  named limitation of what save-time validation can check.
- Decision 7 (performance warning) — **not built**. Genuinely deferred, no surface decided; tracked as an
  open question still.

**Verified for real**: full solution build, 0 errors, 0 warnings. Full non-integration suite green
throughout. New unit tests: `SegmentScopeTests` (transform application, no-match-is-unchanged,
relationship-sourced-mapping-is-ignored, and the actual regression this phase fixes — a transformed range
predicate matching what a target's stored value would need); `PipelineStatementTests`/
`WatermarkStatementTests` (transform-aware `BuildRange`/`BuildRead`/`BuildMaxWatermark`);
`ReconcileScopeColumnValidationTests` (new file — every combination of reconciling/non-reconciling writer,
mapped/unmapped column, segment vs. watermark, and the relationship-sourced-mapping-doesn't-count edge
case). **Not verified**: an actual end-to-end reconciling-writer integration test proving a transformed,
segmented reload correctly deletes/keeps target rows — this repo's convention tests that class of property
against a live server (see 191S's own identically-shaped gap), which this sandbox has none of.
