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

## Handoff — 2026-09-14

**Status: implemented, fast-checked locally, not yet reviewed by CI.** Branch
`phase-134-initial-load-becomes-a-bulk-load`, cut from `main` (which already has phases 133/133a). This
implements the phase per a corrected spec handed to this session by the orchestrator — several things
the spec above states are stale (flagged inline below); follow the corrections, not this doc's own prose,
where the two disagree.

### What's done

**1. Position capture and routing (the crux), in `RunExecutor.RunMappingAsync`.** For a `Primary` pass
resolving to `ReadIntent.InitialLoad`, once the source connection is open: if the resolved reader
implements `IPositionCapturing`, `CapturePositionAsync` runs immediately (before the target connection
even opens), the captured position is handed to a new `IRunnerState.RequestInitialLoad` call, and the
pass returns `(0, 0, null, null)` — no read, no write, no watermark touched. A reader that does **not**
implement `IPositionCapturing` (the Exempt four) is dispatched exactly as before. `RunKind.BulkLoad`
passes are untouched (they already always ask for `InitialLoad` and never reach this new branch, since
the check is `item.RunKind == RunKind.Primary` explicitly).

**2. `ReadHold.Loading`** — new enum value, `src/DbDataSync.Core/Config/ReadIntent.cs`. No scheduler
change needed — `SchedulerService.FilterHeld` already excludes any non-`None` hold generically, verified
by a new test (`SchedulerServiceHoldTests.ALoadingMapping_IsNotDispatchedOnTheNextTick`).

**3. Schema — a genuine migration** (not phase 133's in-place-edit exception), appended to
`Migrations.cs`'s `Templates`: `ChangeWatermarks.PendingWatermark` / `PendingWatermarkTimeUtc` /
`PendingBulkLoadBatchId` (the last `{{key}}`-typed and indexed, matching `TaskRuns.BulkLoadBatchId`'s own
convention). `ChangeWatermarkStore` gained `SetPendingLoad` (one write: pending fields + `ReadHold.Loading`)
and `PromotePendingLoad(batchId)` (one write, matched by `PendingBulkLoadBatchId` alone — a batch id is
already globally unique, so no replication/mapping context is needed to promote by it). Both are unit
tested directly (`ChangeWatermarkStoreTests`).

**4. The cross-process request — `IRunnerState.RequestInitialLoad`, Prerequisite-style** (per the
corrected spec, not journalled/Outcome-style): `RemoteRunnerState` routes it through `Required` (not
`Outcome`); `RunnerStateEndpoints` maps `POST /request-initial-load`; `LocalRunnerState` implements it by
minting a `batchId`, writing the pending state (fail-closed: before any work exists), then calling the
new `IInitialLoadEnqueuer.EnqueueForInitialLoadAsync`.

**5. `IInitialLoadEnqueuer` — a new interface, not a direct dependency.** `LocalRunnerState` lives in
`DbDataSync.State`, which does **not** reference `DbDataSync.Api` (Api references State, not the other
way — confirmed from both `.csproj` files before writing this). `BulkLoadService` (Api) needed to be
reused for its segment-expansion/enqueue core without creating a project-reference cycle, so:
`IInitialLoadEnqueuer` is declared in `DbDataSync.State` (`src/DbDataSync.State/IInitialLoadEnqueuer.cs`),
`BulkLoadService` implements it, and `DbDataSyncHost` registers it as
`services.AddSingleton<IInitialLoadEnqueuer>(sp => sp.GetRequiredService<BulkLoadService>())` — the same
pattern already used for `IConnectionFactory`/`DriverConnectionFactory`. **This is a deviation from the
literal wording of the corrected spec**, which suggested `LocalRunnerState` could just "pull in
`BulkLoadService` directly" — that does not compile given the actual project-reference graph, and this
interface is the fix.

**6. `BulkLoadService` refactor.** `ExpandAsync` now takes a plain `IReadOnlyList<BatchReloadSegment>` +
optional reader-kind override instead of a `BulkLoadRequest`, and a new `CreateBatchAndEnqueueAsync` core
(batch-id passed in, not minted internally) is shared by the existing HTTP `EnqueueAsync` and the new
`EnqueueForInitialLoadAsync` (the `IInitialLoadEnqueuer` implementation — segments from the mapping's own
`DefaultSegmenting`, empty meaning Full, same convention as `ReconcileService.EnqueueScheduledAsync`).

**7. Completion / promotion.** `TaskRunStore.GetRunKindAndBatch(runId)` is a new narrow lookup (RunKind +
BulkLoadBatchId + MappingName — `GetRun`/`RunColumns` don't carry `BulkLoadBatchId` at all).
`BulkLoadBatchStore.GetBatch(batchId)` is a new single-batch rollup lookup (same query as
`GetRecentBulkLoads`, filtered by `BatchId` alone). `LocalRunnerState.CompleteRun` now checks, after every
completion, whether it was a `RunKind.BulkLoad` run whose batch just reached `BulkLoadState.Completed`,
and if so calls `PromotePendingLoad`. `CompletedWithFailures` does nothing (mapping stays `Loading`; no
resume-a-failed-batch mechanism built, per the phase doc's own "Out of scope"). Covered by
`LocalRunnerStateInitialLoadTests` (4 cases: promotes on last-segment completion, does not promote on
`CompletedWithFailures`, does not promote while segments are still outstanding, an ordinary `Primary`
run's completion never touches an unrelated pending load).

**8. The deletion.** Removed the `if (intent == ReadIntent.InitialLoad)` full-load branch (and the
now-dead `ReadFullLoadAsync` helper it alone used) from `MsSqlChangeTrackingReader`, `MsSqlCdcReader`,
`TriggerAuditReader`. **`WatermarkReader` deliberately got no code deletion** — it never had an explicit
`if (intent == InitialLoad)` branch (its "full load" is just `incremental = intent == ReadIntent.Changes`
evaluating false), and unlike the other three it is a plausible choice for a mapping's *Bulk Load* reader
override, where `RunKind.BulkLoad` always asks for `InitialLoad` with no watermark regardless of
position-capturing — hard-coding it to always be "incremental" would have broken that legitimate,
pre-existing configuration. Only its doc comment was updated to state this. This is a considered
deviation from the corrected spec's literal "delete it from each of the four", made because the literal
instruction doesn't apply cleanly to this one reader — flag if you disagree, it's easy to revisit.

**9. `detailed-design.md` §4.1** rewritten in full: an initial load runs the Bulk Load pipeline; no change
reader full-loads; the ordering/crux, the hold, the one-act promotion, and the two unaffected behaviours
(reload-reader-as-BulkLoad-reader, re-pointing a mapping) are all stated.

**10. Frontend.** `holdState.ts` gained a `'loading'` `HoldState` (label "Loading — initial load in
progress") and `api/types.ts`'s `ReadHold` union gained `'Loading'`. `MappingReadStateDialog.tsx`
deliberately untouched, per the corrected spec (the hold clears on its own; no recovery UI needed).

### Known follow-up / not done here

- **Driver-level unit tests that call the three affected readers directly with `ReadIntent.InitialLoad` +
  a null watermark exist well beyond the four "Declaring" proof tests.** I found and fixed the direct
  owners (`MsSqlChangeTrackingReaderTests`, `MsSqlCdcReaderTests`, `TriggerAuditReaderTests` in both the
  MsSql and Postgres test projects — including their shared `ReadAsync`/`BaselineAsync` helpers, which
  many other tests in those same files reuse for a "seed a baseline position" idiom now served by
  `CapturePositionAsync` instead). **I did *not* audit or fix**: `SourceTransformTests.cs`,
  `MsSqlPipelineTests.cs`, `Scd2CdcGuaranteedDeliveryIntegrationTests.cs`,
  `Scd2CdcGuaranteedDeliveryIntegrationTests.cs`'s own `RunPassAsync(null, ReadIntent.InitialLoad)` call,
  or any other direct caller a broader grep might still find (`grep -rn "ReadIntent.InitialLoad" tests/`
  is the query I used to enumerate the search space — rerun it if picking this back up). These are all
  Docker-backed (`[Trait("Category","Integration")]`), so they are **compile-clean but may fail at test
  run time** — CI will surface exactly which. The fix pattern is established (see the three files above):
  replace a direct `ReadChangesAsync(..., null, ReadIntent.InitialLoad, ...)` call used only to seed a
  starting position with a `CapturePositionAsync` call, and where the test's whole point was asserting
  full-load *rows*, rewrite it as a capture-then-replay ordering test instead.
- **A reader from the affected three (`MsSqlChangeTrackingReader`/`MsSqlCdcReader`/`TriggerAuditReader`)
  configured as a mapping's *Bulk Load* reader override** would now throw instead of full-loading, if
  `RunKind.BulkLoad` ever dispatches it with a null watermark (it always does, per its own contract). This
  is a real, if unusual and previously-untested, configuration regression — not raised by the corrected
  spec, not fixed here. `WatermarkReader` is exempt from this (see point 8 above) but the other three are
  not. Worth a decision (defensive throw with a clear message on that reader/RunKind combination? leave
  it?) in a follow-up round.
- **`BulkLoadService.EnqueueForInitialLoadAsync`'s failure mode is not hardened.** If it throws for a
  reason other than owner-unavailability (e.g. an `AutoSegment` in `DefaultSegmenting` failing to expand
  against an unreachable source), that currently surfaces as an unhandled exception from the
  `/request-initial-load` endpoint, which `RemoteRunnerState.IsUnreachable` would treat as a 5xx —
  meaning the runner could misread a genuine config/segment error as "the owner is gone" and start
  journalling. Not addressed; flagged for whoever picks this up next.

### Local verification

- `dotnet build` on every changed project (`DbDataSync.Core`, `.State`, `.Api`, `.TaskRunner`,
  `.Drivers.MsSql`, `.Drivers.Generic` transitively via `.Api`) — clean, 0 warnings, 0 errors.
- `dotnet build`/`dotnet test` on every test project this phase touches
  (`DbDataSync.Core.Tests`, `DbDataSync.State.Tests`, `DbDataSync.TaskRunner.Tests`,
  `DbDataSync.Api.Tests`, `DbDataSync.Drivers.MsSql.Tests`, `DbDataSync.Drivers.Postgres.Tests`,
  `DbDataSync.Drivers.Generic.Tests`) — all compile clean.
- Actually **run** (no Docker needed): `DbDataSync.Core.Tests` (252 passed), `DbDataSync.State.Tests`
  (245 passed, including the 7 new tests this phase adds), `DbDataSync.TaskRunner.Tests` filtered
  `Category!=Integration` (46 passed, including the pre-existing `InitialLoad`-is-never-refused and
  undeclared-intent-is-refused tests — unaffected, confirming no regression to that guarantee).
- **Deliberately NOT run**: the full Docker-backed `DbDataSync.Api.Tests`, `DbDataSync.Drivers.MsSql.Tests`,
  `DbDataSync.Drivers.Postgres.Tests` suites (real SQL Server/Postgres), and the `Category=Integration`
  cases within `DbDataSync.TaskRunner.Tests` — per this repo's new CI-gated convention. They compile; CI
  is what actually proves the CDC/Change-Tracking/trigger-audit behavior and the new/edited tests in them.

### What's left

- Watch CI on the PR. On red: read the failure output (the "known follow-up" section above names the
  most likely source), fix, update this Handoff, commit, push, repeat.
- On green: merge, move this doc `todo/` → `done/`.
