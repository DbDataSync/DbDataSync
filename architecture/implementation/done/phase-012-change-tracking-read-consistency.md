# Phase 12 — Change Tracking Read Consistency

**Status**: Complete
**Plan reference**: `architecture/planning/done/task-run-errors-during-high-volume-workload.md` — the
observation, the investigation, and the repro that established the cause. This document was written
as a plan before implementation and rewritten as a retrospective after it, per
`architecture/implementation/README.md`.

## What was built

Two independent problems, found together, fixed together.

**1. The primary key now survives a missing source row.** `MsSqlChangeTrackingReader` selected
`CT.[pk], base.*`; because `base.*` re-emits the key it appeared twice in the result set, and the
reader's name-keyed column mapping let base's occurrence overwrite CHANGETABLE's. Where the source row
was present the two were equal and nothing looked wrong; where it had been deleted, the one value that
was still reliable was set to NULL along with the rest, and the writer then attempted to insert a NULL
key.

The statement moved to a new `MsSqlChangeTrackingStatement.BuildIncremental`, which names every column
explicitly — key from `CT` only, non-key columns from `base` only — plus a computed
`__BaseMissing` marker. Extracted rather than left inline so the select list, the part that carried the
defect, is assertable without a server.

**2. A vanished source row is skipped and counted, not applied as NULLs.** `__BaseMissing = 1` on a
`'D'` row is normal. On an `'I'`/`'U'` it means the row was deleted between `CHANGETABLE` being
evaluated and the join probing the table; such a row carries no usable values and is skipped.

Skipping converges rather than losing data, and the reason is worth restating: the delete that removed
the row bumped that key's change version above this run's `@targetVersion`, so the very filter that let
the stale row through guarantees the delete is still pending and arrives on a later pass.

`ReadResult` gained an optional `ReadDiagnostics` carrying `RowsSkippedSourceRowGone`, which
`RunExecutor` logs as a warning once staging has drained the stream. The counter exists because the
original cost of this bug was mostly invisibility.

**3. Optional snapshot isolation.** `snapshotIsolation` (default off) reads `CHANGETABLE` and the
source table inside one snapshot transaction, which is the pairing SQL Server's own Change Tracking
guidance recommends — the race stops happening rather than being tolerated. Off by default because it
requires `ALTER DATABASE <source> SET ALLOW_SNAPSHOT_ISOLATION ON`, and this driver does not change
database settings.

## Real bugs found while building it

1. **The snapshot transaction leaked its isolation level across pooled connections.**
   `BeginTransaction(IsolationLevel.Snapshot)` issues a session-level
   `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`, and Microsoft.Data.SqlClient pools the underlying
   session — so a connection returned to the pool still carrying SNAPSHOT hands it to whoever draws it
   next. That next caller may be a different table mapping, or the writer, neither of which asked for
   it; against a database that doesn't allow snapshot isolation their very first statement fails with
   error 3952 for no reason they could diagnose.

   Found the hard way, which is the only reason it was found at all: tests that never enabled the
   option began failing on their *seed INSERT* after a sibling test used it. Fixed with an explicit
   `RestoreDefaultIsolationLevelAsync` on both the success and failure paths. A production-only version
   of this would have been extremely unpleasant to track down.

2. **Snapshot-not-allowed surfaces on the first `ReadAsync`, not on `BEGIN` or `ExecuteReader`.** The
   first attempt caught around beginning the transaction and executing the command, and never fired —
   SQL Server raises 3952 on first *data access*, which for this query is inside the streaming loop
   where a `yield return` forbids a surrounding `catch`. A second attempt added a
   `SELECT TOP (0) 1 FROM <table>` probe to force the failure early; that did not fire either, because
   `TOP (0)` never touches a data page. Resolved by wrapping the `reader.ReadAsync` call alone, leaving
   the `yield` outside the `catch`.

## How this was verified

- **7 unit tests on the generated statement** (no server): the key is selected only from `CHANGETABLE`
  and `base.*` is gone; non-key columns are named explicitly; the `__BaseMissing` marker tests a key
  column, which is NOT NULL in the source, so NULL there can only mean "the join matched nothing";
  column order matches the ordinals the reader walks; composite keys join and select every part; a
  key-only table produces no dangling comma.
- **5 integration tests** against real SQL Server. The load-shaped ones seed 150,000 change-tracked
  rows and read them while a second connection deletes in batches:
  - no row is ever yielded without its key — the regression guard for the defect itself;
  - deletes are still delivered, with their keys intact;
  - with `snapshotIsolation` on (and the database setting enabled), `RowsSkippedSourceRowGone` is
    **zero**, which is what distinguishes removing the race from tolerating it;
  - requesting the option without the database setting produces the actionable message naming the
    exact `ALTER DATABASE` statement, not error 3952's raw text;
  - with no concurrent writer, all 150,000 rows are read and nothing is skipped — so the skip path
    cannot be quietly discarding work under normal conditions.

  A race-based test passes vacuously if the race does not fire, so the run was checked rather than
  assumed: **38,183 rows were skipped out of ~102,000 processed**, and the no-concurrency test read all
  150,000 with zero skipped. The tests say this about themselves rather than looking exact.
- Full suite green: 147 non-integration, 47 integration. `dotnet build` clean.

## What's explicitly not built

Enabling `ALLOW_SNAPSHOT_ISOLATION` from the application. Any change to the watermark-on-success-only
behaviour — it is what made these failures self-healing and it is correct. Equivalent hardening of
`MsSqlWatermarkReader` or `MsSqlBatchReloadReader`: neither joins two sources, so neither has this
class of problem.

## Notes / things to revisit later

- `dev-harness up` still does not enable `ALLOW_SNAPSHOT_ISOLATION` on its scenario database, so trying
  the option locally means one hand-run `ALTER DATABASE`. Left alone deliberately: making the harness's
  source database differ from a default one would make the default path the untested one.
- Skipping is logged but not bounded. A mapping skipping a large fraction of its rows every pass is a
  source changing faster than the schedule can drain it, and a log line may not be signal enough — but
  a threshold nobody has felt the need for yet would be invented, not designed. Worth revisiting once
  the log line has been seen under real load.
- The `ReadDiagnostics` shape — mutable, filled during enumeration, read afterwards — sits awkwardly
  beside the immutable `ReadResult` record. It is the price of the reader having no logger; if more
  readers grow diagnostics, passing a sink into `ReadChangesAsync` would be the better shape.
