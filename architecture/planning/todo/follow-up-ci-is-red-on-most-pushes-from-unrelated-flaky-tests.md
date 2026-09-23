# CI is red on most pushes, from several unrelated flaky tests — and a red run blocks promotion

**Observed** 2026-09-20 while working phase 160. Not a defect in any one change: every failure below happened on a
commit that could not have caused it (one was docs-only).

## Why it matters beyond noise

`promote-test.yml` fast-forwards `test` **only on a green CI run**, and `publish-snapshot.yml` (phase 158) publishes
from `test`. So each red run is a missed promotion and a missed snapshot. `promote-test` shows the effect: over
2026-09-19/20 its runs read `success`, `skipped` (CI red), `success` — and on 2026-09-20 after `b512cc5` went green
(`success` at 07:27) the next four were all `skipped`, because every later commit's CI run was red. A flaky suite quietly
turns "every green commit is releasable" into "some commits get through".

## What failed in the six runs pushed 2026-09-20 (`5c7094a` … `8b3e5b6`)

| run | commit (what it changed) | failed job → test | known? |
| --- | --- | --- | --- |
| `35495888790` | `5c7094a` (docs only) | `dotnet-integration` → `BulkLoadIntegrationTests.ARaceBetween…` (racer *won*), `Scd2CdcGuaranteedDeliveryIntegrationTests` ×2 (identical mapped times) | yes — [bulk-load race](follow-up-phase-154-bulk-load-race-retrigger-still-observational.md), [SCD2 CDC](follow-up-phase-154-scd2-cdc-timestamp-mapping-race.md) |
| `35495888790` | same | `dotnet-windows` → `WindowsServiceEventLogTests.WriteError_ARealEntryIsReadableBackFromTheApplicationLog`: `Cannot open log for source 'DbDataSync'` — *Access is denied* writing the Event Log | partly — [Event Log follow-up](follow-up-phase-136-140-windows-service-event-log-output-never-read-by-a-human.md) is about reading it, not this failure; the write denial now has [its own doc](follow-up-event-log-tests-guard-registration-but-not-the-write.md) |
| `35496100986` | `099230a` (packaging + CI files) | `dotnet-integration` → `BulkLoadIntegrationTests.ARaceBetween…` (the run expected to succeed failed *"still loading"*) | yes |
| `35496100986` | same | `dotnet-windows` → `RunWatermarkTimeTests.EveryRunOnThePageIsDatedFromOneReadOfTheGroupsHistory`: `Assert.Equal` on a run id in the test's own `CompleteRun` helper (`RunWatermarkTimeTests.cs:404`) | now [its own doc](follow-up-runwatermarktimetests-claims-the-wrong-row-again-via-the-real-scheduler.md) — the real SchedulerService enqueues a competing row for the same task |
| `35496282777` | `305af60` (SPA renderer) | `playwright` → 116 passed, then `ENOTEMPTY` in teardown | yes — [temp dir](follow-up-a-temp-dir-that-cannot-be-deleted-fails-a-job-whose-tests-all-passed.md), a Linux occurrence |
| `35496389094` | `51920fe` (SPA viewer) | `dotnet-integration` → SCD2 CDC | yes |
| `35496467194` | `8b3e5b6` (Playwright spec) | `dotnet-integration` → `BulkLoadIntegrationTests.ARaceBetween…` (racer *won*, as in the first) | yes |
| `35495928319` | `b512cc5` | — green (the only one; `promote-test` moved `test` to it) | |

Of the six runs that finished, five failed, on different combinations of the above. Two failures are new to the
follow-ups (the Event Log *write* denial, `RunWatermarkTimeTests`); the rest were already documented and have now
recurred.

## Suggested order of attack

1. **The cheap, deterministic ones first.** SCD2 (a delay between the two operations, or a `<=`), documented with its
   options in the phase-154 SCD2 doc. Playwright teardown (retry the delete, don't fail on it). Both are test-harness
   fixes, not product changes.
2. **`RunWatermarkTimeTests` and the Event Log write**: read the tests; the first looks like a race in a helper that
   grabs "the run I just made" by something other than its id, the second like a source that has to be registered by an
   elevated step before a non-elevated test writes to it.
3. **The bulk-load race** last and separately — its own doc argues the assertion, not the code, is what's wrong, and
   the fix is a decision about what the test claims.
4. Decide **what CI should do about a known flake** in the meantime: `promote-test` could accept "only a known-flaky
   job red" (risky — it needs a list that stays honest), or the flaky jobs could retry once. Either is a policy
   choice; this doc doesn't make it — [that decision now has its own doc](follow-up-what-ci-should-do-about-a-known-flake.md),
   which rules the first option out: `release.yml` and `publish-snapshot.yml` both now require a green run for the SHA.

## What "done" looks like

Several consecutive `dev` pushes go green with no re-runs, and `promote-test` shows `success` for each.

## Later the same day (2026-09-20), two more runs

| run | commit | failed job → test | note |
| --- | --- | --- | --- |
| `35497291033` | `37e59bd` (docs only) | `dotnet-integration` → `Scd2CdcGuaranteedDeliveryIntegrationTests.ADuplicateKeyStartingOrEndingInADelete…` — again identical mapped times (`d0 closed at 2026-09-20T07:38:52.2300000, d1 opened at 2026-09-20T07:38:52.2300000`) | recurs; same `.2300000` fraction as the earlier failure at `…07:18:47.2300000` — worth knowing when picking the fix |
| `35497291033` | same | `dotnet-integration` → `MsSqlCdcReaderTests.ChangesFromEarliest_ReturnsTheChangeAtTheFloor_InclusiveOfMinLsn`: `Assert.Single() Failure: The collection contained 2 items` | now [its own doc](follow-up-cdc-floor-test-asserts-a-row-count-its-own-guard-allows-to-be-wrong.md) — not the SCD2 family after all; the test's own guard allows the floor to land short of the mark, which is when two rows are correct |
| `35497490691` | `5f7dfc6` | `dotnet-windows` → `UpdateConfirmationServiceTests.WithNothingApplied_ItStillClearsWhatEarlierStartsLeftBehind` — `UntilAsync` gave up after 3 s; that project's tests took 10 m 8 s on the runner | a phase-159 test's own patience; **fixed** by raising the deadline to 15 s (only spent when failing) |

Runs `35497164033` (`d312d01`) and `35497253627` (`ffbb295`) were green.

## 2026-09-23: three straight red runs, one real cause plus one new, unrelated flake

| run | commit | failed job → test | note |
| --- | --- | --- | --- |
| `35804896116`, `35804952962`, `35807159477` | three consecutive JDBC-work pushes | `playwright` → `connection-test-preview.spec.ts` — a new spec assumed the `mysql` docker-compose service without checking; that job's own `docker compose up -d --wait` only starts `mssql-source mssql-target postgres`. **Not a flake** — a real, deterministic gap once that spec existed, root-caused and fixed (added `mysql` to the job's service list; the spec's own `toContainText('reachable')` assertion was also too loose — "unreachable" contains that substring too, which is what let the connection actually failing read as a pass until a later assertion caught it). |
| `35807159477` (same run as above) | same | `dotnet-integration` → `MsSqlChangeTrackingConsistencyTests.ConcurrentDeletes_StillDeliverDeletesWithTheirKeys`: `SqlException` — *"Transaction (Process ID 84) was deadlocked on lock \| communication buffer resources with another process and has been chosen as the deadlock victim."* New to this catalogue, unrelated to the commit (docs + JDBC exception-handling changes, nothing touching `MsSqlChangeTrackingReader`). A real SQL Server deadlock under deliberately concurrent write pressure between the reader under test and the test's own background delete loop, on a connection with no priority set — either side could be picked as the victim, and only the deleter's own loop had a catch for it. **Fixed** — `SET DEADLOCK_PRIORITY LOW` on the deleter's connection, so it's always the one killed, converting an occasional test-crashing race into the "nothing to report" outcome that loop's own catch already handled. Not a retry or a sleep — a real, structural resolution of which of the two genuinely-conflicting sessions is expendable. See [`follow-up-what-ci-should-do-about-a-known-flake.md`](follow-up-what-ci-should-do-about-a-known-flake.md)'s own status table. |
