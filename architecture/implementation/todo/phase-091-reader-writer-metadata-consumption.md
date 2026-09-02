# Phase 91 — Readers and writers run from the cache only; an empty cache fails the run

**Status**: Not started.
**Plan reference**: `architecture/planning/done/reader-writer-metadata-cache-consumption.md` — the
deliberately deferred second half of `architecture/planning/done/mapping-metadata-cache.md`, revised
before dispatch from an initial live-fallback design to cache-only/fail-loud. Read the plan doc's
"The design that was rejected" section — it matters for why this phase has no fallback path at all.

## The gap

Phase 90 built `TableMappingConfig.SourceColumns`/`TargetColumns`, captured on save and updated only by
an explicit Refresh action. Nothing reads them. Six run-time consumers still live-query
`ITableCatalog`/`IDriver` introspection on every pass, exactly as before phase 90 existed.

## What to build

### The rule: cache only, no live fallback, ever

For each consumer below, read the mapping's relevant cached column list (`SourceColumns` for a reader,
`TargetColumns` for a writer/staging provider) and look up the needed column by name. If the list is
empty, or the specific column isn't in it, **throw** — never fall through to a live catalog call. This
is a deliberate behavior change on deploy: any mapping that hasn't been saved or explicitly refreshed
since phase 90 shipped will fail its next run under one of these consumers. That's accepted, not a
regression to avoid.

### The failure

A new, named exception (naming your call — `MetadataNotCachedException` is a reasonable starting point),
mirroring `PositionExpiredException`'s existing shape (phase 32): carries the mapping name and which
side/column was needed, with a message telling the operator plainly what happened and what to do — use
Refresh metadata. Caught wherever the run pipeline already catches `PositionExpiredException`, reported
as a distinct `TaskRuns.FailureKind`. Confirm this rides the existing `CompleteRun`/phase-77
run-failure-notification path with no new wiring, the same way phase 80's watermark-expiry producer did
— don't build a second notification path if the first already covers it.

### Source-side consumers

- `WatermarkReader.ReadChangesAsync` — the watermark column, from `SourceColumns`.
- `BatchReloadReader.ReadChangesAsync` — the segment column (feeds `SegmentScope.Build`), from
  `SourceColumns`.
- `TriggerAuditReader.ReadIncrementalAsync`/`ResolveColumnsAsync` — the key/non-key split, from
  `SourceColumns`.
- `ScriptedQueryReader.ReadChangesAsync` — source columns for the script's context, from
  `SourceColumns`.

### Target-side consumers

- `TargetShape.LoadAsync`/`MsSqlTargetShape.LoadAsync` — shared by every writer's `ApplyAsync`
  (`MsSqlDeleteInsertWriter`, `MsSqlMergeReconcileWriter`, `MsSqlMergeWriter`, `DeleteInsertWriter`,
  `SnapshotWriter`, `Scd2Writer`). Fix once here; confirm it actually covers all six call sites rather
  than assuming from the name.
- `BatchInsertStagingProvider.StageAsync` — target/staging columns, from `TargetColumns`.

### Confirm before trusting the cache

`MetadataService`/`IColumnCatalog` (design-time, what phase 90 captures from) and `ITableCatalog`
(run-time, what these six consumers call today) are two different introspection paths in this codebase's
own architecture — a connection with a bound `metadataProvider` script could make them disagree for the
same table. Under a fail-loud design this matters more than it would have under a fallback one: a
diverging cache could report a column present-but-wrong instead of merely absent. Verify this before
wiring anything.

## What this phase should not do

- Touch `BatchReloadReader.ExpandAutoSegmentsAsync` — confirmed to stay live permanently.
- Backfill any mapping's cache, or add a migration/startup step that pre-populates one.
- Add any live fallback, full or partial, anywhere in these six consumers.
- Any SPA change beyond however the new `FailureKind` already displays in run history — check it renders
  sensibly before assuming nothing's needed, but don't build new UI for it.

## How to verify

- Per consumer, a test proving the cache is used and no introspection call happens when it's populated
  with the needed column (a fake `ITableCatalog`/`IDriver` that throws if invoked, the same technique
  phase 87 used to prove `ReaderLagService` stopped calling `IChangeCounterSource`).
- Per consumer, a test proving the new failure is thrown — naming the mapping and the missing side/
  column — when the cache is empty or missing that column, and that no live query happens first.
- A test asserting the failure produces a `Failed` run with the new `FailureKind` and a notification,
  through the existing path, with no new producer wired.
- A test asserting a mapping refreshed or re-saved after this phase ships runs successfully on its next
  pass.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
