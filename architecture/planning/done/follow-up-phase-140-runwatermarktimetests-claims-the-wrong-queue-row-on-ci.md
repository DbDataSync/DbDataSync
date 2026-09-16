# `RunWatermarkTimeTests` intermittently claims a queue row it did not enqueue

**Status: fixed 2026-09-15.** See "Confirmed, and fixed" at the end — the mechanism below turned out to
be exactly right, reproduced directly rather than by further reasoning. Found while reviewing PR #3's
CI (phase 144), where it turned `dotnet-windows` red on a commit that changed no .NET source at all.

## The failure

`DbDataSync.Api.Tests.RunWatermarkTimeTests.EveryRunOnThePageIsDatedFromOneReadOfTheGroupsHistory`,
`dotnet-windows`, run 35025393950 (commit `5679f33`):

```
Assert.Equal() Failure: Values differ
Expected: 63ee180f-06dc-435e-b4c4-ea65f1d6298c
Actual:   a7aafaf6-782e-4506-be31-139a20b731bb
   at RunWatermarkTimeTests.CompleteRun(...) in RunWatermarkTimeTests.cs:line 361
   at RunWatermarkTimeTests.EveryRunOnThePageIsDatedFromOneReadOfTheGroupsHistory() ... line 171
```

One failure in 457; everything else in that job passed. Line 361 is the helper's own sanity check:

```csharp
var runId = queue.Enqueue(taskName, RunKind.Primary, mappingName);
var item = queue.TryClaimNext(taskName, workerId: "watermark-time-tests");
Assert.NotNull(item);
Assert.Equal(runId, item.RunId);   // ← here
```

So `TryClaimNext` returned a **different, pre-existing** pending row for that task rather than the one
just enqueued. `WorkQueueStore.TryClaimNext` orders by `Priority DESC, EnqueuedAtUtc ASC` and is **not
filtered by mapping or run kind**, so any older pending row for the same replication wins.

## What was ruled out

Both by running it, not by reasoning about it.

- **Not the `SchedulerService`.** The obvious suspect: the fixture builds a `ScheduleMode.Continuous`
  replication with *two* mappings (`orders`, `audit`) inside a `TestApiFactory` host that runs every
  real hosted service, and the scheduler ticks every 5s. A probe that stood up exactly that fixture
  shape, waited 8s (> one tick), then dumped `WorkQueue` directly found the table **empty**, and the
  subsequent `Enqueue`/`TryClaimNext` pair matched. The scheduler's continuous path needs to consult the
  real source through `ChangePollingGate` first, and neither this sandbox nor the `dotnet-windows` job
  has a reachable SQL Server, so it never gets as far as enqueuing.
- **Not reproducible under ordinary load.** The full `Api.Tests` suite (`Category!=Integration`) run
  three times: **457/457 every time**. The class alone, six times: 8/8 every time.

## The leading mechanism, unconfirmed

`RunMonitorService.ExecuteAsync` calls `supervisor.ReconcileOrphanedRuns()` **once, at host startup, on
a background thread** — and the `TestApiFactory` class fixture starts its host lazily, inside the first
test that touches it. So that reconciliation runs *concurrently with the first test's own writes*.

What it does (`ProcessSupervisor`):

```csharp
foreach (var run in taskRunStore.GetRunningRuns())
    if (!(run.Pid is int pid && IsProcessAlive(pid)))
    { taskRunStore.CompleteRun(run.RunId, RunStatus.Failed, ...); runLockStore.Release(...); }

foreach (var taskName in workQueueStore.GetTasksWithInFlightWork())
    ReconcileDeadWorker(taskName);      // → workQueueStore.ReleaseClaimsForTask(taskName)
```

`ReleaseClaimsForTask` puts **every claimed row for that task back to Pending**. The helper begins its
runs with `pid: 4242` — a PID that does not exist, so `IsProcessAlive` is false and the run looks
orphaned by construction. (Most other tests pass `pid: null`, which fails the same check even more
directly.) A release landing in the window between the helper's `TryClaimNext` and a later call's
`Enqueue` leaves an older Pending row that the next `TryClaimNext` claims in preference to the new one —
which is exactly the observed shape, an *older* id where a just-created one was expected.

**Not confirmed**, and worth saying why rather than shipping it: the exact interleaving that produces
this specific mismatch has not been demonstrated, only argued, and the helper's own `MarkDone` closes
its row synchronously in the ordinary path. A fix was deliberately **not** applied on this basis — the
one-line change (use a live PID so the reconciler leaves the run alone) is plausible and harmless, but
"plausible and harmless, verified by reasoning" is precisely the pattern phase 140 exists as a warning
about, and nothing here can currently tell a real fix from a coincidence.

## What would actually settle it

- **Make it reproducible first.** Call `ProcessSupervisor.ReconcileOrphanedRuns()` directly, from a
  test, in the middle of the helper's sequence — if that turns the assertion red deterministically, the
  mechanism is confirmed and the fix follows immediately.
- Then decide between: a live PID in the helper; having the helper claim until it finds *its own* run
  id; or making `TryClaimNext`'s contract explicit about the fact that it claims per task, not per
  mapping, which is the sharp edge the helper walked into.
- Worth checking whether any other test that both begins runs with a dead PID and asserts on queue
  identity is exposed to the same thing.

## Why it matters beyond one flake

It is not a Windows bug — nothing in the mechanism is platform-specific — but it surfaced on
`dotnet-windows`, which phase 140 had just brought to green, on a commit that touched no .NET code. A
rare red in a job that was freshly trusted is disproportionately expensive: the next person to see it
has to re-derive whether phase 140's work regressed before they can conclude it did not.

## Confirmed, and fixed

Did exactly what "What would actually settle it" above prescribed — called
`ProcessSupervisor.ReconcileOrphanedRuns()` directly from a scratch test, mid-sequence, rather than
reasoning further. It turned the assertion red immediately and deterministically: a run begun with
`pid: 4242` had its `WorkQueue` claim released back to `Pending` by `ReconcileOrphanedRuns()` while still
genuinely in flight (between `BeginRun` and `MarkDone`) — a second `TryClaimNext` from a different worker
id successfully reclaimed the exact same `RunId`. Swapping in `Environment.ProcessId` (a pid guaranteed
alive for the test's own duration) in the same scratch test left the claim completely undisturbed after
the identical `ReconcileOrphanedRuns()` call. Mechanism confirmed, not merely plausible.

The precise path from "a claim got released" to "a *different* run's id gets claimed instead" needed one
more piece this doc's own draft hadn't stated precisely: it is not the *same* mapping's own next enqueue
that collides (that would hit `WorkQueueStore`'s own partial-unique-index and just return the reopened
row's id again — consistent, not a mismatch). It is a **different mapping under the same task** —
`RunWatermarkTimeTests`' own fixture has two ("orders", "audit") — whose fresh `Enqueue` succeeds cleanly
(different key, no index collision) while the stale reopened row from the *other* mapping is still
sitting there `Pending`. `TryClaimNext` claims by task alone, ordered `EnqueuedAtUtc ASC`, so the older,
stale row wins over the genuinely fresh one — the exact "wrong queue row" shape from the CI log.

**Fix**: `CompleteRun`'s own `BeginRun` call now passes `Environment.ProcessId` instead of `4242` — one
line, exactly the candidate the earlier draft declined to ship on reasoning alone. A permanent regression
test, `AClaimedRun_SurvivesReconciliation_SoALaterEnqueueForADifferentMappingIsNotStolen`, keeps the
scratch reproduction's own shape (enqueue+claim "orders", `BeginRun` with a live pid, call
`ReconcileOrphanedRuns()` mid-flight, then enqueue+claim "audit" and assert it gets its own fresh row) —
so the mechanism stays under a test that would go red again if this regressed, rather than living only in
this doc's prose.

**Not swept wider.** The same `pid: 4242`/`pid: null` convention appears in several other test files
(`RunsControllerTests.cs`, `NotificationsEndpointTests.cs`, `RunnerStateEndpointTests.cs`, and a few in
`DbDataSync.State.Tests`) — but the State.Tests ones never start a real host, so `RunMonitorService` and
its `ReconcileOrphanedRuns()` never run there at all, and none of the others reproduced were checked for
the specific "multiple mappings under one task, claim survives past a reconcile boundary" shape this fix
addresses. Worth a pass if another flake in this family turns up, not assumed fixed by this one.

Verified: `RunWatermarkTimeTests` 9/9 three times in a row; the whole `DbDataSync.Api.Tests`
non-integration suite 435/435 (458 total, 23 Windows-only skips) after a stale, concurrently-built driver
test DLL — unrelated to this change, caused by another session's build landing mid-scan — was ruled out
by rebuilding and rerunning clean.
