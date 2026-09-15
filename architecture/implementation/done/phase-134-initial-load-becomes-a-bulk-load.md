# Phase 134 — an initial load becomes a bulk load

**Status**: Complete
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

- **Driver-level unit tests calling the three affected readers directly with `ReadIntent.InitialLoad` +
  a null watermark, beyond the four "Declaring" proof tests — resolved.** The three files this note
  originally flagged as unaudited (`SourceTransformTests.cs`, `MsSqlPipelineTests.cs`,
  `Scd2CdcGuaranteedDeliveryIntegrationTests.cs`) were all fixed, the first two by this phase's own later
  "Follow-up — 2026-09-15" below, the third by phase 141. Re-running this note's own suggested
  `grep -rn "ReadIntent.InitialLoad" tests/` afterward found no remaining unaudited callers among the
  three affected readers.
- **A reader from the affected three configured as a mapping's Bulk Load reader override now throws
  instead of full-loading** — still open, written up in
  `architecture/planning/todo/bulk-load-reader-override-throws-for-position-capturing-readers.md`.
- **`BulkLoadService.EnqueueForInitialLoadAsync`'s failure mode is not hardened** — still open beyond the
  one cause phase 143 fixed, written up in
  `architecture/planning/todo/request-initial-load-endpoint-failure-mode-not-hardened.md`.

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

## Handoff — 2026-09-14 (round 2, CI fix)

**CI's first run on PR #1 found a real bug** — not one of the "known follow-up" items above, something
this session's own local checks (`dotnet build`, unit tests) could not have caught: the API hung on
startup, never logging a line, which timed out the Playwright `webServer` wait in the E2E job.

**Root cause: a DI cycle this phase's own registration introduced.** `StateHost` (an `IHostedService`,
built eagerly at host startup) depends on `LocalRunnerState`. Round 1's `LocalRunnerState` constructor
depended directly on `IInitialLoadEnqueuer`, registered as `sp.GetRequiredService<BulkLoadService>()`.
`BulkLoadService` depends on `ProcessSupervisor`, and `ProcessSupervisor` depends on `StateHost` directly
(pre-existing, phase-134-unrelated code). So: `StateHost → LocalRunnerState → IInitialLoadEnqueuer
(BulkLoadService) → ProcessSupervisor → StateHost` — closed. Before this phase, `BulkLoadService` (and
therefore `ProcessSupervisor`) was only ever constructed lazily, on an operator's first
`POST .../bulk-load` — long after startup. Round 1 made `LocalRunnerState` (needed eagerly) transitively
require it too, closing the loop.

**Fix: `Lazy<IInitialLoadEnqueuer>` instead of a direct dependency.**
`LocalRunnerState`'s constructor now takes `Lazy<IInitialLoadEnqueuer>` and calls `.Value` only inside
`RequestInitialLoad` — the one place that ever needs it, always well after host startup has finished (a
mapping's first pass claiming `InitialLoad`, at the earliest). Constructing the `Lazy<T>` wrapper itself
does not invoke the factory, so it does not recurse into `BulkLoadService`/`ProcessSupervisor`/`StateHost`
at `LocalRunnerState` construction time — the cycle is broken at exactly the edge this phase added, with
zero change to `ProcessSupervisor`'s or `BulkLoadService`'s own dependency shape. `DbDataSyncHost` now
registers `services.AddSingleton(sp => new Lazy<IInitialLoadEnqueuer>(sp.GetRequiredService<IInitialLoadEnqueuer>))`
alongside the existing `IInitialLoadEnqueuer` registration. Every test fixture that constructs
`LocalRunnerState` directly (6 files) was updated to pass `new Lazy<IInitialLoadEnqueuer>(() => <stub>)`
instead of the stub directly.

**Verified properly this time, per explicit instruction** — not just build/unit tests:
`dotnet build src/DbDataSync.Api` (clean), then actually ran
`dotnet exec src/DbDataSync.Api/bin/Debug/net10.0/DbDataSync.Api.dll --urls http://127.0.0.1:<port>` with
a scratch `DbDataSync:RepoRoot` and confirmed, within ~6 seconds: `Runner state endpoint listening...`,
`Now listening on: ...`, `Application started.` — plus a `curl` against the root path returning `401`
(the fallback auth policy — proof the server is answering requests, not hung). This is the exact failure
mode CI caught and this session did not the first time; re-run before trusting this fix again if the DI
graph changes further.

Also re-ran (no Docker): `DbDataSync.State.Tests` — all passing, including the phase's own new
`LocalRunnerStateInitialLoadTests` (updated for the `Lazy<>` signature change) and
`ChangeWatermarkStoreTests` additions. `DbDataSync.Api`, `DbDataSync.TaskRunner`, and every test project
this round touched (`DbDataSync.TaskRunner.Tests`, `DbDataSync.Api.Tests`) rebuild clean.

Branch was already rebased onto a fresh `main` (phase 133a + an unrelated CI fix) by the orchestrating
session before this round started; pushed from that same commit, nothing further to reconcile.

## Merged — 2026-09-14

PR #1 squash-merged into `main` on explicit user instruction, ahead of a retriggered
`dotnet-integration` CI run's result. That job's most recent completed run showed 27/32 and 18/69
failures in `DbDataSync.TaskRunner.Tests`/`DbDataSync.Api.Tests`, every one with the same
"Expected: Succeeded, Actual: Failed" / "Expected: N rows, Actual: 0" shape and repeated SQL Server
`Login failed for user 'sa' ... Infrastructure error occurred` in the container logs spanning the whole
run — consistent with a broad connectivity problem in that specific run rather than 27 independent
logic regressions, and `dotnet-integration` has an otherwise clean track record on `main` (checked
directly via `gh run list`/`gh api .../jobs` against several pre-session commits). A retrigger (empty
commit) was in flight to confirm one way or the other when the merge instruction arrived. **Whoever
picks this thread up next should check that retriggered run's outcome** before assuming either "it was
just a flake" or "it's fine" — it was never actually confirmed clean.

The two items already named above under "Known follow-up / not done here" are still open post-merge and
were not blocking the merge:
- The Docker-backed driver test files beyond the three directly fixed (`SourceTransformTests.cs`,
  `MsSqlPipelineTests.cs`, `Scd2CdcGuaranteedDeliveryIntegrationTests.cs`, and any other direct
  `ReadIntent.InitialLoad` caller a fresh `grep -rn "ReadIntent.InitialLoad" tests/` turns up) were never
  audited for the same "seeds a baseline via a direct `ReadChangesAsync(..., InitialLoad, ...)` call"
  pattern the three fixed files had. They compile; whether they pass is still unconfirmed.
- `MsSqlChangeTrackingReader`/`MsSqlCdcReader`/`TriggerAuditReader` configured as a mapping's *Bulk Load*
  reader override now throw instead of full-loading (since `RunKind.BulkLoad` always asks for
  `InitialLoad` with a null watermark, and their full-load branch is gone) — a real, if narrow and
  previously-untested, regression. Not fixed here; worth a decision (defensive error message on that
  Kind/RunKind combination, or leave it) in a follow-up.

## Follow-up — 2026-09-15: the retriggered run's real findings, and five fixes

The retrigger from the note above **was not a flake — it reproduced twice**, with growing scope (a third
run pulled in `DbDataSync.Drivers.Postgres.Tests` and `DbDataSync.Drivers.MsSql.Tests` failures the first
hadn't shown). Reading the actual failures rather than assuming either "it's fine" or "it's all
connectivity" turned up **two distinct, real classes of problem**, not one:

**1. Five genuine test regressions, all now fixed and individually verified against real MSSQL/Postgres
containers** (not just `dotnet build`) — exactly the "Docker-backed driver test files... never audited"
gap named above, now closed for the specific files this run's failures actually named:
- `tests/DbDataSync.Drivers.Postgres.Tests/TriggerAuditReaderTests.cs`'s
  `ACompositeKey_CollapsesAndIdentifiesADeleteByEveryPart` and
  `tests/DbDataSync.Drivers.MsSql.Tests/TriggerAuditReaderTests.cs`'s two `Incremental_With*` tests: a
  direct `ReadChangesAsync(..., null, ReadIntent.InitialLoad, ...)` call to seed a baseline, now
  `ArgumentNullException` in `long.Parse` since that branch is gone. Fixed with `CapturePositionAsync`,
  matching the pattern the phase's own `ReadAsync` helpers already used elsewhere in the same files.
- `SourceTransformTests.cs`: `ChangeTrackingReader_AppliesTheTransformOnItsFullLoadPath` **removed** —
  its subject no longer exists; `BatchReloadReader_AppliesTheTransform` already covers the transform on
  an actual initial load's reader. `...OnItsIncrementalPath`'s baseline seed fixed the same way as above.
- `MsSqlPipelineTests.cs`: its shared `RunOnceAsync` helper's null-watermark case switched to
  `ReadIntent.ChangesFromEarliest` — reads from the guaranteed-valid floor (everything Change Tracking
  currently holds) with **no call-site restructuring needed** across its four callers, since
  `ChangesFromEarliest` can never throw `PositionExpiredException` by construction.
- `MsSqlCdcReaderTests.AnInitialLoad_CapturesThePosition_AndDoesNotFullLoad` — **the design's own
  "correctness crux" test, and its bug was real, not environmental**: it captured a position right after
  inserting two rows with no capture-job scan in between (the test fixture stops the real Agent job and
  scans manually), so the captured position was stale and all three rows — not just the one inserted
  *after* capture — replayed on the subsequent read. A production Agent job is always this far ahead by
  the time anything asks; the fixture wasn't, and needed one more `WaitForCaptureAsync` call before
  capturing to match. This is the exact scenario the crux is supposed to prove correct — worth reading
  closely if picking this thread up, not just trusting the "19/19 pass now" result.

**2. The much larger remainder (~50 of the ~56 failures) — `BulkCreateRunIntegrationTests`,
`ReconcileDeletesIntegrationTests`, `Scd2NaturalKeyIntegrationTests`, generic (non-InitialLoad-related)
`RunExecutorIntegrationTests` cases, `PreviewIntegrationTests` — never touch an affected reader's
InitialLoad path at all**, and all shared one signature: "Expected: Succeeded, Actual: Failed" / "N rows
expected, 0 actual", alongside repeated SQL Server `Login failed for user 'sa' ... Infrastructure error
occurred` spanning entire runs. Not chased further in this round — but **while this was in flight, a
separate concurrent session investigating phase 136's new `dotnet-windows` job independently found and
fixed a real, unguarded concurrent-install race in `DriverConnectionFactory.EnsureLibraryInstalledAsync`**
(see phase 140), which plausibly explains a good share of this: many test classes each installing a
driver library concurrently, racing on shared state, presenting as generic connection/login failures under
load. That fix is already on `main` as of this note. **Whoever next gets a clean `dotnet-integration` run
should not assume it's clean because of anything done in this phase** — check whether phase 140's fix (or
something else) is what actually closed the gap, since it was never isolated and confirmed here.

Local verification for all five fixes: `dotnet build DbDataSync.slnx` clean, and each affected test file
run directly against the real local `dbdatasync-mssql-source`/`dbdatasync-postgres` Docker containers
(not the shared CI ones) — `SourceTransformTests`/`TriggerAuditReaderTests`(MsSql): 8/8 pass;
`TriggerAuditReaderTests`(Postgres): 1/1 pass; `MsSqlPipelineTests`: 4/4 pass; `MsSqlCdcReaderTests`
(whole file, not just the fixed test): 19/19 pass. Pushed directly to `main` (small, well-verified,
test-only changes — not routed through a new branch/PR, consistent with the CI-gated convention's own
"a phase small enough to implement, verify locally, and commit within one session does not need any of
this" carve-out).

## Follow-up — 2026-09-15: the "much larger remainder" answer, and two things phase 141 found here

Phase 141 (`architecture/implementation/done/phase-141-dotnet-integration-remaining-failures.md`) finally
answers this doc's own repeated "not confirmed" warnings above: **it was not phase 140's concurrent-install
race fix.** The real cause of the ~50-failure remainder was this phase's own `IInitialLoadEnqueuer` seam
never being wired to a real implementation in the `TaskRunner.Tests`/`Api.Tests` fixtures, plus a batch of
tests (written before this phase, several by this phase's own earlier fixes above) asserting behaviour
that permanently moved once a position-capturing reader's first pass stopped reading directly. All fixed;
`dotnet-integration` is 46 failures down to 1 known, unrelated flake.

Two more things phase 141 found in this phase's own design while chasing that work, worth recording here
since a reader of *this* doc is exactly who'd want to know:

- **`WatermarkReader` is not exempt from the Primary-pass `InitialLoad` diversion** the way point 8 above
  describes — that point is about `RunKind.BulkLoad` reader-override behaviour specifically (code
  deletion), not this. `WatermarkReader` does implement `IPositionCapturing`, so a mapping using it as
  its ordinary `ChangeProcessing.Reader` is diverted to the Bulk Load pipeline on its first pass exactly
  like Change Tracking/CDC/TriggerAudit are. Not a bug — just a gap in this doc's own "which readers does
  this affect" accounting.
- **A losing auto-triggered `RequestInitialLoad` call strands a mapping's `ReadHold` at `Loading`
  forever** — a real production bug in this phase's own design (`SetPendingLoad` runs before the enqueue
  attempt's outcome is known), reproduced deterministically. Diagnosed in
  `architecture/planning/done/initial-load-pending-batch-stranded-by-a-concurrent-reload.md`, designed
  into `architecture/implementation/todo/phase-143-initial-load-race-loses-cleanly.md`.
