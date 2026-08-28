# Phase 39 — One process owns the state store (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/state-store-concurrency.md`.

**Updated 2026-08-28** with two requirements that change the design rather than decorate it: the state
endpoint is **localhost-only**, and a TaskRunner that loses the API **spills its changes to disk and
exits cleanly** rather than failing. Both are below, after the core design.

**This replaces an earlier draft of phase 39 that had the requirement wrong.** That draft framed the
work as "measure whether SQLite contention is real, and enable WAL if it is". The actual requirement is
architectural and is not conditional on a measurement:

> There is no scenario where I want multiple processes trying to write to the same state tracking file.
> They need to all go through a single process.

Contention is a symptom. Multiple writers is the thing to remove.

## Where we are

`grep -l "new StateDatabase("` returns exactly two: `DataSync.Api/Program.cs` and
`DataSync.TaskRunner/Program.cs`. The API opens the file, and **every TaskRunner it spawns opens the
same file** — `ProcessSupervisor` passes `--state-db <path>` to each child. With `DegreeOfParallelism`
defaulting to 4 and one process per replication, a busy installation has many processes writing one
SQLite file.

That is why `StateDatabase` carries `busy_timeout`, why `SqliteRetry` exists, and why WAL was turned off
after cross-process coordination "was observed to be unreliable". Three mitigations for a design
decision, rather than a fix for it.

## What was measured about DuckDB and Quack

The re-evaluation asked for. Run against `DuckDB.NET.Data.Full` **1.5.5**, with a server process and
three concurrent client processes.

### The architecture works, today

```
SERVER: listening; state file owned by this process only
CLIENT 1: attached over the network — no file lock taken
CLIENT 2: attached over the network — no file lock taken
CLIENT 3: attached over the network — no file lock taken
```

`CALL quack_serve('quack:localhost', token = '…')` on the owning process; `CREATE SECRET (TYPE quack,
TOKEN '…')` then `ATTACH 'quack:localhost'` on each client. Exactly the shape the requirement describes.

**600 concurrent appends across three client processes, zero conflicts.** That matches DuckDB's
documented model — MVCC plus optimistic concurrency, and *"appends will never conflict, even on the same
table"* — and appends are most of what this store does.

DuckDB also **enforces** the single-writer-process rule: a second process opening the file read-write is
refused outright (`Could not set lock on file … Conflicting lock is held`). The mistake the current
design makes is not expressible.

And **the storage format is separable, as expected**: `INSTALL sqlite; ATTACH 'x.sqlite' (TYPE sqlite)`
gives a real SQLite file written through DuckDB, with DuckDB's SQL over it — window functions and all.

### But three statements DataSync depends on do not work over Quack

| statement | over an attached Quack database |
| --- | --- |
| `INSERT` (single and multi-row) | **OK** |
| `CREATE TABLE` | **OK** |
| `BEGIN … INSERT … COMMIT` | **OK** |
| `UPDATE` | **FAILS** — `Binder Error: Can only update base table` |
| `DELETE` | **FAILS** — `Binder Error: Can only delete from base table` |
| `INSERT … ON CONFLICT DO UPDATE` | **FAILS** — `Not implemented Error: GetStorageInfo not implemented yet` |

Not conflicts — unimplemented. Qualifying the table, or `USE remote` first, makes no difference.

Against what this store actually does:

| store | needs |
| --- | --- |
| `WorkQueueStore` | 3 × `UPDATE` — claiming and completing work items |
| `TaskRunStore` | 2 × `UPDATE` — run status transitions |
| `RunLockStore` | `DELETE` + upsert — releasing and acquiring locks |
| `ChangeWatermarkStore` | upsert |
| `LogWriter` | `INSERT` only |

**Four of the five stores cannot be expressed over Quack today.** Only the log writer could move.

### Throughput, for when it does work

| pattern | rate |
| --- | --- |
| single-row `INSERT`, one per round trip | ~400/s per client, ~1,100/s across three |
| 2,000 rows in one statement | **3 ms** |

Three orders of magnitude apart, so the cost is the HTTP round trip and not the engine. Any design on
this protocol wants batched writes — which suits `LogWriter` and does not suit a lock acquisition that
has to be immediate.

### Verdict

**Not adoptable yet, for a specific and re-testable reason.** Quack is beta, shipped in DuckDB 1.5.3
(May 2026), with **stable planned for September 2026** and the FAQ warning of breaking changes to the
protocol, function names and defaults until then.

That is a month away. The right posture is not to design around a beta whose gaps are exactly the
statements we need — it is to build the architecture the requirement asks for in a way that does not
care which engine is underneath, and re-run the table above when Quack is stable.

## What this phase builds

**A single owning process, reached over a network protocol — with the store behind an interface.**

### The owning process is the API, not a new one

DataSync already has a long-lived process that owns everything else and **already spawns TaskRunner**.
Making it the state owner adds no deployment surface, no second thing to supervise, and no new port.
`ProcessSupervisor` already passes configuration to each child; it passes an endpoint and a token
instead of a file path.

### The endpoint is localhost-only, two ways

The state API mutates run state, releases locks and reads logs. It is an internal channel between a
parent process and the children it spawned, on one machine, and it should be impossible to reach from
anywhere else.

**Both defences, because they fail differently:**

- **A separate Kestrel endpoint bound to loopback** — `ListenLocalhost(statePort)`, distinct from the
  port the SPA and public API use. The OS refuses the connection; no application code has to be correct
  for that to hold. It also means the state API can never be exposed by someone putting the main API
  behind a reverse proxy or binding it to `0.0.0.0`, which is the realistic way this would go wrong.
- **A middleware check on the state endpoints** — reject unless `HttpContext.Connection.RemoteIpAddress`
  `IsLoopback`. This is what survives a misconfiguration of the first: an operator who edits the binding,
  or a future refactor that merges the two endpoints back together.

**Loopback is not the same as trusted**, and the design should not pretend otherwise: any process
belonging to any user on that host can reach `127.0.0.1`. So the token the parent generates per child
is not belt-and-braces, it is the part that distinguishes *our* TaskRunner from anything else local.
A per-process random value, never written to disk.

**The token goes in the environment, not on the command line.** On Linux a process's command line is
world-readable and its environment is not:

```
-r--r--r--  /proc/self/cmdline
-r--------  /proc/self/environ
```

So a token in `ArgumentList` is visible to every local user through `ps aux` — which is precisely the
local-user threat the token exists to answer, handed straight back. The same holds on Windows, where a
command line is readable through `Get-CimInstance Win32_Process` while the environment block is not.

`ProcessSupervisor` already sets `UseShellExecute = false`, which is the prerequisite for
`ProcessStartInfo.Environment`, so this is `startInfo.Environment[…] = token` beside the existing
`ArgumentList` calls rather than a change to how the child is launched. It also matches how this
codebase already passes secrets between processes — `SecretStore`'s `CLRKERNEL_SECRET_*` fallback,
which the Playwright fixture and the integration tests both use.

Two things that follow and are worth stating rather than discovering:

- **The environment is inherited.** Anything TaskRunner spawns sees the token. It spawns nothing today,
  and that is a constraint to keep rather than an observation.
- **Root and the same user can still read it.** This is defence against *other* local users, which is
  exactly the gap loopback leaves, and not against a compromised host. Crash dumps and some diagnostic
  tooling also capture environment blocks — so the token stays short-lived and per-process, and is not
  reused across restarts.

### TaskRunner talks to it over HTTP

The API is ASP.NET Core, TaskRunner already has an `HttpClient`, and there is already a SignalR channel
from the API to the SPA for live runs. A state endpoint is the same stack.

What has to move is small and already has seams: `TaskRunStore`, `WorkQueueStore`, `RunLockStore`,
`ChangeWatermarkStore` and `LogWriter` are five classes with narrow method surfaces, all constructed in
one place in `TaskRunner/Program.cs`. Each becomes an interface with two implementations — direct (the
API's own, in-process) and remote (TaskRunner's, over HTTP).

### When the API is unavailable: spill, then stop

A local SQLite write always succeeded or failed. A remote one can find the other end gone — the API
restarted, was upgraded, or crashed — while a run is in flight and has real work to record. Losing that
record is not acceptable: a run that moved rows and could not say so leaves a watermark unadvanced and
an operator with no trace.

So a TaskRunner that loses the API **finishes what it safely can, writes its unrecorded changes to
disk, and exits cleanly.**

#### The grace period

A failed state call is not immediately fatal. The runner retries with backoff for a configured grace
period (`--state-grace`, defaulting to something on the order of a minute — the API is its own parent
and a restart should be far quicker than that).

During the grace period it **stops claiming new work items** but lets the unit of work in flight finish.
That unit is already transactional per segment, and its watermark is only persisted on success, so
letting it complete is both safe and strictly better than abandoning work already paid for.

If the API returns within the grace period, everything buffered is flushed and the run continues
normally. This is the common case and it should leave no trace beyond a warning in the log.

#### The journal

If the grace period expires, the runner writes a **journal** and exits with a distinct exit code
(alongside `ExitCode.ConfigError`'s existing family), so the parent — when it returns — can tell this
apart from a crash.

- **JSON Lines, not a JSON document.** One state change per line, appended and flushed. A file truncated
  by a kill or a full disk loses only its last line; the rest still replays. A single JSON object would
  be unparseable.
- **One file per run**, under `<state-dir>/pending/<replication>/<runId>.jsonl`, so recovery can be
  scoped per replication exactly as the requirement asks.
- Each line carries the run id, a per-run monotonic sequence, a timestamp and the change itself.

**What may be spilled is the discipline that makes this safe**, and it is narrower than "everything
buffered":

| change | spillable? |
| --- | --- |
| log lines | always — they are observations, not state |
| a run's terminal status | only if the run actually reached it |
| a watermark advance | **only if the target write committed.** This is the watermark-on-success-only rule, and the runner is the only party that knows the answer |
| a completed work item | only if it completed |
| a *claimed but unfinished* work item | spilled as **released**, never as completed, so it can be re-claimed |
| a held run lock | spilled as **released** |

A runner that cannot write the journal either — a full disk — has nothing left to do but log loudly and
exit non-zero. That is a real loss and should say so rather than exiting quietly.

#### Recovery, before anything else runs

**Before the scheduler starts any work for a replication**, the API drains
`<state-dir>/pending/<replication>/`:

1. read each journal, oldest first
2. apply its changes **in one transaction**, in line order
3. delete the file
4. **log it at warning level** — `Applied 47 state changes recovered from disk for run 8f2c… ; the API
   was unavailable while it ran` — because this is an unusual case and should never pass unremarked in
   a normal-looking log

Ordering across files is by journal timestamp. Within a file it is line order. Files for different
mappings are independent, so cross-file ordering only matters for the work queue.

**Replay must be idempotent**, because the API can crash mid-apply and come back to the same file.
Terminal statuses and watermarks are naturally idempotent (they set a value). Log lines are not, and
they are the highest-volume entry — so `Logs` gains a per-run sequence and its insert becomes
`ON CONFLICT DO NOTHING`, which makes a partial replay safe rather than duplicating an operator's log.

#### The conflict recovery has to resolve

The API may already have marked the run **Failed**, because it saw its child exit. The journal may say
it **Succeeded**. Both observations are honest and they disagree.

**The journal wins**, and the reason is asymmetric knowledge: the API observed a process exiting, while
the runner observed whether the rows actually landed. But the correction must be *visible* — recovery
logs that it changed an outcome, rather than quietly overwriting one.

The genuinely dangerous field, the watermark, is safe by construction: it is only in the journal if the
write committed.

Three things that need care rather than code:

- **`RunLockStore` is the correctness-critical one.** It is what stops two processes running the same
  mapping. Moving it behind a network call makes the lock's holder a remote party, so the API has to
  release locks held by a TaskRunner that died — which it can, because it is the parent and already
  watches for exit.
- **`LogWriter` is the highest-rate writer**, one row per log line. It should batch, which it does not
  need to today because the write is local. This is the change most likely to alter behaviour under
  load rather than just move it.
- **Failure semantics change.** A local SQLite write fails or succeeds; a network write can time out
  with the result unknown. Every state write needs to be idempotent or retried safely — `SqliteRetry`
  already establishes the retry habit, but "unknown outcome" is a new case.

### Why this is worth doing regardless of engine

Because it is **the requirement**, and because it makes the engine question a detail. With one process
owning the store:

- SQLite's multi-process problems disappear, along with `busy_timeout`, `SqliteRetry` and the WAL
  question — the whole reason that comment exists.
- WAL becomes available and uncontroversial, since there is one process.
- Swapping in DuckDB later — for its SQL, over either format — is a change behind one interface in one
  process, not a change to how five stores are reached from two processes.

## Priority

**First.** Moved ahead of the rest of the queue on 2026-08-28 — the build order is recorded in
`architecture/implementation/README.md`.

## What this phase does not build

DuckDB adoption. Re-evaluate when Quack is stable, by re-running the statement table above; if `UPDATE`,
`DELETE` and upsert work, the case is worth making properly, and phase 36's analytics are the thing that
would benefit.

A general-purpose remote state API. This is an internal protocol between a parent and the children it
spawned, on localhost, with a token the parent generates. It is not a public surface and should not be
documented as one.

## How to verify when built

- **The token is not in the child's command line.** Assert it directly against the spawned process —
  `ProcessStartInfo.ArgumentList` carries no secret — because this is the kind of thing a later
  "just add a flag" refactor undoes silently.
- **The state endpoint refuses a non-loopback client**, tested against both defences independently:
  bound to loopback (connection refused), and the middleware (rejected even if the binding is widened).
- **Journal round trip**: kill the API mid-run, confirm the runner spills and exits with the distinct
  code, restart, and confirm the changes are applied, the file removed, and the warning logged.
- **A claimed-but-unfinished work item comes back as released**, not completed — so it is re-claimed
  rather than silently dropped.
- **A watermark is not spilled for a write that did not commit.** The test that protects the invariant
  the whole run model rests on.
- **Replay is idempotent**: apply the same journal twice, and the log has no duplicates and the run has
  one outcome.
- **The journal wins a conflict, visibly**: API marks the run failed, journal says succeeded, recovery
  corrects it and says so.
- **No TaskRunner opens the state file.** Assert it structurally — `new StateDatabase(` reachable from
  exactly one project — because that is the requirement and everything else is consequence.
- The existing `DataSync.State.Tests` suite green against the direct implementation, unchanged.
- The same suite green against the remote implementation, over a real loopback API.
- `RunLockStore`: a TaskRunner killed mid-run has its lock released by the API, and another run can
  claim the mapping. This is the failure that currently cannot happen and now can.
- A network write that times out after the API applied it, retried, not double-applied.
- `tools/dev-harness up` end to end, which is the real multi-process configuration.
- Throughput: `LogWriter` under a workload, before and after, since it is the one that gets slower.

## Open questions

- **Does the TaskRunner still need SQLite at all?** If every write is remote, it does not, and the
  dependency can go — which also removes the `DuckDB.NET` question from that process entirely.
- ~~**What happens when the API is down?**~~ **Answered above**: grace period, then spill to a JSON Lines
  journal and exit cleanly; the API drains it per replication before scheduling anything new, and says
  loudly that it did.
- **How long is the grace period, really?** A minute is a guess. It should be long enough to cover an
  API restart and short enough that a TaskRunner does not sit idle holding a lock. Measurable once
  there is a restart to measure.
- **Should a spilled journal ever be applied by a *different* API instance** than the one that spawned
  the runner? On one host with one API, no — but it is the question that decides whether the journal
  needs to identify who wrote it.

---

# Retrospective

Built as planned. The design held; what it cost was three real bugs, and all three were found by the
things the plan said to verify rather than by the things it said to build. Two of them predate this
phase and would have been just as wrong before it.

## The structural test found the hole the behavioural ones could not

`new StateDatabase(` reachable from exactly one project was written as a formality — the requirement
restated as an assertion. It failed immediately: `RunnerStateFactory` still opened the file directly
when no endpoint was supplied, kept as a fallback for "running a worker by hand".

Running a worker by hand *while the API is up* is the second writer this entire phase exists to
prevent. The fallback is gone, and a runner without an endpoint now refuses to start with a message
saying why — at startup, rather than later with corruption.

Every behavioural test would have stayed green with that fallback in place, because nothing exercised
it. The assertion that catches it is the one that says what the requirement is instead of what the
system does.

## A killed worker used to strand its work, permanently

Killing the API mid-backfill in the dev harness left two work items `Claimed` forever. Since
`UX_WorkQueue_InFlight` covers `Claimed` and `Running`, their mapping could never be enqueued again:
the replication stopped, and stopped *silently* — no failed run, no error, just nothing.

Reconciliation released the lock and the run row, and never touched the queue. Fixing the release was
half of it; the other half was the trigger. It asked "which runs are `Running` with a dead pid", and
an item claimed by a worker that died before starting it has **no run to be found by** — the exact
case the harness produced. It now asks the queue which replications hold in-flight work and returns
those with nothing alive working on them, logged at warning.

This bug predates phase 39 entirely. It surfaced here because this was the first time anything killed
an API mid-run and then looked at what was left behind.

## Returning from `Main` took the consumers with it

`await producer; await Task.WhenAll(consumers);` loses the second line the moment the first throws —
and the owner going away is precisely when the first throws. The consumers were left as unawaited
tasks, and the process returned from `Main` and terminated them mid-item, along with the outcomes they
were in the middle of recording.

The journal cannot help with work whose recording never gets a chance to run. The producer's failure
is now captured, the consumers awaited, and only then rethrown. A consumer that cannot reach the owner
stops taking work rather than recording an outcome it is in no position to observe — which is why
`StateOwnerUnavailableException` is the one exception `ProcessWorkItemAsync` does not convert to
`MarkFailed`.

## Prerequisites and outcomes, and why the split is the whole design

Everything else follows from one distinction. A **prerequisite** — claiming an item, taking a lock,
reading a watermark — fails when the owner is unreachable, because there is no outcome to preserve and
proceeding would mean assuming a claim nobody granted. An **outcome** — a completion, a release, a
watermark, a log line — is written to a journal, because the work is already done and losing it with
the process is the thing worth avoiding.

`JournalOperation` has no members for prerequisites, and that absence is deliberate: there is no way to
spell "replay a claim the owner never gave me".

## A separate server, not another route

The state endpoint is its own Kestrel server bound to `127.0.0.1`, constructed in `StateHost`. The main
API is the thing an operator puts behind a reverse proxy, binds to `0.0.0.0`, or exposes through a
container port map, and anything sharing its pipeline inherits all of those decisions. Serving these
routes from a separate server means "what is reachable from off-box" has an answer that does not depend
on how the main API was configured.

It also made the integration tests honest. `WebApplicationFactory` replaces the main server with an
in-memory one, so a spawned child could never have reached a route on it — the tests that trigger real
runs would have had to be given a local fallback, which is the very thing being removed. `StateHost` is
a real socket in those tests, and the children really do talk HTTP to it.

The port is ephemeral by default. Nothing needs to know it in advance: the only clients are children
this process spawns, and each is told the address actually bound. A fixed default would buy nothing and
cost a collision every time two instances ran on one host.

## The token goes in the environment

`/proc/<pid>/cmdline` is world-readable and `/proc/<pid>/environ` is not, so a token in an argument is
visible to every local user through `ps` — precisely the threat it exists to answer. Windows has the
same asymmetry. `BuildStartInfo` was extracted so this can be asserted against the value rather than
trusted to survive the next "just add a flag" refactor.

Loopback is not a trust boundary. Any local process can reach `127.0.0.1`; the binding keeps remote
clients out and the token is what distinguishes this API's own children from everything else on the
host. Both are tested, independently.

## Recovered log lines needed two things they did not have

A timestamp, because a recovered run whose every line is stamped with the moment of recovery says
nothing about when anything happened. And an idempotency key, because recovery applies a journal and
then deletes it, and a process dying between those two steps replays the whole file. Every other
operation was already last-writer-wins; appending a log line was the exception, so it now carries a
`<runId>:<sequence>` source key with a unique index over it — `NULL` for every live line, and `NULL` is
distinct from `NULL` in SQLite, so two genuinely identical live lines are still two lines.

## Verification

- `StateJournalTests` (4) — ordered replay with payloads, a truncated final line skipped with the rest
  surviving, no file *or directory* when nothing is appended, and per-replication grouping.
- `RemoteRunnerStateTests` (9) — a prerequisite failing after the grace period with nothing spilled, a
  prerequisite succeeding when the owner returns, outcomes journalled, an abandoned item journalled as
  `ReleaseClaim` and never `MarkDone`, a 4xx raised rather than spilled, no re-spending the grace
  period per call, logs batched into one request, and logs journalled one entry per line.
- `RunnerStateEndpointTests` (6) — bound to loopback, a request with the token served, missing and
  wrong tokens refused, the guard refusing a non-loopback client *with* a valid token, and the whole
  `IRunnerState` surface over a real socket checked against what the owner recorded.
- `JournalRecoveryTests` (6) — silent when there is nothing, applied/removed/logged at warning, a
  double apply leaving one outcome and one of each log line, the journal winning a conflict visibly, a
  claimed-but-unfinished item coming back claimable, and a truncated journal applied as far as it goes.
- `StateOwnershipTests` (2) — `new StateDatabase(` reachable from exactly one project, and no token in
  the child's `ArgumentList`.
- `RunnerWithoutItsOwnerTests` (3) — the runner as a real process: refusing to start without an
  endpoint (and creating no state file), exiting with the distinct code when the owner never answers,
  and refusing an endpoint with no token in the environment.
- `WorkQueueStoreTests` (+4) — release returning an in-flight item, leaving a finished one alone, also
  returning one claimed but never started, and reporting which replications hold in-flight work.
- `RunExecutorIntegrationTests` (+1) — against real SQL Server: a pass that reads changes and then
  fails to write them leaves the watermark where it was, and the next successful pass still delivers
  those rows.
- Dev harness, end to end and twice over. First: 305k rows seeded, a live workload, source and target
  matching row by row. Then the round trip — froze the API mid-pass, the runner spilled 8 entries
  (5 log lines, `CompleteRun` 2560/2560, `MarkDone`, `ReleaseLock`), reported the grace period expiring
  and exited; on restart the API applied them, removed the file, said so at warning level, and the run
  reads Succeeded with its log lines carrying the timestamps of the work.
- Full .NET suite green: 531 tests. Playwright: 17 green.

## Throughput

`LogWriter` is the one write that got slower, and it is the highest-rate one — a line at a time.
Measured over 20,000 lines: **15,700/s** writing directly, **12,900/s** through the endpoint over real
Kestrel. An 18% cost, against a workload that logs a handful of lines per mapping per pass.

Log lines are batched 200 to a request; a round trip each would have been the difference between
hundreds a second and tens of thousands. The endpoint deliberately does *not* flush per arriving batch:
`LogWriter` already batches on its own threshold and a two-second timer, which is exactly what a runner
writing in-process used to get.

## Open questions, two answered

- ~~**Does the TaskRunner still need SQLite at all?**~~ It no longer opens the file, and the structural
  test keeps it that way. It still *references* `DataSync.State` for the shared types (`RunKind`,
  `WorkItem`, `IRunnerState`, `StateJournal`) and so still links `Microsoft.Data.Sqlite`. Splitting the
  types out from the stores would drop the dependency; worth doing when something else needs it.
- ~~**Should a spilled journal be applied by a *different* API instance?**~~ In practice it always is —
  the instance that applies it is by definition a later one than the instance that went away. Nothing
  in the journal identifies who wrote it, and nothing needs to on one host with one API. It becomes a
  real question the day two APIs share a state file, which is the thing this phase forbids.
- **How long is the grace period, really?** Still a guess at 60 seconds, and now measurable: an API
  restart in the harness takes about 10, so 60 is generous. What it costs is a killed API leaving a
  runner idle for up to a minute holding a lock. Worth revisiting with a real restart to measure.
- **A worker left by a previous API instance** that has claimed work and not yet started it can have
  that claim released underneath it. Narrow, and strictly better than a replication that never runs
  again — but it is the residual case, and the honest fix is a worker heartbeat rather than process
  liveness, which is the same follow-on phase 8 already names.

## What this unblocks

The state file has one writer, which is what phase 40's provisioning and phase 36's run metrics both
assume when they add writes. And the analytics question that started this — DuckDB, Quack, a richer
query surface over run history — is now a question about what the *owner* does with its own file,
rather than a question about concurrent access.
