# Phase 39 — One process owns the state store (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/state-store-concurrency.md`.

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

### TaskRunner talks to it over HTTP

The API is ASP.NET Core, TaskRunner already has an `HttpClient`, and there is already a SignalR channel
from the API to the SPA for live runs. A state endpoint is the same stack.

What has to move is small and already has seams: `TaskRunStore`, `WorkQueueStore`, `RunLockStore`,
`ChangeWatermarkStore` and `LogWriter` are five classes with narrow method surfaces, all constructed in
one place in `TaskRunner/Program.cs`. Each becomes an interface with two implementations — direct (the
API's own, in-process) and remote (TaskRunner's, over HTTP).

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

## What this phase does not build

DuckDB adoption. Re-evaluate when Quack is stable, by re-running the statement table above; if `UPDATE`,
`DELETE` and upsert work, the case is worth making properly, and phase 36's analytics are the thing that
would benefit.

A general-purpose remote state API. This is an internal protocol between a parent and the children it
spawned, on localhost, with a token the parent generates. It is not a public surface and should not be
documented as one.

## How to verify when built

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
- **What happens when the API is down?** Today a TaskRunner writes to the file regardless. It becomes a
  hard dependency, which is honest — the API spawned it — but should be a deliberate answer rather than
  a discovered one.
