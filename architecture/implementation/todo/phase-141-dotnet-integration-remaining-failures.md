# Phase 141 — `dotnet-integration`'s remaining ~46-test failure wave

**Status**: Planned, not started.
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
