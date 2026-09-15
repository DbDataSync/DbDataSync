# Phase 143 — a losing auto-triggered initial load fails cleanly instead of stranding `ReadHold`

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/initial-load-pending-batch-stranded-by-a-concurrent-reload.md`
— the diagnosis, evidence, and design discussion live there; this doc carries the design forward into a
buildable phase. Fixes a real bug in phase 134's `RequestInitialLoad`/`ReadHold.Loading` machinery
(`architecture/implementation/done/phase-134-initial-load-becomes-a-bulk-load.md`), found while chasing
`BulkLoadIntegrationTests.PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`'s flakiness during phase
141 (`architecture/implementation/done/phase-141-dotnet-integration-remaining-failures.md`) but not
fixed there, since it's a production correctness bug, not a test problem.

## Why

`LocalRunnerState.RequestInitialLoad` writes a mapping's pending-load state (`ChangeWatermarks.
PendingBulkLoadBatchId`, `ReadHold.Loading`) *before* knowing whether its own attempt to enqueue that
load will actually create new work or collide with other in-flight work for the same mapping+segment
(`WorkQueueStore.Enqueue`'s own `UX_WorkQueue_InFlight` uniqueness constraint silently collapses two
concurrent requests for the identical segment into one real `WorkQueue` row — correct behaviour when
the two requests are the same kind of thing, e.g. two identical reload clicks, but this is the *only*
caller that also writes durable pending-load bookkeeping ahead of knowing the outcome). When a mapping's
own first-ever Primary pass auto-requests an initial load at the same moment an operator manually
reloads that mapping, the auto-trigger reliably loses the `WorkQueue` race (confirmed: it runs later,
from inside Primary-pass processing, after the operator's HTTP request has already enqueued). Its own
`SetPendingLoad` call already ran, though, so the mapping's `ChangeWatermarks` row is left pointing at a
batch that will never complete — `ReadHold` stays `Loading` forever, and `SchedulerService.FilterHeld`
(the only place that hold is enforced) never schedules another Primary pass for that mapping again.
Reproduced deterministically: 15 of 15 local runs of a real wait for the hold to clear timed out.

## What this phase builds

**`src/DbDataSync.State/LocalRunnerState.cs`** — reorder `RequestInitialLoad` so the enqueue attempt
happens first, and `SetPendingLoad` only runs on success:

```csharp
public void RequestInitialLoad(
    string taskName, string mappingName, string sourceTable,
    string capturedPosition, DateTimeOffset? capturedPositionTimeUtc)
{
    var batchId = Guid.NewGuid().ToString("N");

    // Throws if this loses the WorkQueue race for this mapping+segment — see WorkQueueStore below.
    // Only on success does this mapping's pending-load state get written at all.
    initialLoadEnqueuer.Value.EnqueueForInitialLoadAsync(taskName, mappingName, batchId, CancellationToken.None)
        .GetAwaiter().GetResult();

    watermarks.SetPendingLoad(taskName, mappingName, sourceTable, capturedPosition, capturedPositionTimeUtc, batchId);
}
```

**`src/DbDataSync.State/WorkQueueStore.cs`** — `Enqueue`'s own `if (!inserted)` branch currently returns
the existing item's `RunId` instead of the fresh one (silent collapse). Add a variant — a `bool
throwOnCollision` parameter, or a separate `EnqueueOrThrow`-style method, whichever reads more like the
rest of this store's own API — that throws instead, for the auto-triggered-initial-load path only. Every
other caller (`ReconcileService`, `SchedulerService`, the operator-facing `POST .../bulk-load` endpoint)
keeps today's collapse behaviour unchanged — collapsing two *identical* requests from the same kind of
source is correct for them (see the existing, passing
`BulkLoadIntegrationTests.TwoIdenticalBulkLoadTriggers_CollapseIntoOneRun`), and this phase must not
change that.

**`src/DbDataSync.Api/Services/BulkLoadService.cs`** (`EnqueueForInitialLoadAsync` /
`CreateBatchAndEnqueueAsync`) and **`tests/DbDataSync.TaskRunner.Tests/RealInitialLoadEnqueuer.cs`** (the
test-local mirror `IInitialLoadEnqueuer` implementation phase 141 added) — both need to call the new
throwing variant instead of the collapsing one, since both are reached only from
`RequestInitialLoad`'s own call path. **No change to `IInitialLoadEnqueuer`'s own interface** — it stays
`Task`, not `Task<...>`; the thrown exception already has an unguarded path to the surface (see below),
so no new return value needs plumbing through it.

**No new catch block anywhere.** `RunExecutor.RunMappingAsync`'s call to `state.RequestInitialLoad(...)`
is a "Prerequisite" call by the interface's own doc, deliberately not wrapped — its failure already
propagates to `ProcessWorkItemAsync`'s existing generic exception handling, landing this Primary pass as
an ordinary `RunStatus.Failed` row with `errorSummary` set to the exception's message. Consider (open
question below) whether that message deserves a dedicated `FailureKind` (matching
`PositionExpiredException`'s "known cause, known fix" treatment,
`src/DbDataSync.TaskRunner/RunExecutor.cs`'s existing catch clauses) so the UI can frame it as
self-healing rather than an undifferentiated failure.

**Net effect**: because `SetPendingLoad` never runs on the losing side, `ReadHold` never leaves whatever
it already was (`None`, in the scenario this phase is about) — the strand becomes structurally
impossible rather than merely rarer. The mapping's next scheduled Primary pass (ordinary
`SchedulerService` cadence) tries the whole capture-and-request sequence again on its own; if the
winning reload has finished by then it succeeds, if not it fails the same way and retries again next
tick. No operator recovery endpoint needed.

## Out of scope

- **The multi-segment case.** An auto-triggered initial load enqueues one segment per
  `mapping.DefaultSegmenting` entry (empty meaning a single `FullSegment`) inside one batch
  (`BulkLoadBatchStore.CreateBatch`, `SegmentCount = segments.Count`). For a mapping with more than one
  configured segment, some segments could win their own `WorkQueue` race while others lose — this
  phase's "throw on any collision" still leaves whichever segments *did* win as real, enqueued work
  belonging to a batch this attempt is about to declare a loss on, so that batch's own `SegmentCount`
  will never be satisfied by its own completions — a smaller, more contained version of today's bug (an
  orphaned batch that never reaches `BulkLoadState.Completed`), not a new one, and needs the
  batch-create-plus-enqueue sequence to become one atomic operation to close fully (currently two
  separate store calls, each its own transaction). Narrower than the single-segment case this phase
  fixes — it additionally needs an operator to manually reload exactly the same custom segment scheme a
  mapping's own `DefaultSegmenting` already uses, in the same narrow window. Tracked as a known
  follow-on, not blocking this phase.
- **The separate, still-unexplained row-count flake** in the same test
  (`BulkLoadIntegrationTests.PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`'s `Tgt_1` occasionally
  reading back 0 rows moments after its Bulk Load run self-reports 9 read/9 written) — phase 141's own
  retrospective is explicit this is a different, independently-caused symptom (the *winning* run's own
  write is what's reported and apparently missing; that has nothing to do with the losing run's doomed
  batch this phase fixes). This phase should make the test's `ReadHold`-related timeout risk (if a wait
  for hold-clearing were ever added to it) go away, but is not expected to change the row-count flake's
  own occurrence rate — re-check both once this phase lands, since it's possible they turn out to share
  a cause after all.
- A recovery endpoint for a mapping stuck in `ReadHold.Loading` for some *other* reason (a crashed
  worker mid-batch, say) — this phase makes the specific strand it describes impossible, it doesn't add
  general-purpose recovery tooling for the hold.

## How to verify when built

- `WorkQueueStoreTests` (or a new file) — a unit test proving the throwing variant actually throws on
  collision and leaves the losing caller's own `BulkLoadBatches` row absent (not merely orphaned).
- `LocalRunnerStateInitialLoadTests` (phase 134's own file) — a new case: two `RequestInitialLoad`-shaped
  calls for the same mapping+segment, second one throws, `ReadHold` stays `None` afterward (not
  `Loading`).
- The real proof: extend `BulkLoadIntegrationTests.PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`
  (or a new, adjacent test) to assert the losing Primary pass — map-1's, in that test's specific
  scenario — actually shows up as `RunStatus.Failed` with a clear `errorSummary`, and that a *subsequent*
  scheduled pass for that mapping succeeds cleanly (the self-healing claim, not just "it doesn't hang").
- Re-run `PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed` itself several times locally (it was
  reliably reproducible via 15 back-to-back runs during diagnosis) to confirm the specific timeout this
  phase targets is gone.

## Open questions to resolve during implementation

- Is a dedicated exception type / `FailureKind` worth adding (see "What this phase builds" above), or is
  a plain exception with a clear message sufficient given the UI doesn't currently do anything special
  with `FailureKind` beyond `PositionExpired`/`MetadataNotCached`'s own recovery affordances?
- Where exactly should the "throw instead of collapse" flag live on `WorkQueueStore.Enqueue` — a new
  parameter on the existing method (risk: every call site needs to explicitly opt into the old
  behaviour, or the default silently changes for someone) vs. a clearly-named sibling method (no risk to
  existing callers, more surface area). Lean toward the sibling method given `Enqueue` already has eight
  parameters.
