# Phase 144 — The `playwright` job fails most runs, and nothing on the run page says why

**Status**: Planned, not started.
**Plan reference**: none — logged directly from this session's own investigation, the same way phase 140
was. Phase 140's "CI result — 2026-09-15" section names this job as needing a follow-up of its own and
does not attempt one; this is that follow-up. Prior art for the job itself:
`architecture/planning/done/playwright-suite-in-ci.md` and `done/phase-098-playwright-suite-in-ci.md`.

## Why

The `playwright` job has failed **9 of the last 12 runs on `main` that reached a conclusion**. It is not
newly broken and it is not consistently broken, which is exactly why it has gone unattended: any single
red run reads as a flake, and the job has no voice on the run page to argue otherwise.

It also means the suite currently proves nothing. A job that fails three runs out of four is one nobody
reads, and the whole argument for standing it up (`playwright-suite-in-ci.md`: golden-path test 18 sat
failing on `main` from phase 94 until somebody happened to run it locally) is void while that is true.
A real regression landing today would look exactly like the last nine runs.

Phase 140's own doc asserted, correctly at the time it was written, that "the `dotnet` and `playwright`
jobs are both green as of the latest run." That has not been true for some time, and nothing surfaced
the change — which is itself part of what this phase is about.

## What the evidence actually shows

All of this is read from the GitHub Actions API for `main`, not inferred.

### 1. It is intermittent, and independent of what the commit changed

| run | commit | files | subject | `playwright` |
|---|---|---|---|---|
| 35007443761 | `c69f50b` | 2 | Phase 143 doc | cancelled |
| 35006082487 | `a7f39c4` | 6 | Promote ReadHold planning doc to phase 143 | **pass** |
| 35004306147 | `680971b` | 1 | Planning doc: reorder-and-throw | fail |
| 35003858618 | `62d06df` | 1 | Phase 140 doc to `done/` | cancelled |
| 35002376768 | `d8585eb` | 1 | Planning doc: ReadHold strand | fail |
| 35001958114 | `15330e0` | 2 | Phase 139 doc to `done/` | fail |
| 35001833005 | `c266078` | 19 | Phase 139: Bulk Load History (real SPA feature) | **pass** |
| 35001058463 | `a2517ad` | 17 | Phase 140: Windows CI fixes | fail |
| 34991837713 | `cc278dd` | 1 | Phase 138 doc | fail |
| 34991040790 | `885c569` | 2 | Phase 141: Scd2Cdc fix | fail |
| 34941222477 | `fd9ca25` | 1 | Phase 141 doc | fail |
| 34940965723 | `8e28a35` | 10 | Phase 141: Api.Tests fixes | fail |
| 34938515579 | `3eb2598` | 3 | Phase 141: TaskRunner.Tests fixes | **pass** |
| 34930605612 | `14e545e` | 3 | Rescope phase 140 | fail |

The decisive rows are the single-file ones. `680971b`, `d8585eb`, `cc278dd` and `fd9ca25` each change
**one Markdown file under `architecture/`** — nothing the SPA renders, nothing the API serves, nothing
the suite touches — and all four failed. Meanwhile `c266078`, a 19-file commit adding a whole new
Monitoring sub-tab with its own new spec, passed.

So the failure is **not caused by the code under test**. Any investigation that starts by bisecting
product commits is starting in the wrong place.

### 2. Failing runs are ~2 minutes slower, not hung

`Test (Playwright)` step durations:

- failing: 7m00s (`680971b`), 7m03s (`a2517ad`)
- passing: 5m19s (`a7f39c4`), 4m50s (`c266078`)

A hang, a `webServer` that never comes up, or a global-setup collapse would look nothing like this — it
would hit the 120s per-test timeout across the board, or fail in seconds. A roughly two-minute delta over
a five-minute baseline is the shape of **a small number of tests failing and being retried**, with the
rest of the suite running normally. Whatever is wrong is narrow.

### 3. `retries: 2` makes the intermittency stranger, not simpler

`playwright.config.ts` sets `retries: process.env.CI ? 2 : 0`, deliberately and with a good comment: a
first-attempt timeout on a loaded runner is often just slowness. But that means whatever fails in a red
run **failed three times in a row inside that run** — and then the whole suite passes clean on a
neighbouring run whose commit changed one Markdown file.

That combination is the central puzzle, and it should be resolved before anything else:

- A genuinely flaky test with, say, a 60% per-attempt failure rate would still fail all three attempts
  about a fifth of the time — plausible, and it would fit the table.
- A test that is *order*- or *state*-dependent (the suite is `fullyParallel: false`, `workers: 1`, so
  every test runs in a fixed order against shared, accumulating state: one git-backed config repo, one
  SQLite state DB, real SQL Server and Postgres databases) would fail deterministically once something
  earlier leaves the wrong state behind — and the "something earlier" could itself be timing-dependent.
- A runner-resource threshold (SQL Server, Postgres, MySQL, the API, a spawned TaskRunner, the Vite dev
  server and Chromium all on one standard runner) would produce exactly this: mostly-fails, sometimes-
  passes, uncorrelated with the diff.

These are different bugs with different fixes. The data above cannot distinguish them; the logs can.

### 4. The real blocker: the job cannot be diagnosed from the outside

This is the finding that should be fixed first, because everything else depends on it.

- `reporter: [['list']]` — the console reporter only. Playwright's `github` reporter, which emits
  `::error` annotations that appear directly on the run and the commit, is not enabled. Checking the
  failing job's annotations returns **only** the unrelated Node 20 deprecation warning (phase 142's
  subject); there is not one word about which test failed.
- No JSON, JUnit or HTML report is produced or uploaded, so there is no machine-readable record either.
- The only evidence a failing run leaves is the `playwright-failure-artifacts` upload — a ~13 MB trace
  bundle, retained 7 days, whose download requires an authenticated GitHub client.

The practical effect: **nobody can tell which test is failing without authenticating and downloading a
13 MB zip.** That is a large part of why nine consecutive red runs produced no investigation. A CI job
whose failures are that expensive to read is a job that gets ignored, which is the failure mode
`playwright-suite-in-ci.md` was written to prevent, arriving by a different route.

## What this phase will do

1. **Make the job self-reporting, before diagnosing anything.** Add Playwright's `github` reporter
   alongside `list` so failures appear as annotations on the run and the commit, and emit a report
   (`json` and/or `html`) uploaded as its own small artifact separate from the trace bundle. The goal is
   that the *name of the failing test and its error* are visible on the run page to anyone, with no
   download and no auth. Verify by reading a subsequent red run's annotations, not by assuming.
2. **Then read a real failing run** and identify the failing test(s) — the same discipline phase 140
   ended up proving out: read what actually failed rather than sampling or reasoning about it.
3. **Determine which of the three hypotheses in §3 it is.** Concretely: is it the same test every time,
   or a varying one? Does it fail on attempt 1 of 3 and stay failed, or degrade across attempts? Does it
   correlate with total suite runtime (resource pressure) or with position in the fixed run order (state
   leakage)?
4. **Fix it**, or — if it is genuinely environmental — make the job honest about that rather than
   leaving a permanently-amber signal. Quarantining a known-bad test with an explicit annotation is an
   acceptable outcome; leaving nine red runs unexplained is not.
5. **Re-run and confirm green across several consecutive runs**, not one. A job with this failure profile
   cannot be declared fixed by a single pass — three of the last twelve runs passed without anyone fixing
   anything.

## What this phase will not do

- **Re-litigate the suite's `workers: 1` / `fullyParallel: false` design.** `playwright-suite-in-ci.md`
  settled it deliberately and said to revisit only if CI runtime becomes a real problem. A ~5-minute
  suite is not that problem. If step 3 finds the *serial shared state* is the root cause, that changes
  the calculus and this boundary should be revisited then — but not before, and not as a guess.
- **Reduce or remove `retries: 2` as a first move.** It is load-bearing on a slow runner and its comment
  explains why. It may deserve revisiting once the actual failure is known; changing it first would only
  make the signal noisier while removing the evidence that the failure survives three attempts.
- **Touch `dotnet-integration`**, which is also red and is its own separate matter.
- **Add the component-test layer** (vitest/Testing Library) — still deferred, and tracked separately in
  `architecture/planning/todo/spa-has-no-component-test-layer.md`.

## Open questions to resolve during implementation

- Is the failing test the same one every run? If it varies, is there a common shape (all Monitoring, all
  post-run-trigger, all involving a spawned TaskRunner)?
- Does the failure predate the job itself — i.e. has this suite ever been green on CI for more than a run
  or two at a time? Worth reading further back than the 14 runs sampled here before assuming a
  regression, since the sample above contains no sustained green streak to regress *from*.
- Is `globalSetup`'s work implicated? It shells out to the CLI for `config library install duckdb` and to
  `docker exec … sqlcmd` for the test database; both are outside the Playwright timeout model and both
  are plausible sources of a slow start that pushes later tests over their limits.
- Does the standard GitHub runner have the headroom this topology needs at all? Three database containers
  plus four processes plus a browser is a lot for two cores, and if the answer is no, the fix is
  infrastructural (a larger runner, or fewer containers for this job) rather than a test change.

## What could not be done from this session, and why

Named rather than left as an implicit gap, since both would have shortened this considerably:

- **No local reproduction.** The suite needs the `docker compose` topology (`test-db.ts` reaches SQL
  Server by `docker exec dbdatasync-mssql-source sqlcmd`, i.e. by container name); Docker is not
  installed on this machine, so the suite cannot run here at all. Node 24 is present, so only the
  containers are missing.
- **No CI log access.** `gh` is not authenticated here and the unauthenticated logs endpoint answers
  `403`, so the failing step's output and the trace artifact were both out of reach. Everything above is
  from the public runs/jobs API. `gh auth login` is the one thing that unblocks step 2 immediately — and
  step 1 is worth doing regardless, so that the *next* person does not need it.
