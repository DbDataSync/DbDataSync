# Wiring readers and writers to the cached metadata — cache-only, fail loud when empty

**Status: resolved — ready for an implementation phase doc. The deliberately deferred second half of
`architecture/planning/done/mapping-metadata-cache.md`. Revised from an initial live-fallback draft
before implementation started — see "The design that was rejected" below.**

## What phase 90 built, and what it deliberately didn't

Phase 90 added `TableMappingConfig.SourceColumns`/`TargetColumns`, captured at mapping save time and
updated only by an explicit "Refresh metadata" action. Nothing reads them yet — every consumer
identified in the original audit (`architecture/planning/done/metadata-queries-design-time-only.md`)
still live-queries exactly as before:

- **Source side**: `WatermarkReader.ReadChangesAsync` (the watermark column's type),
  `BatchReloadReader.ReadChangesAsync` (the segment column's type, via `SegmentScope.Build`),
  `TriggerAuditReader.ReadIncrementalAsync`/`ResolveColumnsAsync` (key vs. non-key column split),
  `ScriptedQueryReader.ReadChangesAsync` (source columns for the script's context).
- **Target side**: every writer's `ApplyAsync` via `TargetShape.LoadAsync`
  (`MsSqlDeleteInsertWriter`, `MsSqlMergeReconcileWriter`, `MsSqlMergeWriter`, `DeleteInsertWriter`,
  `SnapshotWriter`, `Scd2Writer`) and `BatchInsertStagingProvider.StageAsync`.
- **Confirmed staying live, unconditionally, forever**: `BatchReloadReader.ExpandAutoSegmentsAsync` —
  auto-segment discovery samples a column's actual current value distribution, which no cache could
  ever substitute for.

## The resolution: always the cache, never a live fallback, fail loud when it's empty

Every one of the six consumers above reads only `SourceColumns`/`TargetColumns` from now on. There is no
live `ITableCatalog` call left in any of their run paths. When the relevant cache is empty (or missing
the specific column needed), the run **fails** — a distinct, named failure kind, with a message that
says plainly what happened and what to do: this mapping's cached metadata isn't there, use Refresh
metadata to populate it.

## The design that was rejected

An earlier draft of this doc proposed a live fallback whenever the cache was empty, specifically to
avoid every pre-existing, never-refreshed mapping failing its next run the moment this phase shipped.
**Overridden, deliberately**: the operator's instruction was cache-only, fail loud, full stop. The
accepted consequence, stated plainly rather than hidden: **on deploy, every mapping using one of these
six consumers that has never been saved or refreshed since phase 90 shipped will fail its next run**,
until an operator explicitly refreshes it. That is the intended behavior, not a rollout risk to be
engineered around — a hard stop with a clear, actionable message is exactly "someone being involved in
that decision," made unavoidable rather than merely available.

## The failure itself

Mirror `PositionExpiredException`'s existing shape (phase 32) — a specific, named exception type
(naming convention is implementation's call, e.g. `MetadataNotCachedException`), carrying the mapping
name and which side/column was needed, caught wherever the run pipeline already catches
`PositionExpiredException` and reports a `TaskRuns.FailureKind`. This gets the notification system's
existing run-failure trigger (phase 77 — any `Failed` run notifies) for free, with no new wiring, the
same way phase 80's watermark-expiry producer rode the same `CompleteRun` path rather than a new one.

## One thing to verify before trusting the cache is equivalent

Cached metadata is captured via `MetadataService`/`IColumnCatalog` — the *design-time* introspection
surface, which the codebase's own doc comments note is a **different path** from what a reader/writer
consults at run time (`ITableCatalog`, the driver-level catalog): "This is the surface an operator
*browses and maps against*... the pipeline reads the driver's own catalog." Both return the same
`ColumnMetadata` shape, but a connection with a bound `metadataProvider` script could make
`MetadataService`'s answer diverge from `ITableCatalog`'s raw one for the same table. If that's possible,
it now matters more than it would have under a live-fallback design: a cache built from a diverging
source could report a column present-but-wrong rather than merely absent. Confirm this before
implementation trusts the two are interchangeable.

## What this phase should build

For each of the six run-time consumers: read the mapping's relevant cached column list; look up the
needed column by name; throw the new failure, with the mapping/side/column named in the message, if the
cache is empty or the column isn't in it. `TargetShape.LoadAsync`/`MsSqlTargetShape.LoadAsync` is shared
by all six writers — fixing it once likely covers five of the eight consumer call sites in one change.

## What this phase should not do

- Touch `ExpandAutoSegmentsAsync` — confirmed, permanently live.
- Backfill any mapping's cache, or add a migration that pre-populates one — the failure on an
  unrefreshed mapping is the intended signal, not a gap to paper over.
- Add any live fallback, partial or otherwise, for a missing column in an empty or incomplete cache.

## How to verify

- A test per consumer: cache populated with the needed column → runs correctly, zero calls to
  `ITableCatalog`/`IDriver` introspection methods (a fake that throws if called proves this directly,
  the same technique phase 87 used to prove `ReaderLagService` stopped calling `IChangeCounterSource`).
- A test per consumer: cache empty → the new failure is thrown, naming the mapping and the missing side,
  not a generic error and not a silent live query.
- A test asserting the failure produces a `Failed` run with the new `FailureKind` and a notification,
  through the existing `CompleteRun`/phase-77 path, with no new producer wired.
- A test asserting a mapping edited or refreshed after this phase ships (populating its cache) runs
  successfully on its very next pass.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean (no SPA
  change expected beyond however the new failure surfaces in run history, which should already display
  any other `FailureKind` the same way).

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-091-reader-writer-metadata-consumption.md`.
