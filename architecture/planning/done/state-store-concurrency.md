# State-store concurrency (formerly: replace SQLite with DuckDB?)

**Status: resolved 2026-08-28 — see Outcome at the end.**

## The question

Investigate replacing SQLite (`DataSync.State`, the central state store) with DuckDB — using
`DuckDB.NET.Data.Full` and the quack protocol.

## The motivation (2026-08-28)

The goal is to reduce locking and improve performance for analytics queries, so that reads for
analytics/reporting don't block the writes TaskRunner does mid-run. SQLite's writer lock is
database-wide (or table-wide in WAL mode for the writer, but readers can still be starved under
sustained write load); the concern is a dashboard or metrics query run against `DataSync.State`
stalling — or being stalled by — a TaskRunner process mid-write.

## Why no implementation phase yet

That motivation narrows the question but does not yet answer it. Still open, and still needed before
a phase doc:

- Whether SQLite's WAL mode (which already allows concurrent readers alongside a single writer)
  already solves this, and current code isn't using it or isn't configured for it.
- Whether the actual contention is real (measured lock waits / query latency under concurrent
  TaskRunner writes) or theoretical.
- Whether DuckDB's `DuckDB.NET.Data.Full` + quack protocol offers meaningfully better concurrent
  read/write isolation for this access pattern than SQLite WAL — DuckDB is a columnar analytical
  engine, generally optimized for scan-heavy reads over row-at-a-time write concurrency, which is the
  opposite shape from TaskRunner's frequent small writes.
- Whether this is really the same problem as `planning/done/run-metrics.md` (phase 36) — if the
  analytics queries in question are the ones that phase already made queryable, that phase may have
  already relieved the pressure this is meant to solve.

**Next step**: check whether SQLite WAL mode is already enabled for `DataSync.State`; if not, that's
the cheap first fix to try before evaluating a storage engine swap. If it's already on and contention
still measurably exists, benchmark DuckDB against it for this specific write-mostly, small-dataset
access pattern.

---

# Outcome — resolved 2026-08-28, and re-framed the same day

**The first outcome written here had the requirement wrong**, and is replaced. It read the motivation as
"analytics reads contend with TaskRunner writes" and proposed measuring that contention, with WAL as the
likely fix. The requirement is architectural and is not conditional on a measurement:

> There is no scenario where I want multiple processes trying to write to the same state tracking file.
> They need to all go through a single process, with other processes applying state changes over a
> network protocol.

Contention is a symptom of multiple writers. Removing the writers is the fix, and measuring the symptom
first would only have decided how urgent it was.

The work is `implementation/todo/phase-039-state-store-single-writer.md`.

## What was measured about DuckDB and Quack

Investigated properly against `DuckDB.NET.Data.Full` 1.5.5, with a server process and three concurrent
client processes.

**The architecture works today.** `CALL quack_serve('quack:localhost', token = …)` on the owner;
`CREATE SECRET (TYPE quack, …)` + `ATTACH 'quack:localhost'` on each client. Three client processes
attached with no file lock and performed **600 concurrent appends with zero conflicts** — matching
DuckDB's documented model, where *"appends will never conflict, even on the same table"*.

**Two of the premises hold.** DuckDB does *enforce* single-writer-process — a second process opening the
file read-write is refused (`Could not set lock on file`), so the mistake the current design makes is
not expressible. And the storage format really is separable: `INSTALL sqlite; ATTACH 'x.sqlite' (TYPE
sqlite)` writes a genuine SQLite file with DuckDB's SQL over it, window functions included.

**But three statements do not work over Quack**, and they are the ones this store lives on:

| statement | over an attached Quack database |
| --- | --- |
| `INSERT`, `CREATE TABLE`, `BEGIN…COMMIT` | OK |
| `UPDATE` | `Binder Error: Can only update base table` |
| `DELETE` | `Binder Error: Can only delete from base table` |
| `INSERT … ON CONFLICT DO UPDATE` | `Not implemented Error: GetStorageInfo not implemented yet` |

Unimplemented rather than conflicting; qualifying the table or `USE remote` makes no difference. Four of
the five stores — work queue, task runs, run locks, watermarks — need `UPDATE`, `DELETE` or upsert. Only
`LogWriter` could move.

**Throughput**: ~400 single-row inserts/sec per client (~1,100/sec across three) against **2,000 rows in
one statement in 3 ms**. Three orders of magnitude, so the cost is the HTTP round trip rather than the
engine — any design here wants batched writes.

## Verdict

**Not adoptable yet, for a specific and re-testable reason rather than a maturity worry.** Quack is beta,
shipped in DuckDB 1.5.3 (May 2026), with **stable planned for September 2026** and the FAQ warning of
breaking changes to the protocol, function names and defaults until then.

So phase 39 builds the architecture the requirement asks for — the API as the single owner, TaskRunner
applying changes over HTTP — behind an interface, which makes the engine a detail rather than a
prerequisite. With one process owning the store, SQLite's multi-process problems disappear along with
`busy_timeout`, `SqliteRetry` and the WAL question; and swapping in DuckDB later becomes a change behind
one interface in one process.

**Re-evaluate when Quack is stable** by re-running the statement table above. If `UPDATE`, `DELETE` and
upsert work, the case for DuckDB's SQL over the analytics in phase 36 is worth making properly.

Sources: [Concurrency](https://duckdb.org/docs/current/connect/concurrency),
[Quack FAQ](https://duckdb.org/quack/faq),
[Quack announcement](https://duckdb.org/2026/05/12/quack-remote-protocol),
[Quack extension](https://duckdb.org/docs/current/core_extensions/quack).

