# Phase 90 — Mapping metadata cache: captured on creation, refreshed only on request

**Status**: Not started.
**Plan reference**: `architecture/planning/done/mapping-metadata-cache.md`, resolving the audit at
`architecture/planning/done/metadata-queries-design-time-only.md`.

## The gap

`ColumnMapping` stores no type/key/nullability info for either side, deliberately (its `TargetType`
field's own doc comment explains why: freezing an inference risks staleness). As a result, several
readers and every writer/staging provider re-query the source or target's live catalog on every actual
run to get information a stored cache could answer instead — a real cost, but not a bug to fix blindly,
since a table's shape genuinely can change and a silently stale cache is worse than the live query it
replaces. This phase builds the cache and a deliberate refresh action; it does not yet make anything
read from the cache instead of the source/target.

## What to build

### Schema

`TableMappingConfig` gains `SourceColumns`/`TargetColumns` (`IReadOnlyList<ColumnMetadata>` — the
existing `Name`/`NativeType`/`IsNullable`/`IsPrimaryKey`/`IsIdentity` shape), empty by default.

### Capture on creation

Find where the mapping editor's column-mapping tab currently fetches live source/target columns for its
picker (`MappingSide.tsx`/`ColumnMappingEditor.tsx` and whatever API call backs it — confirm the actual
current mechanism before assuming). Persist that same fetch's result into the new fields when the
mapping is saved, rather than adding a second query whose only purpose is populating the cache.

### Refresh endpoint

A new action on `TableMappingsController` (e.g. `POST .../table-mappings/{mappingName}/refresh-metadata`)
that re-runs the introspection live — the same `ITableCatalog`/`IDriver` methods already used at design
time — and overwrites `SourceColumns`/`TargetColumns`, returning the updated mapping.

### UI

A "Refresh metadata" control on the mapping editor (source and target sides, or one control for both —
your call once laid out) that calls the new endpoint and shows what came back, not a silent update.

## What this phase should not do

- Touch `BatchReloadReader.ExpandAutoSegmentsAsync` or any auto-segmenting behavior — confirmed to stay
  live, no stored substitute exists for sampling a value distribution.
- Change what any reader, writer, or staging provider actually does at read/write time —
  `WatermarkReader`, `BatchReloadReader`, `TriggerAuditReader`, `ScriptedQueryReader`, every writer's
  `TargetShape.LoadAsync`, and `BatchInsertStagingProvider.StageAsync` keep live-querying exactly as
  today. Wiring them to read this cache instead is a real behavior change, deliberately left for its own
  later, separately-reviewed phase.
- Backfill existing mappings' caches — an existing mapping's `SourceColumns`/`TargetColumns` stay empty
  until it's edited (triggering the same capture creation uses) or an operator explicitly hits Refresh.

## How to verify

- A test asserting a newly created mapping's cache is populated from the column-mapping tab's existing
  fetch, with no additional live query introduced.
- A test asserting only the Refresh action updates the cache — an ordinary save that doesn't touch
  source/target config leaves it untouched.
- A test asserting an existing mapping with an empty cache (simulating a pre-this-phase row) works
  correctly everywhere else in the app — nothing yet depends on these fields being populated.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
