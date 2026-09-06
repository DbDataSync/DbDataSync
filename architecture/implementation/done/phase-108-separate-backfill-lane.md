# Phase 108 — the worker drains change processing and backfills on separate lanes

**Status**: Done.
**Plan reference**: a plan-mode session, transcribed below. Stacks on phase 106
(`change-processing-parallelism` — the degree-of-parallelism config and the worker-spawn wiring it
evolves). Numbered 108 to sit above phase 107 (backfill batches), which is an independent branch;
this feature only routes `RunKind.Backfill` to its lane, which holds with or without batches.

## Why

Every unit of work for a replication — an incremental `Primary` pass, a `Backfill` segment, a
`Verification` — was a row in one `WorkQueue`, claimed by one `DbDataSync.TaskRunner` process,
drained by one bounded `Channel<WorkItem>` with `degreeOfParallelism` consumers, on one claim
ordering (`WorkQueueStore.TryClaimNext`: `WHERE TaskName = $task AND Status = 'Pending'`, no
`RunKind` filter). A claimed backfill segment holds a consumer slot for its whole run — minutes to
hours on a large table — so a big reload directly starved incremental sync of the slots it needs
(`architecture/planning/todo/change-queue-fairness-investigation.md`).

Goal: two independent lanes inside the one worker process —

- **change-processing lane** — `RunKind.Primary`, sized by `changeProcessing.degreeOfParallelism`
- **backfill lane** — `RunKind.Backfill` + `RunKind.Verification`, sized by
  `changeProcessing.backfillDegreeOfParallelism`

so a saturated backfill lane can never take a slot the change-processing lane needs, and each lane's
concurrency is an operator-visible control.

Decisions: **one process / two channel+consumer pools** (not two OS processes — that would re-key
`ProcessSupervisor` and the restart-reconcile path by lane for far more code and a new correctness
hazard); **Verification rides the backfill lane** (both are on-demand, non-incremental, whole-table
work).

## What this phase built

### Lane model — `src/DbDataSync.State/Models.cs`
`enum RunLane { ChangeProcessing, Backfill }` and `RunLanes.KindsFor` / `LaneFor` — the one place
the `RunKind` → lane mapping is stated, so the claim query and the worker cannot disagree. No
persisted `Lane` column: a lane is derivable from `RunKind`, and the one-process model keeps
reconciliation whole-process.

### Claim path takes a lane — `src/DbDataSync.State/WorkQueueStore.cs` + the runner seam
`TryClaimNext` and `HasOutstandingWork` gain an optional `RunLane? lane` — non-null adds
`AND RunKind IN (…)` (from `RunLanes.KindsFor`, enum names, never user input); null is "anything at
all", which is what reconciliation and lane-agnostic tests want. Threaded through `IRunnerState`
(non-nullable there — the worker always knows its lane), `LocalRunnerState`, `RemoteRunnerState`,
`StateProtocol.TryClaimNextRequest` (now carries `RunLane Lane`), and `RunnerStateEndpoints`
(`has-outstanding-work?…&lane=`). `UX_WorkQueue_InFlight` and `RunLocks` were already
`(TaskName, RunKind, MappingName, …)`-scoped, so no schema change and Primary/Backfill of the same
mapping never contend.

### Two lanes in the worker — `src/DbDataSync.TaskRunner/RunExecutor.cs`
`ExecuteWorkerAsync(taskName, WorkerLanes lanes, ct)` (`WorkerLanes` = `record(int ChangeProcessing,
int Backfill)`, `.Uniform(n)` for tests). It runs two `RunLaneAsync` loops — each its own bounded
channel, one `ProduceAsync` claiming only that lane, N `ConsumeAsync` — and `await Task.WhenAll`,
capturing the first exception and rethrowing after `state.Flush()`.

`ProduceAsync` gained a `RunLane`:
- **change-processing lane** — today's logic verbatim (continuous idle-timeout via
  `_lastProductiveTicks`, or periodic `EmptyPollsBeforeExit`). It owns the process lifetime; its
  producer's `finally` cancels a linked `winddown` CTS.
- **backfill lane** — under a periodic replication it drains and exits the same way. Under a
  continuous one it *rides the process*: an empty backfill queue is normal and the process is up for
  the change lane anyway, so it keeps polling until `winddown` fires, then does one last drain. That
  is what keeps a backfill enqueued mid-life from sitting unclaimed until the worker respawns — an
  intra-process version of phase 008's "worker exits as work arrives" race, closed here rather than
  reintroduced.

`MarkProductive` (which resets the idle clock) now fires only for `RunKind.Primary` — a backfill or
verification reading rows says nothing about whether incremental changes are still arriving, and a
continuous worker held up purely by backfills is exactly the coupling this phase removes.

### Config — `src/DbDataSync.Core/Config/ChangeProcessingConfig.cs`
`DegreeOfParallelism` (phase 106, `[DefaultValue(4)]`) is now the change-processing lane;
`BackfillDegreeOfParallelism` (`[DefaultValue(4)]`) is the backfill lane. Same per-lane default keeps
"no config" simple to explain. `ConfigValidation.ValidateChangeProcessing` rejects either `< 1`.

### Wiring
- `TaskRunnerOptions` — `--backfill-parallelism` (validate `>= 1`); `options.Lanes` builds the
  `WorkerLanes`. `Program.cs` passes it.
- `ProcessSupervisor.BuildStartInfo` — takes `changeParallelism` + `backfillParallelism`, emits both
  `--degree-of-parallelism` and `--backfill-parallelism`. `ResolveParallelism` reads both from config
  with the same missing-file fallback.

### UI — `src/DbDataSync.Web`
`ScheduleCard` gains a second number input beside phase 106's ("up to N mapping(s), and M
backfill(s), at once"). `types.ts` + the new-replication default carry `backfillDegreeOfParallelism`.

## How it was verified

- `dotnet build DbDataSync.slnx` clean; `npm run build` + `run lint` clean.
- **New tests**:
  - `WorkQueueStoreTests` — `TryClaimNext(lane)` returns only that lane's kinds; a `ChangeProcessing`
    claim is not blocked by a pinned Running `Backfill`; `HasOutstandingWork` is per-lane.
  - `RunExecutorTests.ExecuteWorkerAsync_DrainsBothLanes` — one invocation drives a queued Primary
    and a queued Backfill to terminal. `…_ABusyBackfillLane_DoesNotBlockChangeProcessing` — a
    pinned-Running backfill segment does not stop the Primary pass finishing.
  - `TaskRunnerOptionsTests` — `--backfill-parallelism` parse / default / `< 1` rejected;
    `options.Lanes`.
  - `ChangeProcessingParallelismTests` (phase 106) — the backfill lane round-trips independently and
    `< 1` is refused.
  - `StateOwnershipTests` (phase 106) — `BuildStartInfo` emits `--backfill-parallelism`.
  - `golden-path.spec.ts` test 04 — sets and reads back both lane sizes.
- **Mechanical**: ~30 `ExecuteWorkerAsync(taskName, degreeOfParallelism: N, ct)` call sites →
  `WorkerLanes.Uniform(N)`; ~6 `IRunnerState` seam call sites in tests → explicit
  `RunLane.ChangeProcessing`.
- Full suites: State, TaskRunner, Api, Core all green. Manual container check: a Continuous
  replication with change parallelism 2 / backfill parallelism 1 — the `Primary` passes start and
  finish while auto-segmented backfill segments are still `Running`, no more than one backfill
  segment `Running` at a time, both `--degree-of-parallelism 2` and `--backfill-parallelism 1` on the
  spawned runner's command line, and the worker still exits after its idle timeout (both lanes wind
  down together).

## Decisions

- **One process.** Two OS processes would give crash/resource isolation but re-key `_workers`,
  `DescribeStatus`, `CancelRun`, `ReconcileOrphanedRuns` and the journal-dir lifecycle by
  `(taskName, lane)`, and introduce the hazard that killing one lane must not release the other's
  claims. The stated goal — lane separation so backfills can't starve change processing — needs none
  of that.
- **Nullable `lane` on `WorkQueueStore`, non-nullable on `IRunnerState`.** The store has honest
  lane-agnostic callers (reconciliation asks "anything outstanding?"); the runner boundary never
  does.
- **`BackfillDegreeOfParallelism` under `changeProcessing`, default 4.** Backfill is the reload half
  of change processing, and a matching default makes the migration from phase 106's single number a
  non-event to explain.

## Out of scope

- Two OS processes per replication (separate crash/resource domains).
- `Priority` biasing within a lane.
- Bounding one backfill segment's size (`change-queue-fairness-investigation.md`'s other half).
