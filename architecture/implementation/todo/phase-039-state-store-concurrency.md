# Phase 39 — State-store concurrency: measure, then WAL (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/state-store-concurrency.md`, formerly
`replace-sqlite-with-duckdb.md`. Renamed because the investigation its own "next step" called for was
done, and it changed what the work is.

## What the investigation found

The doc's next step was *"check whether SQLite WAL mode is already enabled for `DataSync.State`; if not,
that's the cheap first fix to try before evaluating a storage engine swap."*

**It is not enabled, and it is off on purpose.** `StateDatabase.OpenRawConnection` carries the reason:

> Deliberately not WAL mode: WAL relies on a shared-memory (-shm) file to coordinate multiple processes
> reading/writing the same database… **In this project's sandboxed dev environment that coordination was
> observed to be unreliable** across the API process and its spawned DataSync.TaskRunner child processes.

Two things follow, and they point in opposite directions.

### WAL works in this environment now

```
journal_mode -> wal    (accepted, on the repo's own filesystem and on /tmp)
```

The documented reason is **environment-specific and dated**. It records an observation about a sandbox,
not a property of SQLite or of any deployment filesystem. That does not make the observation wrong; it
makes it stale enough to re-test rather than inherit.

### But the contention is narrower than the motivation assumes

The doc's concern is *"a dashboard or metrics query… stalling — or being stalled by — a TaskRunner
process mid-write."* Tested directly, in both modes, with a writer holding an open transaction:

```
journal_mode=delete  -> analytics read during an open write: OK
journal_mode=wal     -> analytics read during an open write: OK
```

Rollback-journal mode takes its EXCLUSIVE lock **at commit**, not for the life of the transaction. A
writer that is mid-transaction holds RESERVED, which still permits readers. So the picture of "a
TaskRunner holds the database while it works" is not what happens — readers are blocked only for the
duration of each commit flush, and `busy_timeout` plus `SqliteRetry` already absorb that as latency
rather than as errors.

**That is a negative result and it is the important one**: it means there may be no problem to fix, and
it makes measuring first mandatory rather than diligent.

## What this phase builds

### Part 1 — Measure

A benchmark in `tools/DataSync.Benchmarks` (which exists for exactly this) putting a realistic write
load through `TaskRunStore`/`LogWriter` from one process while running phase 36's aggregate query from
another, reporting:

- read latency distribution under write load, both journal modes
- `SQLITE_BUSY` counts and `SqliteRetry` retry counts
- the same at the write rates a real replication produces — `LogWriter` per log line is the highest-rate
  writer here, not `TaskRuns`

**If the numbers say there is no contention worth fixing, the phase stops here** and says so. That is a
success, not a failure: it retires a question that would otherwise keep being asked.

### Part 2 — Journal mode as a probed choice, if the numbers say so

If WAL measurably helps, the fix is a one-line PRAGMA, not an engine. What makes it a phase rather than
a one-liner is that the original concern was real on *some* filesystem:

- **Probe at startup**: attempt `PRAGMA journal_mode=WAL`, verify it took, and fall back to the current
  behaviour if it did not — SQLite reports the mode it actually adopted, so this is checkable rather
  than hopeful.
- **Configurable override**, so an operator on a filesystem where WAL misbehaves (network shares are
  the classic case, and `-shm` is the reason) can pin it.
- **Say which mode is in use** where the state database is otherwise described, so a support
  conversation starts from fact.

The comment in `StateDatabase` gets replaced with what is actually true after measuring, rather than an
observation about a sandbox that no longer exists.

## Why not DuckDB

Not ruled out, but nothing here points at it, and the doc's own analysis says why: DuckDB is a columnar
analytical engine optimised for scan-heavy reads, and this is a **write-mostly store of small rows** —
run records and log lines, appended at rate, read in modest aggregates. That is the shape SQLite is
best at and the shape DuckDB is worst at.

The doc also raises the possibility that this is really phase 36's problem. It may be: if the analytics
queries in question are the ones phase 36 makes queryable, that phase's index is the change that
relieves the pressure, and it lands first.

**A storage-engine swap is the largest available answer to a problem not yet shown to exist.** If the
measurement in part 1 finds real contention that WAL does not fix, that is the point to re-open it —
with numbers.

## How to verify when built

- The benchmark, with its numbers recorded in the retrospective for both journal modes. That is the
  deliverable even if no code changes.
- If WAL is adopted: the existing suite green under it, particularly `DataSync.State.Tests` and the
  TaskRunner integration tests, which are the cross-process case the original comment worried about.
- The probe falling back cleanly when WAL cannot be established — testable by pointing the state
  database at a filesystem that refuses it, or by faking the PRAGMA's response.
- A run under `tools/dev-harness` with the API and a spawned TaskRunner both writing, which is the
  exact configuration the original observation came from.

## Open questions

- **Where does the read load actually come from?** Today: the SPA polling run history, and phase 36's
  metrics when it lands. Neither is heavy. If the motivating scenario is something else — an external
  BI tool pointed at the file — that changes the answer, and it is worth asking before measuring the
  wrong thing.
