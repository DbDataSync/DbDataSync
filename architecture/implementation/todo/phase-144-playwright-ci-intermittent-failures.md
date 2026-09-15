# Phase 144 — The `playwright` job: one test, failing most runs, hiding a quarter of the suite

**Status**: Planned, not started. Root cause narrowed to a single test and a leading hypothesis with a
known fix elsewhere in the repo — see "What the logs actually say".
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

1. **Confirm the hypothesis against a real failing run** before changing anything — specifically, that
   the `orders` mapping's run list contains a terminal `Succeeded` Primary run *plus* an in-flight Bulk
   Load at the moment line 789 reads the table. The `playwright-failure-artifacts` trace bundle for a red
   run has the API responses in it; `npx playwright show-trace` on the retained artifact is the cheapest
   confirmation, and it needs no local database.
2. **Apply phase 141's fix to the Playwright suite** — a `read-state`-polling wait equivalent to
   `WaitForLoadToCompleteAsync`, used wherever a test triggers a pass and then reads real target rows.
   Audit the other specs for the same pattern rather than fixing only test 18: phase 141 found this
   shape in several `Api.Tests` classes' shared setup, and there is no reason the SPA suite would have
   it in exactly one place.
3. **Root-cause the `Microsoft.Data.SqlClient` load failure separately**, and explicitly re-check whether
   it still occurs once retries stop happening. If it does not reproduce, say so rather than declaring it
   fixed — it would merely be unobserved.
4. **Reconsider the serial cascade.** One failing test hiding 25 others is a large amount of lost signal
   for a suite whose entire justification is catching what local runs miss. Whether test 18 (which builds
   its own replication, its own source table and its own target) genuinely needs to live inside the shared
   serial block is worth asking; it is largely self-contained already.
5. **Make the job self-reporting.** `reporter: [['list']]` only — no `github` reporter, so the failing
   job's annotations carry nothing but the unrelated Node 20 warning, and no JSON/JUnit/HTML report is
   produced. Everything in this doc took an authenticated `gh` and several multi-megabyte log downloads to
   establish; adding `['github']` plus a small uploaded report would have put the failing test's name on
   the run page from the start. Cheap, and the reason nine red runs drew no investigation.
6. **Confirm green across several consecutive runs, not one.** Three of the last twelve runs passed
   without anyone fixing anything, so a single green run proves nothing here.

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
- Do other specs share test 18's "trigger a pass, then read real rows" shape? `mapping-column-add`,
  `replication-wide-provisioning` and `bulk-load-progress` are the likely candidates and none of them has
  failed yet, which may only mean their timing is luckier.
- Why were there red runs *before* phase 134 (`b7af603`, `f2e15ac`, `485fd7d`)? Attributed here to the
  since-fixed missing `DbDataSync.Cli` build, but not verified — if they were the same failure, the phase
  134 correlation is weaker than it looks and the hypothesis needs re-examining.

## Investigation notes

- **No local reproduction was possible.** The suite needs the `docker compose` topology (`test-db.ts`
  reaches SQL Server by container name via `docker exec`); Docker is not installed on this machine. Node
  24 is present, so only the containers are missing. Everything above is from CI logs.
- **The green-run comparison is what made the SqlClient correlation legible** — zero occurrences in
  either green run, 3–5 in every red one. Worth repeating as a technique: diffing a passing run against a
  failing one separated the incidental log noise from the signal far faster than reading either alone.
