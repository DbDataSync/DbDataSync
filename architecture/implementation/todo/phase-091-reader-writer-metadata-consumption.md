# Phase 91 — Readers and writers consume the cached metadata, binary per side, no partial trust

**Status**: Not started.
**Plan reference**: `architecture/planning/done/reader-writer-metadata-cache-consumption.md` — the
deliberately deferred second half of `architecture/planning/done/mapping-metadata-cache.md`.

## The gap

Phase 90 built `TableMappingConfig.SourceColumns`/`TargetColumns`, captured on save and updated only by
an explicit Refresh action. Nothing reads them. Six run-time consumers still live-query
`ITableCatalog`/`IDriver` introspection on every pass, exactly as before phase 90 existed.

## What to build

### The binary rule, applied uniformly

For each consumer below: if the mapping's relevant cached list (`SourceColumns` for a reader,
`TargetColumns` for a writer/staging provider) is **non-empty**, look up the needed column from it and
never call live introspection for that side, even if the specific column is missing from the cache — a
missing column is the cache's own honest answer ("not there, or not refreshed since it changed"), not a
reason to peek live. If the cached list is **empty**, live-query exactly as today. No third state, no
per-column fallback within an otherwise-populated cache.

### Source-side consumers

- `WatermarkReader.ReadChangesAsync` — the watermark column's type, from `SourceColumns` when populated.
- `BatchReloadReader.ReadChangesAsync` — the segment column's type (feeds `SegmentScope.Build`), from
  `SourceColumns` when populated.
- `TriggerAuditReader.ReadIncrementalAsync`/`ResolveColumnsAsync` — the key/non-key split, from
  `SourceColumns` when populated.
- `ScriptedQueryReader.ReadChangesAsync` — source columns for the script's context, from `SourceColumns`
  when populated.

### Target-side consumers

- `TargetShape.LoadAsync`/`MsSqlTargetShape.LoadAsync` — shared by every writer's `ApplyAsync`
  (`MsSqlDeleteInsertWriter`, `MsSqlMergeReconcileWriter`, `MsSqlMergeWriter`, `DeleteInsertWriter`,
  `SnapshotWriter`, `Scd2Writer`). Fix once here; confirm it actually covers all six call sites rather
  than assuming from the name.
- `BatchInsertStagingProvider.StageAsync` — target/staging columns, from `TargetColumns` when populated.

### Confirm before trusting the cache

`MetadataService`/`IColumnCatalog` (design-time, what phase 90 captures from) and `ITableCatalog`
(run-time, what these six consumers call today) are two different introspection paths in this codebase's
own architecture — a connection with a bound `metadataProvider` script could make them disagree for the
same table. Verify this can't silently produce a wrong answer for a mapping in the cache-in-use state
before wiring anything; if it can, the missing-column handling above already covers the failure mode
(the cache just won't have the column), but confirm that's actually what happens rather than something
worse (a wrong, present value).

## What this phase should not do

- Touch `BatchReloadReader.ExpandAutoSegmentsAsync` — confirmed to stay live permanently.
- Backfill any mapping's cache.
- Add any live fallback for a single missing column inside an otherwise-populated cache — the binary
  per-side rule is deliberate.
- Any SPA change — this is entirely run-time backend behavior.

## How to verify

- Per consumer, a test proving zero introspection calls when the relevant cache is populated (a fake
  `ITableCatalog`/`IDriver` that throws if invoked, the same technique phase 87 used to prove
  `ReaderLagService` stopped calling `IChangeCounterSource`).
- Per consumer, a test proving unchanged (live-querying) behavior when the cache is empty.
- A test asserting a mapping edited after this phase ships transitions from live to cache-only on its
  very next run, with no action beyond the save that populated the cache.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
