# Phase 194S — `sqlColumnExpression` metadata: cache-only fix and relationship-aware lookup

**Status**: Built. See Retrospective.
**Plan reference**: none — a real, pre-existing bug found while tracing how relationship metadata should
reach a bound `sqlColumnExpression` script, independent of the query-source work in 190S–193S. Related to,
but distinct from, `follow-up-run-executor-live-catalog-calls-outside-provisioning.md`, which covers a
second, unrelated live-call violation found by the same sweep.

## Why

`RunExecutor.ApplyScriptedTransformsAsync` currently calls `sourceDriver.ListColumnsAsync(...)` live, on
every pass, to resolve a column's metadata for a bound `ISqlColumnExpression` script — the exact "live call
during a run" anti-pattern the rest of this codebase has avoided since phase 91's cache-only migration. A
schema change is always possible and a cache can always go stale — that's already the operator's own
responsibility to manage (via the existing metadata-refresh action), not something a run should
compensate for by calling the catalog itself. It also has no notion of relationship-sourced columns at all —
resolving purely by bare `SourceColumn` name against whatever `ListColumnsAsync` returned for the primary
table — and `ScriptedColumnTransforms.Apply`'s own reconstruction of a transformed `ColumnMapping` silently
drops `Relationship`, `TargetType`, and `Renames`, keeping only `SourceColumn`/`TargetColumn`/`Transform`.

## Decisions made (asked, not guessed)

1. **`ApplyScriptedTransformsAsync` reads only cache** — `mapping.SourceColumns` for a primary-sourced
   `ColumnMapping`, `mapping.RelationshipColumns[relationship]` for a relationship-sourced one. Never a live
   call, regardless of whether the primary source is a table or a query (190S) — that distinction only ever
   matters upstream, at capture time (a table's `SourceColumns` comes from a catalog refresh, a query's from
   a preview; the consumer here doesn't need to know or care which).
2. **`ScriptedColumnTransforms.Apply`'s lookup keys on `(mapping.Relationship, mapping.SourceColumn)`**
   instead of bare `SourceColumn`, resolving against whichever of the two cache sources
   `mapping.Relationship` selects.
3. **A `ColumnMapping.Clone()`/`WithTransform()` method**, colocated with the class's own field list in
   `TableMappingConfig.cs`, replaces the bare object-initializer reconstruction in
   `ScriptedColumnTransforms.Apply` so `Relationship`/`TargetType`/`Renames` survive a generated transform
   instead of silently disappearing.
4. **`SqlColumnExpressionContext.ColumnReference`'s doc comment is corrected.** It currently claims the value
   is "already quoted and already qualified for the statement being built" — the actual value passed is
   always the literal `ColumnMapping.ColumnToken` (`"{{column}}"`), substituted later by
   `SourceProjection.Render`. The doc comment is factually wrong relative to actual behavior and should say
   so.
5. **Boundary confirmed, no change needed**: a segment or watermark's own column choice (192S) is not a
   `ColumnMapping` at all — it's reader-level config, not mapping-level — so it never reaches
   `ScriptedColumnTransforms.Apply` and needs no special-casing here. The two features share the same
   underlying relationship-cache data source but never intersect.

## What this phase builds

- The signature change to `RunExecutor.ApplyScriptedTransformsAsync`/`ScriptedColumnTransforms.Apply`,
  threading both `mapping.SourceColumns` and `mapping.RelationshipColumns` through instead of a single live
  `ListColumnsAsync` result.
- The `ColumnMapping.Clone()`/`WithTransform()` addition.
- The `SqlColumnExpressionContext.ColumnReference` doc-comment fix.

## What this phase does not build

- Any change to how `sqlColumnExpression`'s output interacts with segmenting or watermarking (192S) — the
  two are confirmed independent per decision 5 above.
- The second live-call violation found by the same sweep (`RunExecutor.WithDerivedNaturalKeyAsync`'s SCD2
  natural-key derivation) — a genuinely separate mechanism and bug, tracked in its own follow-up doc rather
  than folded in here.

## Open questions

1. **Whether `SqlColumnExpressionContext` should gain an explicit field naming which relationship (if any)
   a `Column`'s metadata came from**, for a script author who wants to branch on it. Raised during this
   design review, not resolved — a script can already infer this by comparing `Column` against
   `mapping.SourceColumns` itself, so this may be convenience sugar rather than a requirement.

## How to verify

- A unit test proving `ApplyScriptedTransformsAsync` issues zero driver/catalog calls across a pass,
  regardless of reader Kind or primary-source shape.
- A test for a relationship-sourced `ColumnMapping` resolving its metadata correctly from
  `mapping.RelationshipColumns`, not `mapping.SourceColumns`.
- A test that a generated `Transform` preserves `Relationship`/`TargetType`/`Renames` on the returned
  `ColumnMapping` — the concrete regression `Clone()`/`WithTransform()` exists to fix.

## Retrospective

Built as designed. `RunExecutor.ApplyScriptedTransformsAsync` → `ApplyScriptedTransforms`, now synchronous
(no `await` left once the live `ListColumnsAsync` call was removed) and no longer takes `IDriver
sourceDriver`/`DbConnection sourceConnection`/`SourceTableRef source`/`CancellationToken` at all — it only
needs `mapping` itself, converting `mapping.SourceColumns`/`RelationshipColumns` to `ColumnMetadata` via the
existing `CachedColumn.ToColumnMetadata()` extension. `ScriptedColumnTransforms.Apply` gained
`relationshipColumnMetadata` as a new optional parameter positioned before the existing `log` (every real
caller — `RunExecutor`, `PreviewService`, the test suite — already passed `log` by name, so this was a
non-breaking insertion). `ColumnMapping.WithTransform(string)` added (a single method, not a separate
`Clone()` — nothing else needed a generic clone). `PreviewService`'s own call site is untouched: it already
resolves `ColumnMetadata` itself (via `ScriptedMetadata`/`PreviewRequest`, not the run-time cache), which is
a distinct, already-correct mechanism outside this phase's "no live call during a run" scope.

**Verified for real**: full solution build (`dotnet build`, root — no `.sln` file exists in this repo;
`dotnet build` at the repo root discovers every project directly, matching what `ci.yml` already does), 0
errors, 2 pre-existing unrelated warnings. `DbDataSync.Scripting.Tests` 64/64 (62 pre-existing + 2 new:
relationship-sourced metadata resolving from `RelationshipColumns` rather than the primary cache even when
both share a same-named column, and a generated transform preserving
`Relationship`/`TargetType`/`Renames`). `DbDataSync.TaskRunner.Tests` 47/47 unaffected.

