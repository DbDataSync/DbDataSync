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

## A test fixture bug this phase's own reproduction exposed

`BulkLoadIntegrationTests.PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`'s own doc comment states
its subject plainly: "a replication's ordinary scheduled sync and an on-demand reload are separate units
of work with separate locks, so triggering both at once is normal operation, not contention." That is a
real, still-true claim about phase 8's original Primary/BulkLoad separation — an *already-bootstrapped*
mapping's routine incremental pass and an operator's on-demand full reload genuinely don't collide, since
they're not competing for the same `WorkQueue` row at all.

But the test never gives map-1 (or map-2) a prior pass before firing both triggers — it races them
against a brand-new mapping's very first pass. Before phase 134 that was harmless: a fresh mapping's
first Primary pass read the whole table directly through its own reader, with nothing in common with
whatever the explicit reload was doing. Phase 134 changed that retroactively, without this test being
revisited: a fresh mapping's first Primary pass *now* also auto-requests a Bulk Load for the identical
segment the explicit trigger asks for — so, unchanged, this test quietly started exercising exactly the
race this phase is about, not the benign coexistence scenario its own doc comment describes. It kept
"passing" (mostly) only because of the bug this phase fixes: a losing collision was silently swallowed,
so both sides got to report success regardless of whether real work happened underneath.

**This test needs two changes, not one:**

1. **Fix the fixture to test what it actually claims to test.** Give map-1 and map-2 a real prior pass
   (the same `EnqueueAndDrainAsync`-style warm-up phase 141 used throughout for this exact "a first pass
   means something different now" pattern) before the test's real subject — a genuinely-incremental
   Primary pass racing an on-demand reload against an already-bootstrapped mapping, which is a true
   non-collision and should keep asserting both succeed.
2. **Add a new, separate test for the race this phase actually fixes** — a brand-new mapping (what this
   test accidentally became), an explicit reload racing the mapping's own auto-triggered initial load for
   the identical segment, asserting the losing side (the auto-trigger, i.e. the Primary pass) shows up as
   `RunStatus.Failed` with a clear `errorSummary`, and that a *subsequent* pass for that mapping succeeds
   cleanly once nothing is racing it (the self-healing claim).

**This also reframes the still-unexplained row-count flake** (`Tgt_1` occasionally reading back 0 rows
moments after its Bulk Load run self-reports 9 read/9 written) — previously treated as a second,
independent mystery in phase 141's own retrospective. It may not be independent at all: it was observed
in exactly this same accidentally-colliding scenario, so it's at least as plausible that it's a *further*
symptom of the same unintended collision (a stray write, a Kinds mismatch between whichever side happens
to win) as it is a wholly separate bug. Fix the fixture (item 1 above) first and re-check whether the
row-count flake still reproduces at all before assuming it needs its own separate investigation.

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
- **Confirming whether the row-count flake shares a cause with the fixture bug above** — see that
  section for why it's now the leading hypothesis rather than a confirmed independent bug. This phase
  fixes the fixture and the production race; it doesn't include a dedicated investigation beyond
  re-checking whether the flake still reproduces once both are fixed.
- A recovery endpoint for a mapping stuck in `ReadHold.Loading` for some *other* reason (a crashed
  worker mid-batch, say) — this phase makes the specific strand it describes impossible, it doesn't add
  general-purpose recovery tooling for the hold.

## How to verify when built

- `WorkQueueStoreTests` (or a new file) — a unit test proving the throwing variant actually throws on
  collision and leaves the losing caller's own `BulkLoadBatches` row absent (not merely orphaned).
- `LocalRunnerStateInitialLoadTests` (phase 134's own file) — a new case: two `RequestInitialLoad`-shaped
  calls for the same mapping+segment, second one throws, `ReadHold` stays `None` afterward (not
  `Loading`).
- The real proof, per "A test fixture bug" above: `PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`
  itself, fixed to warm up map-1/map-2 first, run several times locally to confirm both sides genuinely
  and reliably succeed (no more timeout, no more row-count flake — or, if the flake persists even
  post-warm-up, that it's confirmed independent after all). Plus the new, separate test for the
  actual race (a fresh mapping, no warm-up, explicit reload racing the auto-trigger) — the losing Primary
  pass shows up `Failed`, a subsequent pass for that mapping succeeds cleanly.

## Open questions to resolve during implementation

- Is a dedicated exception type / `FailureKind` worth adding (see "What this phase builds" above), or is
  a plain exception with a clear message sufficient given the UI doesn't currently do anything special
  with `FailureKind` beyond `PositionExpired`/`MetadataNotCached`'s own recovery affordances?
- Where exactly should the "throw instead of collapse" flag live on `WorkQueueStore.Enqueue` — a new
  parameter on the existing method (risk: every call site needs to explicitly opt into the old
  behaviour, or the default silently changes for someone) vs. a clearly-named sibling method (no risk to
  existing callers, more surface area). Lean toward the sibling method given `Enqueue` already has eight
  parameters.
