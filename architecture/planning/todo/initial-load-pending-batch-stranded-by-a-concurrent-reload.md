# A losing auto-triggered initial load strands `ReadHold.Loading` forever

**Status: diagnosed and reproduced deterministically, no fix agreed yet.** Found while chasing an
intermittent failure in `BulkLoadIntegrationTests.PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`
during phase 141 (`architecture/implementation/todo/phase-141-dotnet-integration-remaining-failures.md`).
Not fixed there — this is a real production bug in phase 134's `RequestInitialLoad`/`ReadHold.Loading`
machinery (`architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md`,
`architecture/implementation/done/phase-134-initial-load-becomes-a-bulk-load.md`), not a test problem,
so it belongs here rather than as a phase-141 test fix.

## The bug

`LocalRunnerState.RequestInitialLoad` (`src/DbDataSync.State/LocalRunnerState.cs`) does this,
unconditionally, in this order:

```csharp
public void RequestInitialLoad(
    string taskName, string mappingName, string sourceTable,
    string capturedPosition, DateTimeOffset? capturedPositionTimeUtc)
{
    var batchId = Guid.NewGuid().ToString("N");

    watermarks.SetPendingLoad(taskName, mappingName, sourceTable, capturedPosition, capturedPositionTimeUtc, batchId);

    initialLoadEnqueuer.Value.EnqueueForInitialLoadAsync(taskName, mappingName, batchId, CancellationToken.None)
        .GetAwaiter().GetResult();
}
```

`SetPendingLoad` writes `batchId` into `ChangeWatermarks.PendingBulkLoadBatchId` and sets
`ReadHold.Loading` — *before* the enqueue call even runs, let alone before it's known whether the
enqueue actually created new work. `WorkQueueStore.Enqueue`'s own uniqueness constraint
(`UX_WorkQueue_InFlight`, partial over in-flight statuses, keyed on `TaskName, RunKind, MappingName,
SegmentLabel`) silently collapses two concurrent requests for the identical segment into one real
`WorkQueue` row — this is correct and by design (proven by the adjacent, passing
`TwoIdenticalBulkLoadTriggers_CollapseIntoOneRun` test). But `RequestInitialLoad`'s own `batchId` was
already minted and already written to `ChangeWatermarks` *before* that collision is known. If this
call's enqueue loses the race, its own `BulkLoadBatches` row (`CreateBatch`, called inside
`EnqueueForInitialLoadAsync` → `BulkLoadService.CreateBatchAndEnqueueAsync`) is created with
`SegmentCount = 1` but **zero `WorkQueue`/`TaskRuns` rows ever carry that batch id** — the winning row
carries whichever batch *it* was enqueued under instead. That batch can never reach
`BulkLoadState.Completed`, so `ChangeWatermarkStore.PromotePendingLoad` (called from
`LocalRunnerState.CompleteRun`, matched on `PendingBulkLoadBatchId` alone) never fires for this mapping.
`ReadHold` stays `Loading` **forever** — the mapping's `ChangeWatermarks` row now permanently reports
"an initial load is in progress" with nothing that will ever finish it.

## Why this is real, not a test artifact

The scenario this needs is ordinary, not contrived: an operator manually reloads a mapping
(`POST .../bulk-load`) at the same moment that mapping's own first-ever Primary pass is capturing its
position and auto-requesting the identical initial load. Nothing prevents this — a mapping can be both
newly created (still needing its automatic initial load) and manually reloaded by an operator in the
same window, and the whole point of phase 134's design is that these two paths (auto-trigger,
operator-trigger) both funnel into the same `WorkQueue` uniqueness mechanism precisely so they can
coexist safely. They coexist at the `WorkQueue` layer; they do not coexist at the `ChangeWatermarks`
layer, because only one of the two callers (`RequestInitialLoad`) ever touches it, and it does so
optimistically.

Once stuck, the effect on a real mapping: `SchedulerService.FilterHeld` (the *only* place `ReadHold`
is enforced, per phase 134's own retrospective) will never schedule another Primary pass for this
mapping again — its incremental sync is permanently dead, silently, with `ReadHold: Loading` in the API
looking like a load that's merely still running rather than one that will never finish. An operator
would have no obvious recovery path short of directly clearing `ChangeWatermarks` (there's no
"recovery" endpoint for this hold the way there is for `PositionExpired`).

## Evidence

Reproduced by temporarily adding a real wait (`HttpClient.WaitForLoadToCompleteAsync`, polling
`.../table-mappings/{name}/read-state` until `Hold != Loading`) to
`BulkLoadIntegrationTests.PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed` for `map-1` — the
mapping whose explicit `POST .../bulk-load` trigger races its own auto-triggered one for the identical
`FullSegment`. **15 of 15 local runs timed out after 30s**, all with the identical error:

```
System.TimeoutException : 'map-1' on '<replication>' was still Loading after 30s.
```

Separately, direct inspection of the `WorkQueue` row that actually won the race (via a raw query
against `StateDatabase`, resolved from the test host's own DI container) showed the *explicit* trigger's
Kinds (`MsSqlBatchReload`/`MsSqlStagingTable`/`MsSqlMergeReconcile`) winning consistently across 15
separate runs — i.e. the auto-trigger's own `RequestInitialLoad` call reliably loses this race in
practice (it runs later, from inside the Primary pass's own processing, after the explicit HTTP
request's enqueue has already landed), so the strand is not a rare interleaving — it is what happens
essentially every time this scenario occurs at all.

This is very likely a second, independent cause of the *original* flaky symptom this investigation
started from (`Tgt_1` reading back 0 rows moments after a Bulk Load run self-reports 9 read/9 written)
— but that symptom is about the row *count*, not `ReadHold`, and the strand described here doesn't
directly explain missing rows (the *winning* run's own write is what's reported and apparently missing;
its data has nothing to do with the losing run's doomed batch). Both remain only partially explained;
see phase 141's own doc for the row-count side, which this file does not resolve.

## Candidate fix directions

Not agreed — options, roughly ordered by how much they actually fix vs. how much they cost:

1. **Full fix — the losing caller rides along on the winning batch.** Change
   `IInitialLoadEnqueuer.EnqueueForInitialLoadAsync`'s contract so it reports back which batch actually
   ends up owning the enqueued work (which may not be the batch id the caller minted and passed in, if
   every segment collided with pre-existing in-flight work). `RequestInitialLoad` would then call
   `SetPendingLoad` with *that* batch id, not its own, so the mapping's pending-load bookkeeping is tied
   to a batch that really will complete and really will promote it. Correct, but touches a shared
   interface (two implementations: production `BulkLoadService`, and `RealInitialLoadEnqueuer` in
   `tests/DbDataSync.TaskRunner.Tests/`) and needs care around the multi-segment case (a batch whose
   segments partially land under a foreign batch and partially under its own is *also* doomed — its own
   `SegmentCount` will never be satisfied by its own completions alone — so "which batch really owns
   this" has to be resolved per-segment or the whole batch treated as foreign, not by a single top-level
   flag).
2. **Reorder without full redirection.** Enqueue first, and only call `SetPendingLoad` if the caller's
   own batch id ends up owning *all* of its segments; otherwise skip `SetPendingLoad` entirely. Simpler
   — no interface change beyond a boolean/void distinction — but leaves `ReadHold` at whatever it already
   was (`None`, in the scenario this doc describes, since an ordinary operator reload never touches
   `ReadHold` at all) rather than correctly reflecting "a load covering this mapping is genuinely in
   flight, started by someone else." The mapping's own watermark stays unset, so its *next* Primary pass
   captures a position and requests an initial load all over again — survivable (it will eventually
   converge, once no concurrent reload is racing it) but wasteful, and not obviously correct if the
   winning reload's Kinds override differ from what the mapping's own incremental sync expects on
   convergence.
3. **Leave it and add operator recovery instead.** Accept that the strand can happen, and give
   `ReadHold.Loading` the same kind of explicit recovery endpoint `PositionExpired` already has, so an
   operator (or an automated health check) can un-stick a mapping that fell into this state. Doesn't fix
   the root cause, but bounds the damage and is far cheaper than 1 or 2. Could be paired with either.

## Open questions

- How often does this actually matter in practice? A mapping only reaches this exact race in the
  narrow window between its creation and its first successful pass, *and* only if an operator (or
  automation) reloads it in that same window. Worth knowing whether this is a real operational
  footgun or a mostly-theoretical one before investing in option 1.
- Should `ReadHold.Loading` even be settable by two independent, uncoordinated callers (auto-trigger and
  operator-trigger) in the first place, or should an operator's manual reload of a mapping that is
  *already* mid-initial-load be refused/coalesced at the API layer instead of silently racing at the
  `WorkQueue` layer? That would shrink this to "can't happen" rather than "handled correctly when it
  happens."
