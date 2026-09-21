# `RunWatermarkTimeTests` claims the wrong queue row again — the same assertion, a new source

**Status: open.** Recorded as a row in
[the CI flake catalogue](follow-up-ci-is-red-on-most-pushes-from-unrelated-flaky-tests.md) (run
`35496100986`, 2026-09-20) and marked "**no** — new"; this is the doc that row is owed. Investigated
2026-09-21.

## The symptom

`dotnet-windows` → `RunWatermarkTimeTests.EveryRunOnThePageIsDatedFromOneReadOfTheGroupsHistory`, failing
inside the test class's own `CompleteRun` helper at `RunWatermarkTimeTests.cs:404`:

```csharp
var runId = queue.Enqueue(taskName, RunKind.Primary, mappingName);
var item = queue.TryClaimNext(taskName, workerId: "watermark-time-tests");
Assert.NotNull(item);
Assert.Equal(runId, item.RunId);   // <-- line 404
```

The claim came back with a different run's id. The commit under test changed packaging and CI files.

## This is a recurrence of a fixed bug, by a route the fix did not cover

`architecture/planning/done/follow-up-phase-140-runwatermarktimetests-claims-the-wrong-queue-row-on-ci.md`
closed this exact assertion before. That failure was: the helper used a made-up dead pid,
`RunMonitorService` (running for real in this host) called `ProcessSupervisor.ReconcileOrphanedRuns()`,
reconcile could not tell a dead pid from a genuinely orphaned run, released the claim back to `Pending`,
and that stale row then outranked the next enqueue. The fix was to use `Environment.ProcessId`, whose
liveness reconcile respects — and the helper carries a long comment explaining it.

**That fix removed one producer of competing `Pending` rows. It did not make the helper's assumption
safe.** The assumption is "the row I claim is the row I just enqueued," and `TryClaimNext` does not
promise that:

```sql
WHERE TaskName = $task AND Status = 'Pending' AND AvailableAtUtc <= $now
  AND NOT EXISTS (... same task+kind+mapping already Claimed/Running ...)
ORDER BY Priority DESC, EnqueuedAtUtc ASC
```

It claims **per task, not per mapping or per run id** — the helper's own comment already says so, in the
course of explaining the *previous* bug. So any other `Pending` row for the same task wins if it sorts
first.

## What produces one now

`SetUpAsync` creates a real replication through the API (`PUT /api/replications/{name}`), and
`TestApiFactory` removes only `SecretStore` from the host — so **`SchedulerService` is running for real**
(`DbDataSyncHost.cs:280`) against a configuration the test just created. Its tick enqueues a Primary pass
per due mapping for that task. That is a second producer of exactly the row shape the phase-140 fix
worked to eliminate, and it is not affected by using a live pid.

Two details make it easy to lose that race rather than merely possible:

- **The ordering has no tie-breaker.** `EnqueuedAtUtc` is bound from one `now` at insert; `DateTime.UtcNow`
  on Windows advances in ~15.6 ms steps, so a scheduler row and the test's row can carry the *identical*
  timestamp and `ORDER BY … EnqueuedAtUtc ASC` then resolves arbitrarily. This is the same clock-granularity
  family as the SCD2 "identical mapped times" failures that `a2a8d66` fixed with a deliberate tick.
- **The window is the gap between `Enqueue` and `TryClaimNext`** — two statements, but a scheduler tick
  landing between them is all it takes.

The scheduler's due-check is schedule-dependent, so *whether* it fires inside that window varies per run
— which is precisely the shape of a test that passes hundreds of times and then does not.

## Fix shape

**Stop the test host from scheduling.** This class exercises how a page of run history is dated; it has no
use for a live `SchedulerService` enqueueing work underneath it. Removing that hosted service from
`TestApiFactory` (or from a factory variant this class uses) removes the producer rather than racing it —
the same move the phase-140 fix made against reconcile, applied one level up.

Two things worth doing alongside, neither sufficient alone:

- **Claim the row the test means**, rather than "whatever is next for this task" — a test-only claim by
  run id would make the helper state its intent instead of inferring it. This is the change that ends the
  recurrence permanently; the two fixes so far have each removed one producer.
- **Give the `ORDER BY` a deterministic tie-breaker** (`, Id ASC`). This does not fix the wrong-row problem
  and must not be mistaken for it, but an arbitrary order among equal timestamps is worth removing on its
  own — it affects production claim order too, not just tests.

## How to verify when closed

- `EveryRunOnThePageIsDatedFromOneReadOfTheGroupsHistory` passes with a `SchedulerService` tick forced
  between the helper's `Enqueue` and `TryClaimNext` — the failure reproduced deliberately, as phase 140 did
  with `ReconcileOrphanedRuns()`, rather than waited for.
- The helper's comment is updated: it currently explains only the reconcile producer, which reads as if
  that were the whole story.
- Nothing in the class depends on the scheduler being live (checked, not assumed, before removing it).
