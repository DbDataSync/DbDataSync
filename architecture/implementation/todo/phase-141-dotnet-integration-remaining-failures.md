# Phase 141 — `dotnet-integration`'s remaining ~46-test failure wave

**Status**: `TaskRunner.Tests` (32/32) and `Api.Tests` (68/69) fixed and verified locally against the
same Docker containers CI uses. One `Api.Tests` failure remains open (a real, intermittent race — see
below). `DbDataSync.Drivers.MsSql.Tests`' 1 failure not yet investigated.
**Plan reference**: none — split out of phase 140 on 2026-09-15. This is the `ubuntu-latest`
`dotnet-integration` job (real SQL Server/Postgres/MySQL service containers), unrelated to anything
Windows-specific — it doesn't belong in phase 140, which is scoped to `dotnet-windows`.

## Why

Phase 134's own "Merged" note flagged that its last `dotnet-integration` run before merge showed 27/32
`TaskRunner.Tests` and 18/69 `Api.Tests` failures, all "Expected: Succeeded, Actual: Failed" / "Expected:
N rows, Actual: 0" shaped, alongside repeated real SQL Server `Login failed for user 'sa' ... Infrastructure
error occurred` messages spanning the whole run — read at the time as consistent with a connectivity
problem in that one run rather than real regressions, with a retrigger in flight to confirm.

Phase 134's own follow-up (2026-09-15, "the retriggered run's real findings") confirmed the retrigger
**was not a flake** — it reproduced, and reading the actual failures (not just assuming) found five real,
now-fixed test regressions plus this much larger remainder: `BulkCreateRunIntegrationTests`,
`ReconcileDeletesIntegrationTests`, `Scd2NaturalKeyIntegrationTests`, generic (non-`InitialLoad`-related)
`RunExecutorIntegrationTests` cases, and `PreviewIntegrationTests` — **none of which touch an affected
reader's `InitialLoad` path at all** — sharing the same "Succeeded → Failed" / "N rows → 0" signature.
That note hypothesized a separate, concurrently-discovered fix (phase 140's own
`DriverConnectionFactory.EnsureLibraryInstalledAsync` concurrent-install race fix, already on `main`)
might explain a good share of it, but said explicitly this was never isolated and confirmed.

**It wasn't confirmed, and checking the actual next run shows it wasn't the (whole) answer**: the most
recent `dotnet-integration` run on `main` — after the race-condition fix, after every phase-134-fixup
commit — still shows **46 failures** (1 `DbDataSync.Drivers.MsSql.Tests`, 18 `Api.Tests`, 27
`TaskRunner.Tests`), the same order of magnitude as before. The failure *character* has shifted, though:
the earlier "Could not load file or assembly 'Microsoft.Data.SqlClient...'" pattern (the exact shape the
race-condition fix targets) is no longer present in the sampled failures — what remains looks like
genuine data/timing mismatches (real assertion failures with specific wrong values, not exceptions), for
example:
- `PreviewIntegrationTests.AVerificationCheck_RunsAgainstBothSides_AndItsResultIsReadableAfterwards` —
  `Expected: 0, Actual: 2`
- `PreviewIntegrationTests.ThePreviewsSourceRead_ReturnsExactlyWhatThePassLoads` — expected three named
  rows, got an empty result
- `PreviewIntegrationTests.AfterAPass_ThePreviewShowsTheIncrementalReadRatherThanTheFullLoad` —
  `Expected: 2, Actual: 1`
- `RunExecutorIntegrationTests.AMappingThatProvisionsItsOwnTarget_SucceedsOnItsFirstPass` — `Expected:
  Succeeded, Actual: Failed`
- `Scd2CdcGuaranteedDeliveryIntegrationTests.APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation`
  — also failing

The SQL Server `Login failed for user 'sa'` messages are still present in the run's container logs, but
only inside the `Stop containers` teardown step's own log dump (which replays the container's entire
lifetime log, including routine health-check probes) — not obviously correlated with any specific test's
failure this time. Whether that message is still causal, or was always background noise from health
checks, is itself unconfirmed.

## What this phase will do

1. **Get a clean read of a full `dotnet-integration` failure set** — all 46, not a sample — with a
   non-interleaved log (`--logger trx`, matching phase 140's own plan for `dotnet-windows`) to see the
   complete, accurate failure shape rather than a handful of examples.
2. **Determine whether these 46 share one root cause or several.** The examples above span
   `PreviewIntegrationTests` (verification/preview), `RunExecutorIntegrationTests` (provisioning),
   `Scd2CdcGuaranteedDeliveryIntegrationTests` (phase 132's own CDC-ordering work) — different enough
   subject areas that one shared infrastructure cause (container resource contention under the combined
   load of `DbDataSync.Drivers.MsSql.Tests` + `Api.Tests` + `TaskRunner.Tests` all hitting the same
   service containers in one job) is at least as plausible as several independent regressions. Distinguish
   before fixing anything.
3. **Determine whether the concurrent-install race fix (phase 140) actually reduced anything**, by
   comparing failure counts/names immediately before and after that specific commit landed, rather than
   the "the character shifted, so it probably helped" impression this doc's own "Why" section is built
   on — get an actual before/after diff of failing test names, not just an aggregate count.
4. **Check container resource limits/timeouts** — this job runs four service containers
   (`mssql-source`, `mssql-target`, `postgres`, `mysql`) on one `ubuntu-latest` runner alongside three
   test projects' full suites; if the newer failures are timing/count mismatches rather than outright
   connection failures, a resource-starvation theory (slow queries returning fewer rows than expected
   inside a fixed wait window, not "no rows at all") is worth checking before assuming logic bugs.
5. **Fix what's real**, the same "reproduce, understand, then fix" discipline phase 134's own follow-up
   round already used for its five regressions — not blind changes based on the failure shape alone.

## Out of scope

- Anything Windows-specific — that's phase 140.
- Phase 134's own two follow-up items (Bulk Load reader-override regression,
  `EnqueueForInitialLoadAsync` failure-mode hardening) — unrelated to CI infrastructure, tracked in
  phase 134's own retrospective.

## Open questions to resolve during implementation

- Whether this job's four-container, three-test-project shape has always been this failure-prone under
  load and simply went unnoticed before recent sessions started watching CI closely, or whether
  something recent (the 133/134 arc's own volume of new integration coverage, the CI-gated handoff
  convention meaning more runs happen per unit of work) changed the load profile enough to newly expose
  it.

## Progress — 2026-09-15: the container-contention hypothesis was wrong; the real cause, and the fixes

Reproduced the exact same failure counts locally (27/32 `TaskRunner.Tests`, 18/69 `Api.Tests`) against
real, healthy, uncontended Docker containers matching CI's topology — this alone disproves the
container-resource-contention theory this doc's own "Why" section was built on. TRX inspection of the
actual failures found one real, shared root cause behind nearly all of them, unrelated to connectivity or
timing: **phase 134's `IInitialLoadEnqueuer` seam was never wired up with a real implementation outside
production**, and separately, **a batch of tests written before phase 134 assert behaviour that
permanently moved** once a position-capturing reader's first pass stopped reading directly.

### `TaskRunner.Tests` (27 → 0 failures)

`RunExecutorIntegrationTests.cs` and `Scd2NaturalKeyIntegrationTests.cs` both constructed their
`LocalRunnerState` with `NeverCalledInitialLoadEnqueuer` — a stub that throws the instant a mapping's
first pass (Change Tracking / TriggerAudit reader) tries to request a Bulk Load. Added
`RealInitialLoadEnqueuer.cs` — the minimal real core (mint a `BulkLoadBatches` row, enqueue one
`WorkQueue` row per segment, mirroring `BulkLoadService.EnqueueForInitialLoadAsync`) — and wired it into
both files' `InitializeAsync`. That alone fixed 19 of `RunExecutorIntegrationTests`' 25 cases and all but
one of `Scd2NaturalKeyIntegrationTests`'.

The remaining 6 + 1 were testing pre-phase-134 assumptions: that a mapping's first Primary pass itself
reads, writes, and can fail on the target. For a position-capturing reader it no longer does any of that
— it only captures a position and requests a Bulk Load, which runs concurrently on the worker's other
lane. Fixed by, per test: waiting for the load to finish (`ReadHold` clearing — the same thing
`SchedulerService.FilterHeld` already waits for in production, per phase 134's own retrospective) before
a second, genuinely-incremental pass; or asserting against the resulting Bulk Load run
(`TaskRunStore.GetMappingRunHistory`) instead of the trivially-succeeding Primary run. Committed as
`3eb2598`.

### `Api.Tests` (18 → 1 failure)

No stub here — `Api.Tests` goes through the real `BulkLoadService` via DI, so every failure was the
second kind above: an HTTP-driven test that triggers a pass, waits only for *that* Primary run's own row
to go terminal, then immediately reads real target rows (or triggers a further pass) — racing the Bulk
Load that pass requested. This hit far more files than expected because several classes' *shared*
`InitializeAsync` prime through exactly this pattern, so tests with nothing to do with data at all
(`ReconcileDeletes_ForAnUnknownMapping_Is404`, `_WithNoSegments_Is400`) failed as pure collateral damage
of their own setup.

Added a shared helper, `MappingLoadWaiter.cs` (`HttpClient.WaitForLoadToCompleteAsync`), polling the
`.../table-mappings/{mapping}/read-state` endpoint until `Hold != Loading` — the same wait, over HTTP.
Wired into the affected classes' own trigger/poll helpers (`ReconcileDeletesIntegrationTests`,
`ReconcileDeletesScd2IntegrationTests`, `PreviewIntegrationTests`, `CrossInstanceEndToEndTests`,
`DescriptorDriverTests`, `RunLifecycleIntegrationTests`) — one line each, in most cases. A few tests also
asserted `rowsRead`/`rowsWritten` on the Primary run's own JSON directly (`BulkCreateRunIntegrationTests`,
`ConcurrentRunsIntegrationTests`, the two above): fixed by fetching the resulting Bulk Load run's own row
instead once the wait confirms it exists. `PreviewIntegrationTests.Metrics_ReportTheRunThatJustHappened`
needed a real rewrite rather than a wait, since a mapping's first-ever pass can no longer produce the
"Primary run that moved real rows" scenario the test needed — restructured to trigger the initial load,
then a second genuinely-incremental pass, and check metrics against *that*; the bulk-load figure this
test tracks changed from an expected 0 to an expected 1, since a real Bulk Load now genuinely happens
(this is the correct new number — the assertion existed specifically to prove Bulk Loads are tracked
separately from the Primary aggregate, not that none occurred).

**Discovered but not fixed — `WatermarkReader` is not exempt from the Primary-pass diversion.**
`DescriptorDriverTests` uses the `Watermark` reader as an ordinary `ChangeProcessing.Reader`, and it
failed with the same signature. Phase 134's retrospective describes `WatermarkReader` as exempt from
*code deletion* (its `ReadIntent.InitialLoad` branch was kept, for the Bulk-Load-reader-override case) —
but it does implement `IPositionCapturing`, so an ordinary mapping using it as its regular reader is
diverted exactly like Change Tracking/CDC/TriggerAudit. Not a bug — just a gap in that retrospective's own
"which readers does this affect" accounting, worth fixing there if anyone revisits it.

**One test remains open: `BulkLoadIntegrationTests.PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`
— a real, intermittent race, not fixed here.** This test fires an explicit `POST .../bulk-load` for
`map-1` concurrently with a `POST .../runs` trigger — whose own Primary pass for `map-1` *also* requests
an auto-triggered Bulk Load for the identical segment. `WorkQueue.Enqueue`'s `InsertOrIgnore` correctly
collapses the two into one run (proven: `GET .../runs?mappingName=map-1` never shows a duplicate), and in
6 of 9 observed local runs the explicit trigger's own row is the survivor, genuinely reads and writes 9
rows (self-reported `rowsRead`/`rowsWritten` match), and the target has 9 rows moments later. In 3 of 9
runs, that same self-reported "9 read, 9 written, Succeeded" row is followed by a `SELECT COUNT(*)`
reading **0** — a real discrepancy between what the writer reported and what's on disk, not a timing
artifact (confirmed by re-checking after a 2s delay in one failing run: still 0). Investigated but not
resolved:
- The losing (auto-triggered) `RequestInitialLoad` call still runs `ChangeWatermarkStore.SetPendingLoad`
  *before* its own `WorkQueue.Enqueue` attempt loses the race — this unconditionally overwrites
  `ChangeWatermarks.PendingBulkLoadBatchId` with a batch that will never complete (the losing enqueue's
  own `BulkLoadBatches` row is created but no `WorkQueue` row ever carries its id). This permanently
  strands the mapping's `ReadHold` at `Loading` and is a real, confirmed bug — but doesn't explain the
  missing rows, since the *winning* run's own write is what's reported and missing.
- Considered whether the auto-trigger sometimes wins instead (using `task.BulkLoad`'s defaulted,
  unqualified `"BatchReload"`/`"MsSqlMerge"` Kinds rather than the explicit trigger's
  `"MsSqlBatchReload"`/`"MsSqlStagingTable"`/`"MsSqlMergeReconcile"`) — plausible, since `WorkQueue`
  doesn't expose which Kinds a completed run actually used via the API, so this couldn't be confirmed or
  ruled out with the tools used here.
- Not chased further per this session's own "reproduce, understand, then fix — not blind changes"
  standard: a production-code change here would be a guess. Worth a dedicated follow-up with SQL Server
  Profiler/Extended Events on the target database to see literally what the two racing pipelines executed
  and in what order, rather than inferring from HTTP-visible state alone.
