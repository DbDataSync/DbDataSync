# Phase 143 — a losing auto-triggered initial load fails cleanly instead of stranding `ReadHold`

**Status**: Complete. `dotnet-integration`'s last known failure — the intermittent
`BulkLoadIntegrationTests` row-count flake phase 141 left open — is gone: it shared the fixture bug this
phase found, not a separate cause. `TaskRunner.Tests` (32/32), `Api.Tests` (70/70 — up from 69, a new
test added), and `DbDataSync.Drivers.MsSql.Tests` (123/123) all pass locally, including 8 back-to-back
full runs of the previously-flaky file with zero failures (64/64 individual test executions). Found one
more, genuinely separate pre-existing gap while building the new test — written up, not fixed here (see
"What was found but not fixed" below).
**Plan reference**: `architecture/planning/done/initial-load-pending-batch-stranded-by-a-concurrent-reload.md`
— the diagnosis, evidence, and design discussion live there. Fixes a real bug in phase 134's
`RequestInitialLoad`/`ReadHold.Loading` machinery
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

## What this phase built

**`src/DbDataSync.State/LocalRunnerState.cs`** — `RequestInitialLoad` reordered: the enqueue attempt
happens first, and `SetPendingLoad` only runs on success.

**`src/DbDataSync.State/WorkQueueStore.cs`** — a new sibling method, `EnqueueOrThrow`, alongside the
existing `Enqueue` (both now call a shared private `EnqueueCore` with a `throwOnCollision` flag) — a
method, not a parameter on `Enqueue` itself, so no existing caller's behaviour could silently change.
Throws the new `WorkQueueCollisionException` (`src/DbDataSync.State/WorkQueueCollisionException.cs`) on
a lost race, used only by the auto-triggered-initial-load path; every other caller (`ReconcileService`,
`SchedulerService`, the operator-facing `POST .../bulk-load` endpoint) keeps `Enqueue`'s collapse
behaviour unchanged — proven still correct by the existing, still-passing
`BulkLoadIntegrationTests.TwoIdenticalBulkLoadTriggers_CollapseIntoOneRun`.

**`src/DbDataSync.Api/Services/BulkLoadService.cs`** (`CreateBatchAndEnqueueAsync` gained a
`throwOnCollision` parameter, `true` only from `EnqueueForInitialLoadAsync`) and
**`tests/DbDataSync.TaskRunner.Tests/RealInitialLoadEnqueuer.cs`** (phase 141's test-local mirror) both
now call `EnqueueOrThrow`. `IInitialLoadEnqueuer`'s own interface is unchanged (still `Task`) — see the
HTTP-boundary work below for why that was still enough.

**A real complication the original design missed: this call crosses an HTTP boundary in production**,
and none of the exception types `RunExecutor` already special-cases (`PositionExpiredException`,
`MetadataNotCachedException`) do — they're thrown by a reader called directly inside `RunMappingAsync`,
same process. `RequestInitialLoad` goes through `IRunnerState`, and the real production topology is
`RemoteRunnerState` (a spawned `DbDataSync.TaskRunner` child process) talking over loopback HTTP to
`RunnerStateEndpoints` on the API — `LocalRunnerState` (same-process, no HTTP) is what phase 134's own
unit tests use, not what a real deployment runs. An unhandled exception from the `/request-initial-load`
endpoint would have surfaced as an unhandled 500, which `RemoteRunnerState.IsUnreachable` already
classifies as "the owner is gone" (500+, matching its own doc: "Not '4xx is a bug', '5xx' means retry
then give up") — turning an ordinary, self-healing loss into the runner declaring its state owner lost
and shutting down. Fixed by:
- The endpoint catching `WorkQueueCollisionException` explicitly and returning `Results.Conflict` (409)
  with a small `WorkQueueCollisionResponse` DTO (`src/DbDataSync.State/Remote/StateProtocol.cs`) carrying
  the exception's own fields.
- `RemoteRunnerState.RequestInitialLoad` no longer using the generic `Required` helper — a dedicated
  `SendRequestInitialLoad` checks for a 409 first and reconstructs `WorkQueueCollisionException` locally
  (same type, same message) before falling through to the ordinary `EnsureSuccessStatusCode` path for
  everything else, so `RunExecutor`'s own `catch (WorkQueueCollisionException)` (added below) matches the
  same way whether the owner is local or remote.

**`src/DbDataSync.TaskRunner/RunExecutor.cs`** — a new catch clause for `WorkQueueCollisionException`,
recording `RunFailureKinds.ConcurrentLoadInProgress` (`src/DbDataSync.State/Models.cs`) — resolving the
phase's own open question: yes, worth a dedicated `FailureKind`, so the run reads as "will retry
automatically" rather than an undifferentiated failure, matching `PositionExpiredException`'s own
treatment even though (unlike that one) there's no operator action to offer.

**Net effect**: because `SetPendingLoad` never runs on the losing side, `ReadHold` never leaves whatever
it already was — the strand phase 134 could produce becomes structurally impossible, not merely rarer.
The mapping's next scheduled Primary pass tries the whole capture-and-request sequence again on its own;
if the winning reload has finished by then it succeeds, if not it fails the same way and retries again
next tick. No operator recovery endpoint needed.

## A test fixture bug this phase's own reproduction exposed — and it was the row-count flake all along

`BulkLoadIntegrationTests.PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`'s own doc comment states
its subject plainly: "a replication's ordinary scheduled sync and an on-demand reload are separate units
of work with separate locks, so triggering both at once is normal operation, not contention." That is a
real, still-true claim about phase 8's original Primary/BulkLoad separation — an *already-bootstrapped*
mapping's routine incremental pass and an operator's on-demand full reload genuinely don't collide, since
they're not competing for the same `WorkQueue` row at all.

But the test never gave map-1 (or map-2) a prior pass before firing both triggers — it raced them
against a brand-new mapping's very first pass. Before phase 134 that was harmless: a fresh mapping's
first Primary pass read the whole table directly through its own reader, with nothing in common with
whatever the explicit reload was doing. Phase 134 changed that retroactively, without this test being
revisited: a fresh mapping's first Primary pass *now* also auto-requests a Bulk Load for the identical
segment the explicit trigger asks for — so, unchanged, this test was quietly exercising exactly the race
this phase is about, not the benign coexistence scenario its own doc comment describes. It kept
"passing" (mostly) only because of the bug this phase fixes: a losing collision was silently swallowed,
so both sides got to report success regardless of whether real work happened underneath.

**Fixed both ways, as planned:**

1. **The fixture now tests what it actually claims to test.** Both mappings get a real, warmed-up prior
   pass (`WaitForLoadToCompleteAsync`, the same shared helper phase 141 added) before the test's real
   subject — a genuinely-incremental Primary pass racing an on-demand reload against an
   already-bootstrapped mapping, a true non-collision, both sides asserted to succeed as before.
2. **A new, separate test**,
   `ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals`, covers the
   race this phase actually fixes: a brand-new mapping (what the original test accidentally became), an
   explicit reload racing the mapping's own auto-triggered initial load for the identical segment. Asserts
   the losing side (confirmed, consistently, to be the auto-trigger — the explicit reload's own HTTP
   request enqueues before a worker has even claimed the Primary pass that would race it) shows up
   `RunStatus.Failed` with `FailureKind = "ConcurrentLoadInProgress"`, and that a subsequent pass for that
   mapping succeeds cleanly (the self-healing claim) — with one real wrinkle worth recording: the retry
   step originally re-triggered *both* mappings, and map-2's own unrelated Bulk Load was sometimes still
   genuinely in flight at that point, hitting the separate, pre-existing bug below. Fixed by waiting for
   map-2's own load to finish first, keeping this test's own retry focused on map-1 alone.

**The still-unexplained row-count flake phase 141 left open turned out not to be independent at all** —
it was exactly this fixture bug. Confirmed, not merely suspected: once both changes above landed, the
same file ran clean 8 times back to back (64 individual test executions, 0 failures) locally, where
before it failed roughly 1 run in 3. The full `Category=Integration` suite (`TaskRunner.Tests` 32/32,
`Api.Tests` 70/70, `Drivers.MsSql.Tests` 123/123) is fully green — `dotnet-integration`'s ~46-failure wave
that started phase 141 has no known failures left at all.

## What was found but not fixed

**A manual "Run Now" against a mapping that's still `Loading` can crash with an unhelpful exception**,
found writing the new race test above (its retry step hit this before the fix described there). Not this
phase's bug: `SchedulerService.FilterHeld` deliberately does *not* apply to a manual trigger (an
operator's explicit "letting it through is one more way to notice a mapping is still held" — phase 101's
own design). The actual gap: once a mapping's `ChangeWatermarks` row exists at all (created by
`SetPendingLoad`'s own upsert), its `ReadIntent` column takes the schema's `NOT NULL DEFAULT 'Changes'`
rather than staying unresolved — so a manual re-trigger while still `Loading` resolves to `Changes` intent
against a watermark that isn't live yet, and a reader whose `Changes` branch assumes a non-null value
(`MsSqlChangeTrackingReader` does; confirmed by direct reproduction) throws `ArgumentNullException`
instead of a recognizable, named failure. Written up, not fixed, in
`architecture/planning/todo/manual-trigger-against-a-loading-mapping-crashes-instead-of-refusing.md` — a
real, separate, pre-existing gap unrelated to the race this phase fixes.

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
- A recovery endpoint for a mapping stuck in `ReadHold.Loading` for some *other* reason (a crashed
  worker mid-batch, say) — this phase makes the specific strand it describes impossible, it doesn't add
  general-purpose recovery tooling for the hold.

## How it was verified

- `WorkQueueStoreTests`: `EnqueueOrThrow_WhileAnEquivalentItemIsAlreadyPending_ThrowsInstead_NoNewRow`
  and `EnqueueOrThrow_WithNothingAlreadyInFlight_EnqueuesNormally` — the throwing variant throws on
  collision with the right fields and creates no row of its own, and works normally otherwise.
- `LocalRunnerStateInitialLoadTests`: `RequestInitialLoad_WhenNothingElseIsInFlight_SetsThePendingLoad`
  and `RequestInitialLoad_WhenAnotherLoadForTheSameMappingIsAlreadyInFlight_ThrowsAndLeavesTheWinnerAlone`
  — the second call throws, and `ReadHold` stays exactly what the winner left it (never disturbed by the
  loser).
- The real proof, end to end through a real spawned `DbDataSync.TaskRunner` process (so the HTTP-boundary
  fix above is exercised for real, not just in-process): `BulkLoadIntegrationTests`' own
  `PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed` (fixture fixed) and the new
  `ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals` — 8 back-to-
  back full local runs of the file, 64/64 individual test executions, 0 failures (previously ~1/3 runs
  failed). Full `Category=Integration`: `TaskRunner.Tests` 32/32, `Api.Tests` 70/70,
  `Drivers.MsSql.Tests` 123/123. Full non-integration suite (`Category!=Integration`) also green
  (1573 passed, 27 skipped — the expected Windows-only tests, skipped on this Linux dev box).

## Decisions made

- Dedicated `FailureKind` (`ConcurrentLoadInProgress`) added — see "What this phase built" above.
- `EnqueueOrThrow` as a sibling method on `WorkQueueStore`, not a parameter on `Enqueue` — no risk of an
  existing call site's behaviour silently changing, at the cost of one more public method.
