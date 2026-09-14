# Phase 134 — an initial load becomes a bulk load

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md`
(resolved 2026-09-04), "Phase B". Depends on phase 133 for the pipeline it routes to.

This is the behaviour change, and the one to review carefully. After it, **no change reader performs a
full load**: `ReadIntent.InitialLoad` runs the Bulk Load pipeline, and the reader is not called at all
for that pass.

## What already exists

More than the planning doc assumed, because phases 101, 107 and 108 landed in between.

**`IPositionCapturing` is built and has no caller.** Phase 101 created it
(`src/DbDataSync.Drivers.Abstractions/IPositionCapturing.cs`), four readers implement it —
`MsSqlChangeTrackingReader`, `MsSqlCdcReader`, `TriggerAuditReader`, `WatermarkReader` — and
`PositionCapturingContractTests` guards which readers may and may not declare it. Phase 101 said
plainly that nothing calls it yet and that the pipeline which would is out of scope. **This phase is
its first caller.** The seam was left deliberately; it is not dead code to be justified.

**Phase 107 already solved this phase's hardest coordination problem.** The planning doc flagged "the
intent must flip only after *every* segment succeeds, and nothing tracks that today" as the hardest
part of the design. It is now tracked: `BulkLoadBatches` (phase 133's rename of `BackfillBatches`)
holds the **planned** segment count — deliberately not `COUNT(RunId)`, which undercounts when an
equivalent segment was already in flight — and `BulkLoadBatchStore.GetRecentBackfills` rolls the
segments up into `SegmentsSucceeded` / `SegmentsFailed` / `SegmentsRunning` with a derived
`State` of `Running` / `Completed` / `CompletedWithFailures`.

So the load id is `TaskRuns.BulkLoadBatchId`, which already exists and is already indexed, and the
completion check is `State == Completed`. **Nothing new needs inventing here** — which is a large
reduction in this phase's scope from what the planning doc anticipated.

**Phase 108 decides which lane it runs on, and gives a benefit for free.** `RunLanes.KindsFor` routes
`RunKind.BulkLoad` to the bulk-load lane, sized independently of change processing. So **an initial
load can never starve incremental sync of consumer slots** — which was the whole point of 108 and
applies to initial loads the moment they become bulk loads.

108 also confirms why the hold below is necessary rather than optional: `RunLocks` are
`(TaskName, RunKind, MappingName)`-scoped, so a `Primary` pass and a `BulkLoad` run **for the same
mapping do not contend** by design. Nothing in the locking model stops a Primary pass running against
a mapping whose initial load is still in flight.

## What this phase will build

### 1. Position capture, in the right order

The correctness crux, and the thing most likely to be got wrong. An initial load is only correct if the
feed's position is captured **before** the table is read:

> capture position → run the bulk load → persist position → flip intent to `Changes`

`MsSqlChangeTrackingReader` gets this right today by construction — it calls its current-version query
first and returns that as the watermark, so changes made *during* the read replay on the next pass.
At-least-once, which is the correct side to err on. Splitting the load out of the reader makes that
ordering explicit rather than incidental, and getting it backwards loses every change made during a
multi-hour load **silently**: the load succeeds, the counts look right, and those rows are never seen
again.

`IPositionCapturing.CapturePositionAsync` is what makes this possible, and it is why phase 101 built it.

### 2. `PendingWatermark`

The captured position needs somewhere to live that is not the live one. A new nullable column pair on
`ChangeWatermarks` beside `Watermark`/`WatermarkTimeUtc`, so **a crashed load cannot leave behind
something that reads as a completed position**.

It implies one in-flight load per mapping, which the row granularity already gives.
`Watermark` has been nullable since phase 100, so the live column simply stays empty until the load
finishes.

### 3. A hold that means "loading"

A new `ReadHold` value. `ReadHold`'s own doc calls its values *reasons* and explicitly leaves room for
another known cause to earn one, so this fits the shape it was given.

It is what stops a `Primary` pass running against a mapping with no valid position — necessary for the
locking reason above. Phase 102 already renders holds on Monitoring, so "loading" appears there without
new UI. The scheduler must also stop queueing Primary passes that would immediately no-op.

**A mapping loading under a paused replication must read as "still not running, and here is why"** —
the same precedence problem phase 102 already solved for its two pause grains, extended by one state.

### 4. Routing, and the deletion

`RunExecutor` resolves the intent as it does today. When it resolves to `InitialLoad`, instead of
handing it to the reader: capture the position, enqueue one `BulkLoad` work item per segment under a
fresh batch id, set the hold, and return. When the batch reaches `Completed`, persist the pending
position as the live one, clear the hold, and flip the intent to `Changes`. `CompletedWithFailures`
does **not** flip anything — the work is still to do, which is the same rule the watermark already
follows.

**And the deletion, in the same phase:**

- every reader's `if (intent == ReadIntent.InitialLoad)` full-load branch comes out
  (`MsSqlChangeTrackingReader.cs:97` and its equivalents)
- `detailed-design.md` §4.1 is rewritten — it currently documents the per-reader full-load rule as *the*
  contract
- `ChangeReaderFirstPassContractTests` **inverts**: it fails a reader that *does* full-load, having
  previously failed one that did not. It keeps its value in the new form for exactly the reason phase 99
  wrote it — the rule is per-reader, so nothing else would catch a sixth reader getting it wrong.

The deletion cannot be split into a later phase. The moment the runner routes `InitialLoad` to the bulk
load pipeline those branches are unreachable, and leaving them would be two paths to one outcome with
only one of them tested.

## How it will be verified

**Unit / state**
- the ordering: a load that captures, then has the source change under it, replays those changes on the
  first `Changes` pass — the test that proves the crux, and it must fail if capture moves after the read
- a crashed load (pending position written, batch not `Completed`) leaves the live watermark untouched
  and the mapping still held
- `CompletedWithFailures` does not flip the intent, clear the hold, or promote the pending position
- `Completed` does all three, once
- a Primary pass against a held mapping does not run, and the scheduler stops queueing them

**Contract**
- `ChangeReaderFirstPassContractTests` inverted — asserted against a deliberately non-conforming stub,
  so the test is seen to fail for the right reason
- `PositionCapturingContractTests` (phase 101) still passes unchanged; this phase adds a caller, not a
  new declaration

**Integration** — a real initial load end to end on Change Tracking and on CDC, with rows inserted at
the source *during* the load, asserting they arrive on the following pass rather than being lost.

**E2E** — Monitoring shows the loading hold, then the mapping in `Changes` with a position, and a
mapping loading under a paused replication reads as held for both reasons.

## Decisions

- **One work item per segment**, under phase 107's existing batch id — the shape a reload already has,
  which is what makes an initial load and a reload genuinely one thing.
- **The intent flips only on `Completed`.**
- **`PendingWatermark` is its own column.**
- **The deletion ships with the routing.**

## Out of scope

- **Live mid-load row counting.** Phase 107 shipped per-segment granularity and named this as future
  work; an initial load inherits that, unchanged.
- **Removing `ReadIntent.InitialLoad`.** It stays, and is the signal. Nothing here removes an intent.
- **`ChangesFromEarliest` / `ChangesFromLatest`.** Genuinely per-reader, unaffected, still declared
  through `IReadIntentDeclaring`.

## Open questions to resolve during implementation

- **What happens when a mapping's segmenting changes mid-load?** `DefaultSegmenting` is read when the
  batch is planned; an operator editing it while a load runs has no effect on the in-flight batch, which
  is probably right but should be stated rather than discovered.
- **Should a failed batch be resumable, or restarted?** `SegmentsFailed` names which segments failed, so
  re-enqueueing only those is possible and is the obvious thing an operator will expect. Whether that is
  this phase or the next depends on how much of phase 107's rollup can be reused for it.
- **Does the captured position need to be re-captured on a resumed batch?** Almost certainly not — the
  original capture is still the correct floor and re-capturing would lose everything in between — but it
  is exactly the kind of thing that looks like a tidy-up to someone reading it later.
