# Phase 56 — Chunked apply, and row-bounded reads for the reference readers

**Status**: Complete
**Plan reference**: `architecture/planning/done/chunked-apply-and-bounded-reads.md`

## What this covers

1. Chunk the apply step (writer) for transaction/lock size — a writer-loop change, no watermark-model
   change. Full crash-resumable chunking is a named follow-on, not built here.
2. Bound reads by row count for the two reference readers (plain Watermark, SQL Server Change Tracking),
   not every mechanism — the riskier ones (CDC, Postgres logical) are separate follow-on work.

## 1. Chunked apply

`MsSqlMergeWriter` and `DeleteInsertWriter` each issued one statement over the entire staged set. Both
now iterate the staged rows in sub-batches (`applyBatchSize`, declared once in
`ApplyBatch.Descriptor` and offered by both writers like any other writer option), issuing one smaller
statement per chunk.

- **A stable per-row ordinal within the staging table** is what each chunk's `WHERE` ranges over.
  Staging had none, so both providers now create one — `__Ordinal`, engine-assigned
  (`IDENTITY(1,1)` on SQL Server, `GENERATED ALWAYS AS IDENTITY` elsewhere, via a new
  `SqlDialect.RenderStagingOrdinalColumn` hook) and made the staging table's key.
- **The watermark model is unchanged.** All chunks of one pass still have to succeed before the
  watermark advances — this phase buys shorter individual lock/transaction durations, not
  partial-progress recovery. That's the explicitly deferred half (option B in the planning doc).
- **`WriteResult` stays a single summary** (`RowsWritten`) — chunking is internal to `ApplyAsync`;
  nothing about the writer's external contract changed.

## 2. Row-bounded reads: Watermark and SQL Server Change Tracking

`TOP (@n) WITH TIES` on SQL Server, `FETCH FIRST @n ROWS WITH TIES` on the standard, behind a new
`SqlDialect.RenderTieSafeRowLimit` hook. One ordered scan; ties at the boundary included natively; the
new position falls out of the last row of the same result set.

`WatermarkReader`'s upfront `GetMaxWatermarkAsync` is skipped entirely when bounded — the row read
*is* the boundary computation. It is untouched on the unbounded path.

`MsSqlChangeTrackingReader` orders by `SYS_CHANGE_VERSION` and keeps its `@targetVersion` bound: the
pass reads up to the smaller of the current end and the row-count-bounded result, per the plan doc.

Both are off by default (`maxRowsPerRead`, unset = unbounded).

## Deliberately not built

- Crash-resumable chunked apply (option B) — a state-model change, deliberately deferred.
- Row-bounding for CDC, Postgres logical, or any reader beyond Watermark and Change Tracking.
- Any change to `StagedChangeSet`'s or `IChangeWriter.ApplyAsync`'s external signatures.
- Chunking in `MsSqlMergeReconcileWriter`, and per-chunk *commits* in either delete-insert writer —
  see the retrospective for why both are wrong rather than merely unbuilt.

---

# Retrospective

Two halves that sound similar and are not. Chunking the apply is a loop; bounding the read is a
correctness problem wearing a performance problem's clothes, and almost all the care went there.

## The read-side trap is the whole phase, and the fix is one clause

The plan doc already said a naive `TOP n` would drop rows past the nth while advancing the watermark
to the source's true max. What made the phase tractable was that both engines in scope express the
correct thing natively: a row limit that includes ties. That collapses "find a candidate boundary,
extend it past its ties, then read up to it" — three statements and a race between them — into one
query whose last row *is* the answer.

So the two readers' bounded paths are smaller than their unbounded ones, not larger. `WatermarkReader`
bounded issues **one** statement where unbounded issues two, because the `MAX` it used to take up front
is exactly the value a bounded read must not use.

## Change Tracking needed a rule the watermark reader does not

A watermark scan has one honest answer for "how far did this pass get": its last row. Change Tracking
does not. A pass that drained its whole window should advance to `targetVersion` — the version it fixed
for itself before reading — and only a pass the cap actually cut short should report its last row's
version.

That asymmetry is not cosmetic. Advancing only to the last row's version on a quiet table would leave
the watermark frozen at whenever that table last changed, while Change Tracking's cleanup moved on
beneath it, until the stored version fell below `CHANGE_TRACKING_MIN_VALID_VERSION` and the mapping
failed as expired — a bug that would only appear on the *least* busy tables, weeks later. There is a
test for it (`Bounded_WhenTheWindowFitsInTheCap_StillAdvancesToTheWindowsEnd`).

The upshot is that `BoundedReadPosition.Reached` is set by the reader on the reader's own rules, and
`ReadResult.WatermarkAfterRead` is a plain null-coalesce over it. Putting the "did we hit the cap"
logic in the shared type was the first attempt and it was wrong: it made one mechanism's rule look like
everybody's.

## The ordering column is usually not in the projection

Both bounded reads have to know the ordering value of the last row they emitted, and neither could
count on finding it — a mapping that does not carry its own watermark column is ordinary, and
`CHANGETABLE`'s version is not a source column at all. Both statements now append it under
`BoundedRead.PositionColumn` (`__DS_Position`), **last**, after everything the change schema describes,
so `MsSqlChangeTrackingStatement.FirstKeyOrdinal` and the ordinal arithmetic under it are untouched.
`ResultSetSchema.FromLeading` is what keeps that column out of the rows the reader emits — the position
is bookkeeping, and a bounded read must not hand staging a phantom column.

## The staging ordinal has to be the staging table's key

Chunking issues one range query per chunk. Without an index on the range column each of those re-scans
everything staged, and a 100-chunk apply does 100 full scans — chunking would cost more than the single
statement it replaced. So `RenderStagingOrdinalColumn` renders the whole column definition including
`PRIMARY KEY` (clustered, on SQL Server) rather than just a type name. Staging is only ever appended
to in ordinal order, so the clustered key costs nothing on the way in.

`SqlBulkCopy` fills the column for free: it isn't in `ColumnMappings` and `KeepIdentity` is off.

## DeleteInsertWriter chunks its statements but not its transaction, deliberately

The plan doc asks for "multiple smaller transactions," and `MsSqlMergeWriter` delivers exactly that —
each chunk is its own statement and its own implicit transaction, which is the lock-duration win.

`DeleteInsertWriter` does not, and should not. Its documented contract is that the delete and the
refill are one transaction so no concurrent reader ever observes the segment empty. Committing per
chunk would publish a half-refilled segment; for a writer whose whole purpose is replacing a scope
atomically that is not a tuning knob, it is a different writer. Its chunking therefore stops at
smaller statements inside the one transaction the delete opened. Recorded here rather than silently,
because it is the one place this phase declined what its plan literally asked for.

`MsSqlMergeReconcileWriter` was left alone for a stronger reason: its `WHEN NOT MATCHED BY SOURCE THEN
DELETE` means each chunk would delete every target row not in *that chunk*. Chunking it is not a
tradeoff, it is wrong.

## Numbers chosen, and the measurement still owed

The phase's open question asked for measured defaults rather than guesses.

- **`applyBatchSize` defaults to 5,000, and chunking is on by default.** 5,000 is SQL Server's
  lock-escalation threshold — past roughly that many row locks in one statement the engine escalates
  to a table lock, which is precisely the "everything waits behind the writer" symptom the phase
  exists to relieve. That is an engine-documented cliff rather than a guessed constant, which is why
  it was taken as the default without a benchmark run. `0` restores the single unchunked statement.
- **`maxRowsPerRead` defaults to unbounded.** Bounding is not a free win the way a smaller write
  statement is: it changes how many passes a catch-up takes, and it is only cheap when the ordering
  column is indexed. On a table where it is not, ordering the whole window to take the first n of it
  is work the unbounded read was not doing. That is an operator's judgement about their own table.

`tools/DataSync.Benchmarks` was not run for either. The apply-side number has a principled basis that
a benchmark would refine rather than establish; the read-side number is deliberately not chosen at all.
A benchmark pass over real chunk sizes is worth doing and is still owed.

## Verification

- `ApplyBatchTests` (8) — the option's parse rules including 0 meaning unchunked and a negative number
  meaning a typo, and the half-open ranges proven to cover every ordinal exactly once.
- `StagingStatementTests` (+2) — the ordinal in the DDL, and that it is the key.
- `PipelineStatementTests` (+3) — the chunked insert's bound, that it keeps the staged-delete filter,
  and that it follows the dialect for placeholders.
- `WatermarkStatementTests` (+4) — the SQL Server and ANSI bounded renderings, the position column
  staying last, and the unbounded rendering unchanged byte-for-byte.
- `MsSqlChangeTrackingStatementTests` (+4) — the cap, the version bound *kept* rather than replaced,
  the position column last, and the unbounded statement carrying neither.
- `MsSqlPipelineTests` (+2, integration) — 250 rows applied in 25 chunks with the right end state, the
  same across a mixed insert/update/delete pass, and `applyBatchSize=0` still working.
- `MsSqlWatermarkReaderTests` (+5, integration) — a cap producing a watermark strictly below the true
  max, 50 rows read over several bounded passes exactly once each, **a deliberately constructed tie**
  (three rows sharing the boundary value) proven not to be split, the position column kept out of the
  emitted rows, and an empty bounded pass leaving the watermark alone.
- `MsSqlChangeTrackingReaderTests` (+5, integration) — the same set against `SYS_CHANGE_VERSION`,
  including four rows sharing one version with a cap of two, and the drained-window rule above.
- Full suite green: 763 unit, 150 integration.

## Open questions

- ~~**The default/tunable value for `applyBatchSize` and the read-side row cap.**~~ 5,000 and
  unbounded respectively, for the reasons above; the benchmark pass is still owed.
- **A dev-harness run at volume** — the phase's last verification step, needing a multi-table workload
  that does not exist yet. Deliberately folded into phase 57, which builds exactly that; running the
  old single-table workload would have verified less than the integration tests already do.
