# Phase 144 — The `playwright` job: one test, failing most runs, hiding a quarter of the suite

**Status**: Complete. Merged via PR #3 (branch `phase-144-playwright-ci-intermittent-failures`) after
three consecutive green CI runs post-fix, against a pre-fix baseline of 3 green out of the last 12. Items
1, 2, 3 and 5 below are done; item 4 was decided (leave as-is) rather than acted on; item 6 is satisfied
by that CI history. See "Final verification" at the end.
**Plan reference**: none — logged directly from this session's own investigation, the same way phase 140
was. Phase 140's "CI result — 2026-09-15" section names this job as needing a follow-up of its own and
does not attempt one; this is that follow-up. Prior art for the job itself:
`architecture/planning/done/playwright-suite-in-ci.md` and `done/phase-098-playwright-suite-in-ci.md`.

## Why

The `playwright` job has failed **most runs on `main` for weeks**. It is not newly broken and it is not
consistently broken, which is why it has gone unattended: any single red run reads as a flake.

It also means the suite currently proves nothing, and the specific way it fails makes that worse than it
sounds. `golden-path.spec.ts` is a serial block, so the one failing test takes **25 further tests down
with it as "did not run"** — roughly a quarter of the suite's 104 tests never execute on a red run. The
whole argument for standing this job up (`playwright-suite-in-ci.md`: golden-path test 18 sat failing on
`main` from phase 94 until somebody happened to run it locally) is void while that is true.

There is a sharp irony worth stating plainly, because it is the strongest argument for fixing this
quickly: **the test that fails is golden-path test 18 — the exact test this job was created to catch.**
`ci.yml`'s own comment on the `playwright` job names it.

## What the logs actually say

Read from the authenticated Actions API across six runs (three red, two green, one for history), not
sampled or inferred.

### One test fails, and it is the same test every time

Every red run ends identically:

```
1 failed
1 flaky
25 did not run
77 passed (7.0m)
```

The failure is always `tests/golden-path.spec.ts:666:3` —
*"18 - a target table that does not exist is named, created from the plan, and replicated into"*.
The flaky one is always test 06, which recovers on retry. Nothing else fails, in any red run examined.

### The primary assertion that fails is the same one every time

In all three red runs checked (`a2517ad`, `cc278dd`, `d8585eb`), the *first* attempt fails at
`golden-path.spec.ts:789`:

```
Error: expect(received).toContain(expected)
Expected substring: "second order"
Received string:    ""
```

The line before it — `expect.poll(...).toContain('Succeeded')` at line 786, which waits up to 90s for one
of this mapping's runs to report success — **passes**. So a run for the `orders` mapping reports
`Succeeded`, and the freshly-provisioned `dbo.PwTgtOrders` is then read back **empty**.

### The leading hypothesis: phase 134's Bulk Load divert, which phase 141 already fixed elsewhere

Test 18's own setup makes it unusually exposed. It creates its own replication
(`playwright-provisioned`) on a **continuous schedule**, provisions a target table that does not exist,
triggers a pass, and asserts over *the mapping's run list*:

```ts
return body.runs
  .filter((r) => r.mappingName === NEW_MAPPING)
  .map((r) => `${r.status}${r.errorSummary ? `: ${r.errorSummary}` : ''}`)
}, { timeout: 90_000 }).toContain('Succeeded')
```

Since phase 134, a position-capturing reader's first pass no longer reads directly — it diverts, requests
a Bulk Load, and the Primary run itself completes. A run list containing `Succeeded` therefore no longer
means the data has landed: the Primary run can be terminal and successful while the Bulk Load that
actually moves the rows is still in flight. `toContain('Succeeded')` is satisfied by the wrong run, the
test reads the table immediately, and finds it empty.

**This is the identical bug phase 141 fixed in the .NET suites**, and its commit message describes this
exact shape:

> an HTTP-driven test triggers a Primary pass, waits only for that run's own row to go terminal, then
> immediately reads real target rows … racing the Bulk Load a position-capturing reader's first pass
> requests instead of reading directly (phase 134).

Phase 141's remedy was a shared `WaitForLoadToCompleteAsync` helper polling the mapping's `read-state`
endpoint until `Hold != Loading`. **The Playwright suite never received the equivalent**, because phase
141 was scoped to `dotnet-integration`'s failures. That also explains the intermittency exactly: it is a
race, so when the Bulk Load happens to win, the test passes — which it does in roughly one run in four.

The CI history is consistent with this, though not by itself conclusive: the two runs immediately before
phase 134 landed (`433e1d7`, `27a4c24`) were green, and `af9e08a` — "Phase 134: an initial load becomes a
bulk load" — is red, with red dominating every stretch since. There were also red runs *before* phase
134 (traced to a different, since-fixed cause: the job never built `DbDataSync.Cli`, fixed by `433e1d7`),
so this is corroboration rather than proof.

### A second, separate symptom on retries — not yet explained

On retry, a different error appears, and it is **perfectly correlated with the red runs**: 3–5 occurrences
per red run, **zero** in either green run.

```
Failed: Could not load file or assembly 'Microsoft.Data.SqlClient, Version=7.0.0.0,
Culture=neutral, PublicKeyToken=23ec7fc2d6eaa4a5'. The system cannot find the file specified.
```

It surfaces as a **"Config error"** on a live run in the UI, and by retry #2 it affects even the `items`
mapping that passed cleanly on the first attempt — so whatever goes wrong appears to be **progressive
within a job**, poisoning later runs in a process that was working earlier.

This is probably downstream of the phase 109h/109i driver decoupling (`Microsoft.Data.SqlClient` now
carries `ExcludeAssets` on the driver package, so consumers supply it themselves — the same gap that
needed explicit `PackageReference`s added to `DbDataSync.Api.Tests`). But the TaskRunner loads it fine
for tests 01–36 in the very same job, so a plain "missing from the output directory" explanation does not
fit, and no guess here is worth more than reading it properly.

Whether fixing the race removes this too — because no retry ever happens — or whether it is an
independent bug that merely needs a retry to become visible, is genuinely open. **It should not be
assumed away.**

### Confirmed against a real failing run, not just read from the doc's own log samples

Both this section's hypothesis and the one above were checked directly against run `35021140430`
(`gh run view 35021140430 --json jobs`, then `gh run view --job=<id> --log`), the CI run this project's
own phase 142 doc landed on — not the `show-trace` route item 1 originally proposed, which needs a
downloaded trace bundle; the raw log turned out to carry the same evidence more directly.

**The race is confirmed exactly as hypothesized.** Attempt 0 of test 18 fails in 7.7s, at line 789 —
`.toContain('second order')` against an empty read — immediately after the `Succeeded` poll at line 780
passed. No ambiguity: this is the shape phase 141 already fixed in `Api.Tests`, reproduced with a real
timestamped log this session read itself, not inferred from a sample.

**The `Microsoft.Data.SqlClient` failure is real, and its shape refines the doc's original guess.** In
this run it did not occur 3–5 times — it occurred exactly **once**, and not on test 18 at all: on retry
#2 (the *third* full pass through the serial block — `test.describe.serial`'s retry model reruns the
whole block from test 01, not just the failed test, which is why 01–17 all show `(retry #1)`/`(retry #2)`
suffixes in the log), test **06**'s own trigger of the `items` mapping failed with

```
Run started for mapping 'items' (Primary). Config error: Could not load file or assembly
'Microsoft.Data.SqlClient, Version=7.0.0.0, Culture=neutral, PublicKeyToken=23ec7fc2d6eaa4a5'.
The system cannot find the file specified.
```

which then cascaded: 06 failing mid-block meant every later test in that pass — including 18 — is
recorded as `-` (did not run) for retry #2. So in this specific run, the *nominal* cause of the whole
job going red was this SqlClient failure, not the race — the race had already failed the job on attempt 0
and retry #1 before retry #2 ever got there. Both bugs are real and independent; either alone is enough
to redden a run, which is consistent with the doc's original observation that red runs correlate with
neither a specific commit nor a specific diff.

### A concrete, falsifiable lead on the SqlClient failure — not yet a confirmed root cause

Checked directly against this sandbox's own build output (a real `dotnet build` of `DbDataSync.Api` and
`DbDataSync.TaskRunner`, not a guess):

```
$ find src/DbDataSync.Api/bin src/DbDataSync.TaskRunner/bin -iname "*sqlclient*"
src/DbDataSync.TaskRunner/bin/Debug/net10.0/runtimes/win-x64/native/Microsoft.Data.SqlClient.SNI.dll
src/DbDataSync.Api/bin/Debug/net10.0/runtimes/win-x86/native/Microsoft.Data.SqlClient.SNI.dll
src/DbDataSync.Api/bin/Debug/net10.0/runtimes/win-x64/native/Microsoft.Data.SqlClient.SNI.dll
src/DbDataSync.Api/bin/Debug/net10.0/runtimes/win-arm64/native/Microsoft.Data.SqlClient.SNI.dll
src/DbDataSync.TaskRunner/bin/Debug/net10.0/runtimes/win-arm64/native/Microsoft.Data.SqlClient.SNI.dll
src/DbDataSync.TaskRunner/bin/Debug/net10.0/runtimes/win-x86/native/Microsoft.Data.SqlClient.SNI.dll
```

**The managed `Microsoft.Data.SqlClient.dll` itself is not there at all** — only the Windows-only native
SNI shim leaked through. This is exactly what `DbDataSync.Drivers.MsSql.csproj`'s
`PackageReference Include="Microsoft.Data.SqlClient" ... ExcludeAssets="runtime"` says it should do —
its own comment names the real mechanism directly: `LibraryRegistry`'s `AssemblyLoadContext.Resolving`
handler is supposed to serve the real assembly from `<repo>/libraries/microsoft-data-sqlclient/lib/` at
first touch, the same phase 109h mechanism `MsSqlStateDialect`/`PostgresStateDialect` already use, and
`microsoft-data-sqlclient` **is** a real `KnownLibraries` catalog entry (it was a wrong initial read of
`grep -n mssql` — the id is `"microsoft-data-sqlclient"`, not `"mssql"`).

**Root cause, confirmed, not just narrowed**: `DriverConnectionFactory.EnsureLibraryInstalledAsync` (the
API's own `OpenAsync`) auto-installs `microsoft-data-sqlclient` into `libraries/` the first time the API
opens a real MsSql/Postgres connection (schema browsing, provisioning, the Test button). But
`DbDataSync.TaskRunner` — a genuinely separate process, spawned fresh per replication by
`ProcessSupervisor`, and the thing that actually reads/writes real data — had **no equivalent call
anywhere**. `src/DbDataSync.TaskRunner/Program.cs` only ever called `LibraryRegistry.LoadAll()` **once**,
at its own process startup, arming a resolver only for whatever already happened to be on disk at that
exact moment. If nothing had installed the library into that replication's shared repo root *before*
this particular worker process started — no ordering between "the API touches this driver first" and
"a worker process for this driver spawns" is ever guaranteed by `ProcessSupervisor`, which enqueues work
and spawns the worker without waiting for any install — that worker's own `new SqlConnection(...)`
throws exactly this `FileNotFoundException`, deterministically, for that process, regardless of whether
the library is durably installed and working fine everywhere else.

**Verified directly, not just reasoned about** — with no Docker, using a throwaway console harness
against the real `DbDataSync.Libraries`/`DbDataSync.State`/`DbDataSync.Drivers.MsSql` projects (not the
Api/TaskRunner processes themselves, but the exact same code paths):
- **Negative control**, reproducing the bug: a fresh repo root, `LibraryRegistry.LoadAll()` (no install
  attempted, matching the old `TaskRunner/Program.cs`), then `MsSqlDriver.CreateConnection(...)` — throws
  `System.IO.FileNotFoundException: Could not load file or assembly 'Microsoft.Data.SqlClient,
  Version=7.0.0.0, Culture=neutral, PublicKeyToken=23ec7fc2d6eaa4a5'. The system cannot find the file
  specified.` — **character-for-character the same message** the real CI run logged.
- **Positive control**, with the fix: the same fresh repo root, but calling the new
  `BuiltInDriverLibraries.EnsureInstalledAsync` first — installs `microsoft-data-sqlclient` for real
  (confirmed: `libraries/microsoft-data-sqlclient/lib/` exists afterward), and the same
  `CreateConnection` + `OpenAsync` against an unreachable address now fails with a real
  `Microsoft.Data.SqlClient.SqlException` ("server was not found or was not accessible") — a genuine
  network failure, not a missing-assembly one.

**The fix**: `src/DbDataSync.State/BuiltInDriverLibraries.cs` (new) holds the shared
check-install-register sequence (moved out of `DriverConnectionFactory`, which now just delegates to it)
— `DbDataSync.State` because it already depends on `DbDataSync.Libraries` and both `Api` and
`TaskRunner` already depend on it, avoiding a circular reference to reuse
`MsSqlStateDialect.LibraryId`/`PostgresStateDialect.LibraryId` rather than a second copy of the id
string. `RunExecutor.OpenAsync` (`DbDataSync.TaskRunner`) now calls it too, immediately before
`driver.CreateConnection(...)` — the one place every connection this process opens actually goes
through. `Program.cs` passes its own already-constructed `libraryRegistry`/`options.RepoRoot` through.

**Known, accepted residual gap**: the install lock is per-process (a `SemaphoreSlim`, matching
`DriverConnectionFactory`'s own original one, added for a real `ConcurrentRunsIntegrationTests` race).
Two different processes (the API and a worker, or two workers for two different replications) racing to
install the *same* not-yet-installed library for the first time at the same moment are not serialized
against each other — a genuine cross-process file lock would close this fully, but is more than this fix
warrants: in practice the API always touches a driver (schema browsing) before a replication using it
can even be configured to run, so the true first-install race this doc is about (worker-before-API) is
what's fixed, and worker-vs-worker was never possible before this fix either way (only the API ever
installed anything). Documented in `BuiltInDriverLibraries`'s own doc comment, not silently assumed away.

### The full CI picture

30 most recent `main` runs, `playwright` job only, newest first. `cancelled` = superseded by a newer push.

```
cancelled a13ad82   failure  b664f88   success  6e66021   failure  2e05e02
failure   3ba3213   cancelled c69f50b  success  a7f39c4   failure  680971b
cancelled 62d06df   failure  d8585eb   failure  15330e0   success  c266078
failure   a2517ad   failure  cc278dd   failure  885c569   failure  fd9ca25
failure   8e28a35   success  3eb2598   failure  14e545e   success  9b5e317
success   1751b16   failure  5449ee3   failure  92ea553   success  b5ac287
failure   af9e08a   success  27a4c24   success  433e1d7   failure  b7af603
failure   f2e15ac   failure  485fd7d
```

Worth noting for anyone re-deriving this: the failures **do not correlate with the diff**. Several red
runs are single-file commits changing one Markdown doc under `architecture/` — nothing the SPA renders or
the API serves — while `c266078`, a 19-file commit adding a whole Monitoring sub-tab and its own new
spec, passed. Bisecting product commits would start in the wrong place.

## What this phase did

1. **DONE — Confirm the hypothesis against a real failing run** before changing anything. Done against
   run `35021140430`'s `playwright` job via `gh run view --job=<id> --log`, not the trace-bundle route
   originally proposed (the raw log carried the same evidence more directly, no download/`show-trace`
   needed) — see "Confirmed against a real failing run" above. Both the race and the SqlClient failure
   are real, independent, and each alone reddens a run.
2. **DONE — Apply phase 141's fix to the Playwright suite** — `tests/DbDataSync.Web.Tests/mapping-load-waiter.ts`
   (`waitForLoadToComplete`), the TypeScript sibling of `MappingLoadWaiter.WaitForLoadToCompleteAsync`,
   polling the same `read-state` endpoint. Wired into test 18 right after the `Succeeded` poll and
   before the `second order` read. **Audited the rest of the suite for the same shape and found none** —
   grepped every spec for `toContain('Succeeded')` (only `golden-path.spec.ts` matches at all) and cross-
   checked every `querySql(...)` call against which mapping it reads: every other one reads `items`/
   `TARGET_TABLE`, whose first-ever pass happened many tests before any of those assertions run, not in
   the same test as the trigger — test 18's `orders` mapping is the only one that triggers and reads back
   inside a single test. `mapping-column-add.spec.ts` and `replication-wide-provisioning.spec.ts`, the
   doc's own guesses, don't call `querySql` or assert `Succeeded` at all.
3. **DONE — Root-cause the `Microsoft.Data.SqlClient` load failure, and fix it.** Confirmed, not just
   narrowed — see "Root cause, confirmed, not just narrowed" above, verified both directions with a
   throwaway console harness (no Docker needed): without the fix, `MsSqlDriver.CreateConnection`
   reproduces the exact real CI error message character-for-character; with it, the same call succeeds
   and only a real network failure remains. Fixed in `src/DbDataSync.State/BuiltInDriverLibraries.cs`
   (new, shared) plus `DbDataSync.TaskRunner`'s `RunExecutor.OpenAsync`/`Program.cs` now calling it,
   mirroring what `DbDataSync.Api`'s `DriverConnectionFactory` already did. Still needs this branch's own
   CI to confirm the failure stops recurring for real, not just in this isolated harness — see item 6.
4. **DECIDED, not done — the serial cascade stays as it is.** Test 18 is not as self-contained as it
   looks: it selects `SRC_CONNECTION_NAME`/`TGT_CONNECTION_NAME` (created in test 02) for its new
   mapping's connections, so pulling it out of the serial block means either giving it its own
   connections (real, if small, duplication) or a lighter-weight dependency-tracking scheme this suite
   doesn't have today. Worth doing, but it's a separable restructuring, not part of fixing the two actual
   bugs this phase was opened for — noted here rather than attempted so it isn't silently dropped.
5. **DONE — Make the job self-reporting.** `playwright.config.ts`'s `reporter` now adds `['github']` and
   a small `['json', { outputFile: 'test-results/results.json' }]`, both gated on `process.env.CI` so a
   local `npx playwright test` is unaffected. `ci.yml`'s `playwright` job uploads that JSON as its own
   `playwright-report` artifact unconditionally (`if: always()`), not only on failure.
6. **DONE — confirm green across several consecutive runs.** Three consecutive green `playwright` runs
   on this branch (`35025393950`, `35033667955`, `35034756695`), the last two after the SqlClient fix
   landed — see "Final verification".

## What this phase will not do

- **Re-litigate `workers: 1` / `fullyParallel: false`.** `playwright-suite-in-ci.md` settled it and said
  to revisit only if CI runtime becomes a real problem; a ~5-minute suite is not that problem. Item 4 is
  about one test's membership of one serial block, not about parallelising the suite.
- **Reduce or remove `retries: 2` as a first move.** It is load-bearing on a slow runner and its comment
  explains why. It may deserve revisiting once the race is fixed; changing it first would remove the
  evidence that a failure survived three attempts.
- **Touch `dotnet-integration`**, which is also red and is its own separate matter.
- **Add the component-test layer** (vitest/Testing Library) — still deferred, tracked in
  `architecture/planning/todo/spa-has-no-component-test-layer.md`.

## Open questions to resolve during implementation

- ~~Is the `Microsoft.Data.SqlClient` failure a consequence of the retry, or independent of it?~~
  **Resolved: independent of retries specifically, though retries make it more likely to be observed.**
  The real trigger is "a TaskRunner worker for a given driver type spawns before that driver has ever
  been installed via the API" — a retry restarting the whole serial block from test 01 means more worker
  spawns happen over the job's lifetime (more chances to hit an unlucky ordering), but the bug doesn't
  need a retry to exist; a first-attempt run unlucky enough to spawn a worker before the API's own first
  connection-open for that driver would hit it too.
- ~~Do other specs share test 18's "trigger a pass, then read real rows" shape?~~ **Resolved: no.**
  `mapping-column-add.spec.ts` and `replication-wide-provisioning.spec.ts` don't call `querySql` or
  assert `Succeeded` at all; `bulk-load-progress.spec.ts` neither. Every other `querySql` in the suite is
  against `items`/`TARGET_TABLE`, read many tests after that mapping's own first pass, not in the same
  test as the trigger.
- Why were there red runs *before* phase 134 (`b7af603`, `f2e15ac`, `485fd7d`)? Attributed here to the
  since-fixed missing `DbDataSync.Cli` build, but not verified — if they were the same failure, the phase
  134 correlation is weaker than it looks and the hypothesis needs re-examining.

## Investigation notes

- **No local reproduction was possible.** The suite needs the `docker compose` topology (`test-db.ts`
  reaches SQL Server by container name via `docker exec`); Docker is not installed on this machine. Node
  24 is present, so only the containers are missing. Everything above is from CI logs.
- **A real `dotnet build src/DbDataSync.Api`/`dotnet build src/DbDataSync.TaskRunner` was possible,
  though, with no Docker needed** — that's what first surfaced the managed `Microsoft.Data.SqlClient.dll`'s
  real absence from both projects' own build output, the thread that led to the confirmed root cause.
  Worth remembering for the next session on this doc: not every part of this investigation needs Docker,
  only the parts that need a live SQL Server or a live web app.
- **A throwaway console project (referencing the real `DbDataSync.Libraries`/`DbDataSync.State`/
  `DbDataSync.Drivers.MsSql` projects directly, no `Microsoft.Data.SqlClient` `PackageReference` of its
  own) is what actually confirmed the root cause and the fix**, with no Docker and no real SQL Server:
  a fresh temp repo root plus `MsSqlDriver.CreateConnection(...)` against an address that doesn't resolve
  is enough to prove *whether the assembly loads at all*, entirely separately from whether the network
  call that follows succeeds. Worth reusing as a pattern for the next library-loading question this repo
  runs into — it isolates "does the assembly resolve" from "does the connection actually work" far more
  cheaply than a full integration test.
- **The green-run comparison is what made the SqlClient correlation legible** — zero occurrences in
  either green run, 3–5 in every red one. Worth repeating as a technique: diffing a passing run against a
  failing one separated the incidental log noise from the signal far faster than reading either alone.

## Files changed

- `tests/DbDataSync.Web.Tests/mapping-load-waiter.ts` (new) — `waitForLoadToComplete`, the TS sibling of
  phase 141's `MappingLoadWaiter.cs`.
- `tests/DbDataSync.Web.Tests/tests/golden-path.spec.ts` — test 18 now awaits it between the `Succeeded`
  poll and the `second order` read.
- `tests/DbDataSync.Web.Tests/playwright.config.ts` — `reporter` adds `['github']` and a CI-only JSON
  report.
- `.github/workflows/ci.yml` — `playwright` job uploads that JSON report unconditionally as
  `playwright-report`.
- `src/DbDataSync.State/BuiltInDriverLibraries.cs` (new) — the shared check-install-register sequence for
  a built-in driver's library, moved out of `DriverConnectionFactory`.
- `src/DbDataSync.Api/Services/DriverConnectionFactory.cs` — `EnsureLibraryInstalledAsync` now delegates
  to the shared helper instead of duplicating it.
- `src/DbDataSync.TaskRunner/RunExecutor.cs`/`Program.cs` — `RunExecutor` takes a `LibraryRegistry` and
  `repoRoot` now, and calls the shared helper in `OpenAsync` before every connection it opens.
- `tests/DbDataSync.TaskRunner.Tests/{RunExecutorTests,RunExecutorIntegrationTests,Scd2NaturalKeyIntegrationTests}.cs`
  — updated for `RunExecutor`'s new constructor parameters.

## Final verification

**Local, all without Docker:**
- `npx playwright test --list tests/golden-path.spec.ts` — 45 tests found, test 18 included, no syntax or
  module-resolution error.
- `dotnet build`/`dotnet test --filter "Category!=Integration"` clean across `DbDataSync.Api`,
  `DbDataSync.TaskRunner`, and their three touched test projects (`TaskRunner.Tests`: 46/46;
  `Api.Tests`: 433/434, the one failure — `ChangeReaderFirstPassContractTests
  .EveryDeclaredProofNamesATestThatExists` — confirmed pre-existing by reproducing it identically with
  this branch's changes stashed away; `State.Tests`: 205/205).
- The SqlClient root cause and fix were verified with a throwaway console harness (see "Investigation
  notes" above): reproduces the exact real error message without the fix, and a genuine network failure
  (not a missing-assembly one) with it.

**On CI, three consecutive green `playwright` runs, against a pre-fix baseline of 3 green out of the
last 12:**
- **`35025393950`** (race fix only, SqlClient fix not yet landed): `playwright` 104/104, zero retries,
  zero `Microsoft.Data.SqlClient` occurrences. `dotnet-windows` failed on an unrelated pre-existing flake
  (`RunWatermarkTimeTests.EveryRunOnThePageIsDatedFromOneReadOfTheGroupsHistory`) that did not recur on
  either later run — this branch touches no `.NET` watermark code.
- **`35033667955`** (after the SqlClient fix landed): `playwright` 104/104, zero retries. A real
  `Microsoft.Data.SqlClient.SqlException: Login failed for user 'sa'` did appear in the `[WebServer]` log
  — from `ChangePollingGate.AdmitAsync`, right after "Nonqualified transactions are being rolled back" at
  the very end of the run (global teardown dropping the test database while a background poll happened to
  fire at the same moment), handled gracefully (a `warn`-level log, not a crash). Notable precisely
  because it's **a real login attempt that reached the server, not a `FileNotFoundException`** — the
  exact condition (a live SqlClient call) that used to trip the bug, now working.
- **`35034756695`** (a docs-only push): `playwright` 104/104 again, the identical benign teardown-timing
  SqlClient pattern as the previous run, `dotnet`/`web`/`dotnet-windows`/`dotnet-integration` all green.

Merged on this evidence, per the user's explicit go-ahead.
