# Phase 11 — Dev Harness: Test Environment & Workload Tooling

**Status**: Complete
**Plan reference**: Requested directly — no prior backlog entry. Design settled in conversation: a C#
console tool with verbs, thin shell/batch launchers, a launch that goes all the way to a running API
and SPA, and a workload generator covering continuous load, drift injection, bulk seeding and
verification. This document was written as a plan before implementation and rewritten as a
retrospective after it, per `architecture/implementation/README.md`.

## Why this phase

Standing up something to actually watch DataSync work was a manual, seven-step README procedure, and
there was no way at all to put *ongoing* load through it — every test in the repo either seeds two
rows and asserts, or drives the browser once. That made the things this system was built for hard to
observe: incremental sync keeping up with a live table, a backfill repairing drift, a segmented reload
of something bigger than a handful of rows.

It also closes a real gap. `tests/DataSync.Web.Tests/test-db.ts` bootstraps its database by shelling
out to `docker exec datasync-mssql-source /opt/mssql-tools/bin/sqlcmd`, which hardcodes a path inside
the container image and can only ever reach the **source** container — so the Playwright suite,
despite the README describing two independent server instances, runs entirely against one. The
harness connects over TCP with `Microsoft.Data.SqlClient` and has neither limitation.

## What was built

**`tools/DataSync.DevHarness`** — a console project in a new top-level `tools/` solution folder,
referencing `DataSync.Core` for the config DTOs it posts and `Microsoft.Data.SqlClient` for everything
database-side. Hand-rolled argument parsing, matching `TaskRunnerOptions`' existing precedent rather
than taking a command-line-parser dependency.

| Verb | What it does |
| --- | --- |
| `up` | Containers up; wait until both instances accept a **client** connection; create and seed both databases; start the API and Vite as managed children; wait for `/api/health`; configure connections, replication and mapping over REST; tail both children until a stop signal. `--no-app`, `--no-containers`, `--keep-data`, `--rows`, `--api-port`, `--spa-port`, `--api-url`. |
| `down` | `docker compose down`, `--volumes` to discard data too. |
| `reset` | Recreate the databases and clear the scratch config repo, containers untouched. |
| `seed --rows N` | Bulk-load rows into the source via `SqlBulkCopy`. |
| `workload --rate N --duration T` | Continuous insert/update/delete transactions against the source (`90s`/`5m`/`1h` all parse). |
| `drift --rows N` | Corrupt the **target** directly: delete rows, alter values, insert rows the source never had. |
| `verify` | Merge-join source and target by key, naming every missing/extra/differing row. Exit code 0/1. |

**`scripts/dev-harness`** and **`scripts/dev-harness.cmd`** — thin launchers so the common case is
`scripts/dev-harness up`. Each runs `dotnet build` unconditionally and then `exec`s the built
assembly. See "A launcher that reimplemented MSBuild" below: they originally carried a hand-rolled
staleness check, which was both unnecessary and wrong.

**Scenario**: one `dev-sync` replication over `dbo.Orders`, whose columns are chosen so every segment
mode has something to bite on — an `INT` key (range/auto), a low-cardinality `Region` string (list), a
`DECIMAL` and a `DATETIME2` (so bucket arithmetic gets exercised on non-integer types, and the
watermark reader is usable without a second table).

## Decisions carried in from existing lessons

- **Scratch config repo under the OS temp directory**, not in the working tree — `playwright.config.ts`
  documents why: LibGit2Sharp's repo discovery walks up parent directories, so a scratch repo nested
  inside the project tree can be mistaken for the project's own repository.
- **The API child is started with `dotnet exec` on the built DLL, never `dotnet run --project`** —
  `dotnet run` spawns a wrapper, and killing the wrapper doesn't reliably kill the app, leaving an
  orphan holding the scratch repo.
- **Credentials passed to the API child as `CLRKERNEL_SECRET_*`**, matching how the Playwright suite
  and the integration tests work around the absence of an OS keychain.
- **Configuration goes through the REST API, not `ConfigRepository`** — standing up the environment
  exercises the same endpoints, validation and git auto-commit path an operator would use, so a break
  in them surfaces here rather than being bypassed.

## Real bugs found

1. **Seeding more than ~420 rows failed outright.** The multi-row parameterised INSERT batched 500
   rows × 5 columns = 2500 parameters, over SQL Server's hard limit of 2100 per request. It went
   unnoticed because every default in the tool seeds fewer than that; `seed --rows 2000` hit it
   immediately. Replaced with `SqlBulkCopy`, which has no such limit and is far faster at the sizes
   this verb exists to produce — 25,000 rows now load in about a second.
2. **A `SqlException` reaching the top level printed a stack trace** rather than a message. A database
   error is an operational fact about the environment, not a defect in the harness; it now reports
   like every other failure.
3. **No SIGTERM handling at all.** Shutdown was wired only to `Console.CancelKeyPress`, which covers
   Ctrl+C at an interactive terminal and nothing else. Since `up` owns child processes, missing a stop
   signal doesn't merely end the harness — it orphans a running API, a Vite server and any TaskRunner
   workers, holding ports and the scratch state database. `docker stop`, CI cancellation and most IDE
   stop buttons send SIGTERM, none of which would have been caught. Replaced with
   `PosixSignalRegistration` for SIGINT/SIGTERM/SIGQUIT, which is also independent of console
   initialisation and so works when output is redirected and there is no terminal.

## A harness design flaw, found by using it

`drift`'s phantom rows were originally inserted at `Id 900000000+`, chosen so they could never collide
with a real key. Running the obvious loop — `drift`, then an `Auto`-segmented backfill, then `verify` —
left the phantoms behind every time, and it looked like a product bug.

It wasn't. `Auto` derives its buckets from the source's real `MIN`/`MAX`, so a phantom above that range
falls outside every bucket, and a segment-scoped reconcile is *right* to leave it alone — that is
precisely the "out-of-segment rows untouched" guarantee Phase 9 exists to provide. The flaw was in the
harness making its own demo unconvergeable.

Phantoms are now placed in **gaps within the source's key range** (found with a `LEAD` window
function, one round trip), so any reload covering that range removes them. When the source has no gaps
— a freshly seeded, contiguous table — it falls back to keys above the range and says so explicitly,
naming the ids and explaining that only a Full segment will reach them.

## A launcher that reimplemented MSBuild

The launchers first shipped with hand-rolled change detection — compare each `*.cs` in the tool's own
directory against the built assembly's timestamp, and shell out to `dotnet build` if any was newer —
so that they could `dotnet exec` the built DLL rather than use `dotnet run`. The check had three
defects, one of them the exact failure it existed to prevent:

- It globbed only `tools/DataSync.DevHarness/*.cs`, so a change in **`DataSync.Core`** — a
  `ProjectReference` — left the harness running a stale binary with no indication anything was out of
  date.
- `-nt` is not POSIX, despite the `#!/usr/bin/env sh` shebang.
- The batch launcher had no equivalent and only built when the assembly was missing outright, so the
  two launchers behaved differently on the same repository.

The first replacement swung too far, to a bare `dotnet run --project … -- "$@"`, on the reasoning that
the wrapper-process concern recorded in `playwright.config.ts` "does not apply to a launcher, where
the OS delivers the signal." **That reasoning was wrong**, and a code review caught it. It holds only
for Ctrl+C at a terminal, which the kernel delivers to the whole foreground process group. It does not
hold for a programmatic kill of the pid a developer actually sees — `dotnet run` forks the app as a
child, so `pgrep` and an IDE's stop button both find the *wrapper*. Measured: `kill -9` on the wrapper
left the harness alive and reparented, still holding ports 5183/5173 and the scratch state database.
That is the same hazard `AppProcesses.StartApi` avoids by starting the API with `dotnet exec`.

The correct fix keeps both properties, and was available all along: **`dotnet build` unconditionally,
then `exec dotnet exec` the DLL.** MSBuild does the incremental decision it already does correctly
(including referenced projects), and `exec` leaves exactly one process, so the pid `ps` shows is the
harness. The mistake was never `dotnet exec` — it was the conditional wrapped around the build.

Verified on .NET 10 rather than assumed: `up` runs as a single process whose pid is the harness
itself; SIGTERM to that pid runs the full shutdown path and takes the API and Vite down with it;
`verify`'s exit code survives; build output goes to stderr so a verb's stdout stays pipeable; and a
compile error surfaces with a non-zero exit.

Two limits worth stating plainly rather than leaving implied:

- **SIGKILL cannot be handled by any design.** With the single-process launcher, `kill -9` does what
  the operator asked — the harness dies — but its API and Vite children are orphaned, because no
  cleanup can run. `SIGTERM` (what `docker stop`, CI cancellation and IDE stop buttons send) is the
  path that cleans up, and is the one that is tested.
- **Windows: building while `up` is running can fail.** A running `up` holds its own build output
  open, so editing harness or `DataSync.Core` sources mid-session and then invoking another verb from
  a second terminal — the workflow the README describes — will fail the build with MSB3027 (file in
  use). On Linux the same sequence succeeds, because MSBuild's copy unlinks first, which is why it was
  not caught here. The batch launcher documents it; stopping `up` before rebuilding avoids it. This
  has not been reproduced first-hand — no Windows machine was available — so it is recorded as a known
  risk, not a verified behaviour.

## How this was verified

Every verb was exercised against the real containers, end to end:

- `up` reached a browsable SPA with a configured, enabled replication; the scheduler picked it up and
  `verify` reported 100 rows identical.
- `workload --rate 20 --duration 15s` produced 153 inserts / 111 updates / 38 deletes; `verify`
  converged to 215 identical rows once the schedule caught up.
- `seed --rows 25000` loaded in ~1s; `verify` confirmed 25,215 identical rows across two servers.
- `drift --rows 60`, then an incremental run, then `verify` — **still 12 differences**, which is the
  point: Change Tracking has nothing to report, so incremental sync can never repair target-side
  drift. A Full backfill with `MsSqlMergeReconcile` then made `verify` pass.
- After the phantom-placement fix: `drift --rows 60` followed by a 6-bucket `Auto` backfill converged
  to 25,312 identical rows, with all six segment runs succeeding and non-overlapping labels.
- Stop-signal handling: SIGTERM to `workload` logged "shutting down", stopped early and printed its
  summary; SIGTERM to `up` took the API and Vite down with it, leaving **no orphaned processes**.
- Launchers: `up` runs as exactly one process (the pid `ps` shows is the harness, not a wrapper) and
  SIGTERM to it takes the API and Vite down with it; exit codes propagate (`verify` returns 1 on
  differences); a `DataSync.Core` edit triggers a rebuild; a compile error surfaces and exits
  non-zero; and stdout stays clean during a rebuild.
- `dotnet build` clean; the full non-integration suite (140 tests) unaffected by the new project.

One thing could not be verified here: SIGINT specifically. A non-interactive bash shell sets SIGINT to
ignored for background jobs and children inherit that, so the signal never reaches a backgrounded
process in this environment — a plain `sleep` survives it too. SIGTERM exercises the identical
shutdown path, so the mechanism is proven; only that one delivery route is untested.

## What's explicitly not built

A second scenario or multiple table mappings (the work queue's per-mapping model would be worth
exercising at breadth, not just depth); triggering backfills from the harness itself (`curl` or the UI
does it, and adding a verb would duplicate the SPA's form); any teardown of the scratch config repo on
`down`; Windows verification — the OS-specific paths (`cmd.exe` for npm, the `.cmd` launcher) are
written and reviewed but have only been run on Linux.

## Notes / things to revisit later

- `tests/DataSync.Web.Tests/test-db.ts` still uses `docker exec … sqlcmd` against the source container
  only. Now that a TCP-based bootstrap exists and is proven, that suite could use the harness instead
  and genuinely exercise two instances — worthwhile, but it is test-suite scope rather than tooling.
- `workload` holds the live key set in memory for the duration of a run. That is what keeps the hot
  path free of a round trip per transaction, but it means a very long run against a very large table
  grows a list proportional to the row count.
