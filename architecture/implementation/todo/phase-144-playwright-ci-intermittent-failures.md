# Phase 144 — The `playwright` job: one test, failing most runs, hiding a quarter of the suite

**Status**: In progress, on branch `phase-144-playwright-ci-intermittent-failures`, not yet merged.
Items 1, 2 and 5 below are done and pushed; item 3 is investigated much further but not fixed (root
cause narrowed, not confirmed); item 4 is decided (leave as-is) rather than acted on; item 6 needs a
real CI run this branch hasn't had yet. See "Handoff" at the end.
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
`PackageReference Include="Microsoft.Data.SqlClient" ... ExcludeAssets="runtime"` says it should do
(phase 109h: consumers supply the runtime asset themselves) — except **nothing does supply it for MsSql
specifically**. Unlike DuckDb/mysql-connector, MsSql has no `KnownLibraries` catalog entry and no
runtime `LibraryInstaller` install step (`grep -n mssql src/DbDataSync.Libraries/KnownLibraries.cs` finds
nothing); it's a plain `ProjectReference` from `DbDataSync.Api`/`DbDataSync.TaskRunner`, both of which
also have no `Microsoft.Data.SqlClient` `PackageReference` of their own (the test projects that need one
directly — `Api.Tests`, `TaskRunner.Tests`, etc. — all added their own explicit reference; `Api`/
`TaskRunner` never did).

If the managed assembly were never resolvable at all, MsSql operations would fail **every single time**,
in every process, immediately — not in roughly one run in several dozen. Since real runs demonstrably
read and write through the real `Microsoft.Data.SqlClient` driver dozens of times per job without issue,
something else must be resolving it successfully most of the time — almost certainly `dotnet exec`'s
normal `<app>.deps.json`-driven probing falling through to the shared NuGet package cache
(`~/.nuget/packages/microsoft.data.sqlclient/...`) rather than the app's own output folder, since a
framework-dependent (non-published, non-self-contained) `dotnet build` output commonly resolves that way
for assets a `PackageReference` doesn't explicitly copy local. **This was not verified further** — no
Docker in this sandbox to spawn a real `DbDataSync.TaskRunner` process and watch it resolve the
assembly, and no way to force the specific race/eviction condition that would make that fallback
occasionally miss. It is a sharper, falsifiable lead — not a confirmed mechanism, and specifically not
something to patch (e.g. by dropping `ExcludeAssets="runtime"` or adding a direct `PackageReference` to
`Api`/`TaskRunner`) without first confirming it actually changes the failure rate, per this doc's own
standing rule not to assume a fix works.

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

## What this phase will do

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
3. **PARTIAL — Root-cause the `Microsoft.Data.SqlClient` load failure.** Materially narrowed (see "A
   concrete, falsifiable lead" above: the managed assembly is verifiably absent from `Api`'s/
   `TaskRunner`'s own build output, a real, checked-in-this-sandbox fact, not a guess) but **not
   confirmed** — the specific condition that makes the fallback resolution miss is still unknown, and no
   fix has been applied. Left for whoever picks this up next, ideally with either Docker locally or a way
   to attach to a live CI runner.
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
6. **NOT DONE — confirm green across several consecutive runs.** Needs this branch's own real CI runs,
   which have not happened yet as of this doc's last edit — see "Handoff".

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

- Is the `Microsoft.Data.SqlClient` failure a consequence of the retry (state left behind by a failed
  run) or an independent bug that retries merely expose? The progressive behaviour — tests that passed on
  attempt 1 failing by retry #2 — suggests the former, but "suggests" is doing real work in that sentence.
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
  though, with no Docker needed** — that's what surfaced "A concrete, falsifiable lead" above (the
  managed `Microsoft.Data.SqlClient.dll`'s real absence from both projects' own build output). Worth
  remembering for the next session on this doc: not every part of this investigation needs Docker, only
  the parts that need a live SQL Server or a live web app.
- **The green-run comparison is what made the SqlClient correlation legible** — zero occurrences in
  either green run, 3–5 in every red one. Worth repeating as a technique: diffing a passing run against a
  failing one separated the incidental log noise from the signal far faster than reading either alone.

## Handoff — 2026-09-15

**Done, on this branch, not yet merged:**
- `tests/DbDataSync.Web.Tests/mapping-load-waiter.ts` — new, `waitForLoadToComplete`, the TS sibling of
  phase 141's `MappingLoadWaiter.cs`.
- `tests/DbDataSync.Web.Tests/tests/golden-path.spec.ts` — test 18 now awaits it between the `Succeeded`
  poll and the `second order` read.
- `tests/DbDataSync.Web.Tests/playwright.config.ts` — `reporter` adds `['github']` and a CI-only JSON
  report.
- `.github/workflows/ci.yml` — `playwright` job uploads that JSON report unconditionally as
  `playwright-report`.
- This doc, rewritten with real evidence from a live CI run and a real local build, in place of the
  original sampled/inferred version.

**Verified so far:** the file loads and its tests list cleanly under
`npx playwright test --list tests/golden-path.spec.ts` (45 tests found, test 18 included) — no syntax or
module-resolution error. **Not yet verified:** an actual run of test 18 against real containers (no
Docker in this sandbox), and therefore no confirmation yet that the fix actually turns the race green
rather than just compiling.

**What's left, in order:**
1. Push this branch, open a PR, let CI run it.
2. If test 18 is green and the race doesn't reproduce, that's item 2 confirmed for real, not just by
   inspection.
3. Item 3 (the SqlClient failure) is still open — this branch does not attempt a fix, only a sharper
   lead (see "A concrete, falsifiable lead" above). Watch specifically for whether it recurs on this
   branch's own CI runs; if it does, that's a second, independent data point on when it happens (which
   test, which retry number, how far into the job) worth adding to this doc before anyone attempts a fix.
4. Per item 6, don't merge on one green run — this job's own history (3 of the last 12 runs passed with
   nothing fixed) means one green run is not evidence. Watch several.
5. On green (repeated) and a decision on item 3 (fixed, or explicitly deferred as its own follow-up
   phase), move this doc to `implementation/done/` in the merge commit, rewritten as a retrospective per
   the usual convention.
