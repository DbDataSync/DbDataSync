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

**An earlier draft of this doc proposed the losing caller "ride along" on the winning batch — reported
back which batch actually won, and pending-load bookkeeping redirected to it.** Ruled out on discussion:
two independent triggers (an operator's manual reload, a mapping's own auto-triggered initial load)
racing for the same segment are not one action that happens to arrive in two pieces — they're two
separate requests, and merging them silently means the loser's *caller* (the Primary pass that captured
a position and asked for this) never finds out its own request didn't happen the way it thinks it did.
Silently attaching its watermark-promotion bookkeeping to someone else's unrelated, differently-scoped
reload is worse than just saying no.

**The direction now agreed: reorder so the enqueue attempt happens first, and treat losing the race as an
ordinary, expected failure of *this* request — not something to route around.**

```csharp
public void RequestInitialLoad(
    string taskName, string mappingName, string sourceTable,
    string capturedPosition, DateTimeOffset? capturedPositionTimeUtc)
{
    var batchId = Guid.NewGuid().ToString("N");

    // Throws if this loses the WorkQueue race — see below. Only on success does this mapping's
    // pending-load state get written at all.
    initialLoadEnqueuer.Value.EnqueueForInitialLoadAsync(taskName, mappingName, batchId, CancellationToken.None)
        .GetAwaiter().GetResult();

    watermarks.SetPendingLoad(taskName, mappingName, sourceTable, capturedPosition, capturedPositionTimeUtc, batchId);
}
```

Mechanically this needs `WorkQueueStore.Enqueue`'s own `if (!inserted)` branch (currently: "return the
existing item's RunId instead") to have a variant that throws instead, used specifically by the
auto-triggered-initial-load call path — every *other* caller (`ReconcileService`, `SchedulerService`, the
operator-facing `POST .../bulk-load` endpoint) keeps today's silent-collapse behaviour, which is correct
for *them* (two identical requests from the same kind of source really should collapse — see the
adjacent, passing `TwoIdenticalBulkLoadTriggers_CollapseIntoOneRun`).

This needs no change to `IInitialLoadEnqueuer`'s own contract (still `Task`, not `Task<...>`) — the
thrown exception already has a route to the surface with no new plumbing: `RunExecutor.RunMappingAsync`
doesn't catch around this call (`IRunnerState.RequestInitialLoad`'s own doc already calls it a
"Prerequisite" call for exactly this reason — its failure is deliberately not swallowed), so it
propagates to `ProcessWorkItemAsync`'s existing generic catch and lands as an ordinary `RunStatus.Failed`
row with a clear `errorSummary` ("a Bulk Load is already in progress for mapping 'map-1', segment
'full'" or similar) — the same path `PositionExpiredException`/`MetadataNotCachedException` already use,
possibly with its own `FailureKind` if a distinct, more helpful message ends up worth it (see open
questions).

Because `SetPendingLoad` never runs for the losing attempt, `ReadHold` never leaves `None` for it — the
strand this doc is about becomes structurally impossible, not merely harder to hit. The mapping's next
scheduled Primary pass (ordinary `SchedulerService` cadence, `FrequencySeconds`) simply tries the whole
capture-and-request sequence again; if the winning reload has finished by then it succeeds cleanly, if
not it fails again the same way and tries again next tick. No operator recovery endpoint needed — the
system self-heals on its own schedule.

**Still open: the multi-segment case.** An auto-triggered initial load enqueues one segment per
`mapping.DefaultSegmenting` entry (empty meaning a single `FullSegment`) inside one batch
(`BulkLoadBatchStore.CreateBatch`, `SegmentCount = segments.Count`). For a mapping with more than one
configured segment, it's possible for *some* segments to win their own `WorkQueue` race and others to
lose theirs — a partial result, not the clean single-request win/lose this doc otherwise describes. The
simplest resolution — treat *any* segment losing as the whole request losing, and throw — still leaves
whichever segments *did* win already real, enqueued, in-flight work belonging to a batch this attempt is
about to declare a loss on; that batch's own `SegmentCount` will then never be satisfied by its own
completions (a smaller, more contained version of today's bug — a batch that never reaches
`BulkLoadState.Completed` — rather than a new one). A fully clean fix needs the batch-create-plus-enqueue
sequence to be one atomic operation (currently two separate store calls, each its own transaction), which
is a real but separable piece of work. Worth noting that this multi-segment collision needs the *same*
rare "an operator manually reloads exactly the same custom segment scheme a mapping's own
`DefaultSegmenting` already uses, in the same narrow window" circumstance the single-segment case needs —
narrower still, and probably fine to land the common case first and treat this as a known, smaller
follow-on rather than a blocker.

## Open questions

- Is a dedicated exception type / `FailureKind` (matching `PositionExpiredException`'s "known cause, known
  fix" treatment) worth adding here, so the run's `errorSummary` reads as "will retry automatically, no
  action needed" rather than an undifferentiated failure? Given the fix above makes this fully
  self-healing, the UI/operator-facing framing matters more than the mechanism.
- How often does this actually matter in practice? A mapping only reaches this race in the narrow window
  between its creation and its first successful pass, *and* only if an operator (or automation) reloads
  it in that same window — worth knowing whether repeated collisions (a very short `FrequencySeconds`
  racing a slow reload) would produce a noisy run history worth suppressing/collapsing in the UI, versus
  being rare enough not to matter.
- The multi-segment atomicity gap above — worth its own follow-up once the common (single-segment) case
  is fixed, or worth solving in the same pass since the underlying "one atomic batch-create-and-enqueue"
  primitive would fix both at once?
