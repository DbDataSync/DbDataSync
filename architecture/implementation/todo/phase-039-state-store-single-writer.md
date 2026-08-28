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
It should be a per-process random value passed on the command line and never written to disk.

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
