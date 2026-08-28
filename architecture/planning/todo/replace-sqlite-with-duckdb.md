# Replace SQLite with DuckDB?

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
