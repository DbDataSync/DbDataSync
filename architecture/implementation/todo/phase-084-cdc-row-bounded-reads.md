# Phase 84 — Row-bounded reads for CDC

**Status**: Not started.
**Plan reference**: `architecture/planning/done/cdc-row-bounded-reads.md`, which resolves
`architecture/planning/todo/mssql-cdc-source-batching-and-guaranteed-delivery.md`'s "Follow-up 1" and
the fix `change-queue-fairness-investigation.md` (`architecture/planning/todo/`) proposed independently.

## The gap

`MsSqlCdcReader` has no row-cap mechanism — confirmed by `grep`, zero references to `BoundedRead`
anywhere in `MsSqlCdcReader.cs`. An uncapped incremental pass reads, stages, and writes everything
between the stored LSN and the database's current max LSN in one uninterrupted `WorkItem`. Two
consequences, both real: it can occupy a worker slot for an unpredictable, unbounded duration
(`change-queue-fairness-investigation.md`), and as of phase 76's default 1800s `CommandTimeoutSeconds`,
it can now fail outright rather than merely run long.

## What to build

### Reader change

Mirror `MsSqlChangeTrackingReader`'s existing shape (`BoundedRead.Read(options)`,
`MsSqlChangeTrackingReader.cs:89`, `ReadResult.Bounded`/`WatermarkAfterRead` at `ReadResult.cs:20-36`),
but ordered correctly for CDC rather than reusing Change Tracking's single-column ordering: `TOP (@n)
WITH TIES` (or `FETCH FIRST @n ROWS WITH TIES`, whichever this reader's existing statement-building
style already uses — check `MsSqlCdcStatement`/`ChangeRowDataReader` first) ordered by
`(start_lsn, seqval)`, since one LSN can span a whole transaction's worth of rows and `__$seqval` is
what orders within it. `ReadResult.WatermarkAfterRead` needs `MsSqlCdcReader` to populate `Bounded` the
same way `MsSqlChangeTrackingReader` already does — a capped read persists how far it actually got, not
the full window's end.

### Default cap, not opt-in

Both mechanisms get a default cap now rather than staying opt-in — resolved in planning, since an
unbounded mapping is exactly the one nobody thought to configure. Pick a default row count (headroom vs.
too many small re-enqueued passes is the tradeoff; look at what a reasonable batch size costs through
staging/writing before picking a number, don't guess one that only accounts for the read side).

### No changes needed elsewhere

`ChangePollingGate` (phase 75) already handles a capped, multi-pass drain correctly — it compares each
mapping's own watermark against a freshly-fetched value every tick, never a cached "last checked" flag,
which is exactly what a mapping mid-drain with no new source writes between passes needs. Confirm this
composition with a test (below) rather than assuming it from the doc's own reasoning.

## What this phase should not do

- The guaranteed-delivery mode (`mssql-cdc-source-batching...md`'s Follow-up 2) or its same-key
  PK-collision prerequisite fix in `Scd2Writer` — both remain separate, unstarted work.
- Any transactional-integrity-across-a-split guarantee — row-level ordering only, not transaction
  grouping.
- Row-bounding for any other reader — Change Tracking already has it, nothing else in scope needs it.

## How to verify

- A test with a backlog larger than the default cap, asserting the pass advances only partway and the
  watermark reflects `WatermarkAfterRead`, not the full window's end.
- A test asserting a subsequent pass picks up exactly where the previous capped one left off — no gap,
  no duplicate rows re-delivered.
- A test confirming a transaction spanning more rows than the cap doesn't lose row-level ordering across
  the split (per `(start_lsn, seqval)`), even though transaction-grouping itself isn't preserved.
- An integration-level test exercising `ChangePollingGate` against a mapping mid-drain across several
  capped passes with no new source writes in between, asserting the gate keeps dispatching it (per
  revision 3's reasoning in phase 75's own doc) rather than misreading "counter unchanged" as "nothing to
  do."
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean if the
  cap becomes a configurable per-mapping option surfaced in the UI (check whether Change Tracking's
  existing `BoundedRead.Descriptor` parameter already gives CDC the same UI surface for free, or needs
  its own).
