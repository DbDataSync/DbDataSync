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

# Outcome — resolved 2026-08-28

The "next step" above was carried out, and it changed what the work is. Now
`implementation/todo/phase-039-state-store-concurrency.md`.

**WAL is not enabled, and it is off on purpose.** `StateDatabase.OpenRawConnection` documents why: WAL
needs a `-shm` file to coordinate processes, and that coordination "was observed to be unreliable" in
this project's sandboxed dev environment, between the API and its spawned TaskRunner children. So the
cheap first fix was already considered and rejected — for an environment-specific reason, dated, and
worth re-testing rather than inheriting. Tested now: **WAL is accepted on this filesystem.**

**But the motivation is narrower than it looks, and this is the important finding.** The concern was a
metrics query stalling or being stalled by a TaskRunner mid-write. Measured directly with a writer
holding an open transaction, in both modes:

```
journal_mode=delete  -> analytics read during an open write: OK
journal_mode=wal     -> analytics read during an open write: OK
```

Rollback-journal mode takes its EXCLUSIVE lock **at commit**, not for the life of the transaction — a
writer mid-transaction holds RESERVED, which still permits readers. Readers are blocked only for each
commit flush, and `busy_timeout` plus `SqliteRetry` already absorb that as latency rather than errors.

So there may be no problem to fix. The phase therefore **measures first** and stops there if the numbers
say so — which is a result worth having, because it retires a question that would otherwise keep coming
back.

**DuckDB is not ruled out but nothing points at it**, for the reason this doc already identified: it is
a columnar analytical engine and this is a write-mostly store of small rows, appended at rate. That is
the shape SQLite is best at. A storage-engine swap is the largest available answer to a problem not yet
shown to exist, and the third possibility this doc raised — that it is really phase 36's problem —
resolves itself, since phase 36 lands first and its index is the cheaper change.

