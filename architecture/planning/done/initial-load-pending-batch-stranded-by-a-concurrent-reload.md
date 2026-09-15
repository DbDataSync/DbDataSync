# A losing auto-triggered initial load strands `ReadHold.Loading` forever

**Status: resolved 2026-09-15 — see Outcome at the end.** Found while chasing an intermittent failure
in `BulkLoadIntegrationTests.PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed` during phase 141
(`architecture/implementation/done/phase-141-dotnet-integration-remaining-failures.md`). Not fixed
there — this is a real production bug in phase 134's `RequestInitialLoad`/`ReadHold.Loading` machinery
(`architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md`,
`architecture/implementation/done/phase-134-initial-load-becomes-a-bulk-load.md`), not a test problem,
so it was written up here rather than as a phase-141 test fix.

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

## Outcome

Discussed two candidate fixes. **Rejected**: the losing caller "rides along" on the winning batch
(reports back which batch actually won, redirects pending-load bookkeeping to it) — two independent
triggers racing for the same segment are two separate requests, not one that happens to arrive in two
pieces, and merging them silently means the loser's own caller never learns its request didn't happen
the way it thinks it did. **Agreed**: reorder `RequestInitialLoad` so the enqueue attempt happens
before `SetPendingLoad`, and have the losing side throw — an ordinary, expected failure of *this*
request, landing as a plain `RunStatus.Failed` row via `RunMappingAsync`'s existing unguarded
"Prerequisite call" handling (no `IInitialLoadEnqueuer` contract change needed). Because
`SetPendingLoad` never runs on the losing side, the strand becomes structurally impossible, and the
mapping's next scheduled pass simply retries on its own — no operator recovery endpoint needed.

Carried forward into `architecture/implementation/todo/phase-143-initial-load-race-loses-cleanly.md`,
which has the full design (including the still-open multi-segment atomicity gap this doc's earlier
draft first raised).
