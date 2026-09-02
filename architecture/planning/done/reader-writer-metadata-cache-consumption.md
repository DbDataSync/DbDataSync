# Wiring readers and writers to the cached metadata, without a mass behavior change on deploy

**Status: resolved — ready for an implementation phase doc. The deliberately deferred second half of
`architecture/planning/done/mapping-metadata-cache.md`.**

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

## The problem a naive wiring would create

Phase 90 explicitly did not backfill existing mappings — every mapping that existed before it shipped,
and hasn't been edited or explicitly refreshed since, has an **empty** cache. If this phase simply made
every consumer above require the cache and fail without it, the moment it ships, every one of those
mappings' next run would fail outright, across an entire production install, with no single deliberate
action having caused it. That directly violates the same principle that motivated phase 90 in the first
place — "the standard operation of the tool shouldn't change behaviour without someone being involved in
that decision" — just inverted: a *mass* failure on deploy is exactly the kind of behavior change nobody
decided on, even if the eventual state (cache required) is the right one.

## The resolution

**Per mapping, per side, the switch is binary and keyed on whether the cache is populated — no partial
trust, no live fallback once cached:**

- `SourceColumns`/`TargetColumns` non-empty → use it exclusively. No live `ITableCatalog` call at all,
  not even as a fallback for a single missing column — a column the cache doesn't have is a fact about
  the cache (stale, or the operator hasn't refreshed since a real schema change), which is exactly the
  situation "refresh it yourself" exists to name, not something to paper over with a live peek.
- Empty → live-query exactly as today. Nothing changes for a mapping nobody has touched since phase 90
  shipped.

This means the transition to cache-based behavior happens **exactly when a human causes the cache to
exist** — by editing and saving the mapping (phase 90 already captures on save) or by explicitly hitting
Refresh — never on its own, never for a mapping sitting untouched. A fleet upgrade changes nothing for
anyone until they interact with a mapping; from then on, that mapping is cache-only.

## One thing to verify before trusting the cache is equivalent

Cached metadata is captured via `MetadataService`/`IColumnCatalog` — the *design-time* introspection
surface, which the codebase's own doc comments note is a **different path** from what a reader/writer
consults at run time (`ITableCatalog`, the driver-level catalog): "This is the surface an operator
*browses and maps against*... the pipeline reads the driver's own catalog." Both return the same
`ColumnMetadata` shape, but a connection with a bound `metadataProvider` script could make
`MetadataService`'s answer diverge from `ITableCatalog`'s raw one for the same table. Confirm this can't
happen for a mapping in the cache-in-use state before assuming the two are interchangeable — if it can,
the phase needs to say plainly what a divergence looks like to an operator (most likely: the cached
column a reader/writer needed just isn't in the list, which the "cache doesn't have it, go refresh"
handling above already covers without special-casing this).

## What this phase should build

For each of the six run-time consumers above: read the mapping's relevant cached column list first;
look up the specific column needed (by name) from it; only fall through to a live `ITableCatalog` call
when the cache is empty for that side. `TargetShape.LoadAsync`/`MsSqlTargetShape.LoadAsync` is shared by
all six writers — fixing it once likely covers five of the eight consumer call sites in one change.

## What this phase should not do

- Touch `ExpandAutoSegmentsAsync` — confirmed, permanently live.
- Backfill any mapping's cache — unchanged from phase 90's own decision.
- Add a live fallback for a single missing column within an otherwise-populated cache — the binary,
  per-side rule above is deliberate; a partial trust rule is a second, subtler kind of silent staleness.

## How to verify

- A test per consumer: cache populated → zero calls to `ITableCatalog`/`IDriver` introspection methods
  (a fake that throws if called proves this directly, the same technique phase 87 used to prove
  `ReaderLagService` stopped calling `IChangeCounterSource`).
- A test per consumer: cache empty → behaves exactly as before phase 90, live-querying as always.
- A test asserting a mapping edited after this phase ships (triggering phase 90's existing capture)
  transitions from live-querying to cache-only on its very next run, with no separate action required
  beyond the save that populated the cache.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean (no SPA
  change expected — this is entirely a run-time backend change).

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-091-reader-writer-metadata-consumption.md`.
