# Phase 7a — Watermark Terminology Rename

**Status**: Complete
**Add-on to Phase 7** (see `architecture/implementation/README.md`'s "File naming" convention) — a
small, discrete maintenance change requested directly by the user shortly after Phase 7 landed, not
independent enough in scope to warrant its own phase number.

## What prompted this

While explaining the (already-implemented) generic fallback reader, it was described as reading rows
via a "cursor" value. The user pushed back: "cursor" has a specific, unrelated meaning in database
administration (a server-side `DECLARE CURSOR`/`FETCH`/`CLOSE` construct — something this codebase
correctly avoids everywhere, since row-by-row server cursors are exactly the kind of thing a
watermark-based `WHERE col > @value` query is meant to avoid). Investigating confirmed the concern was
justified twice over:

1. No actual SQL cursor exists anywhere in the codebase — verified by reading every reader
   implementation. The word was only ever describing a plain scalar checkpoint value.
2. The codebase itself was inconsistent about naming that value: the **store** was already called
   `ChangeWatermarkStore`/`SetWatermark`/`GetWatermark`/`ChangeWatermarks` table, but the **value**
   flowing through `IChangeReader`, `ReadResult`, and the table's own `Cursor` column was called
   "cursor" throughout — the class name said one thing, the column inside it said another.

Separately, the user clarified that "batch" should be reserved for a different, not-yet-built
concept: reloading or backfilling a table's data, either in its entirety or in segments partitioned
by a list of values or ranges. The existing `MsSqlBatchReader` (`Kind = "Batch"`) was actually an
*incremental* watermark-based fallback reader, not a reload/backfill mechanism — so it was using the
name that concept needs.

## What changed

**Terminology, `cursor` → `watermark`**, end to end:
- `IChangeReader.ReadChangesAsync`'s `previousCursor` parameter → `previousWatermark`.
- `ReadResult.NewCursor` → `NewWatermark`.
- `MsSqlChangeTrackingReader`/`MsSqlWatermarkReader` (see below): internal variables, SQL parameter
  names (`@previousCursor` → `@previousWatermark`), and error message text updated to match.
- `ChangeWatermarkStore.SetWatermark`'s `cursor` parameter → `watermark`.
- The `ChangeWatermarks` table's SQLite column, `Cursor` → `Watermark` — edited directly in the one
  existing migration script (`DataSync.State/Migrations.cs`) rather than added as a second migration,
  since nothing has shipped against the old column name yet.
- `RunExecutor`'s live-log line changed from `"... (cursor: ...)"` to `"... (watermark: ...)"` — this
  is user-visible in the SPA's live run log viewer.

**Reader kind, `Batch` → `Watermark`**:
- `MsSqlBatchReader.cs` → `MsSqlWatermarkReader.cs` (class renamed to match).
- `MsSqlDriverKinds.Batch = "Batch"` → `MsSqlDriverKinds.Watermark = "Watermark"`.
- `MsSqlDriver.Readers` registry entry, `driverKinds.ts`'s `READER_KINDS` list, and every test
  referencing the old kind/class name updated to match.

**"Batch reload" reserved as a term, not built**: added to `implementation-plan.md`'s Backlog section
as a distinct, explicitly not-yet-designed concept — a full or list/range-segmented
reload/backfill of a table, separate from the three *incremental* readers (Change Tracking, the
renamed Watermark fallback, and the still-deferred CDC reader). While there, also added CDC's
deferral to that same backlog list — it had only ever been noted in `phase-003-mssql-driver.md`'s Notes
section and was never carried forward, a gap noticed during this rename's investigation.

`architecture/detailed-design.md` §3.5, §3.7, and the end-to-end data-flow section (§4) updated to
use "watermark" consistently and to flag the batch-reload/watermark-reader distinction explicitly, so
a future reader doesn't rediscover the same ambiguity.

**Historical phase docs left untouched**: `phase-002-state-store.md`, `phase-003-mssql-driver.md`, and
`phase-004-task-runner.md` still say "cursor" and "Batch reader" in places — they're accurate records of
what those phases actually built and named at the time, per this project's established convention of
not rewriting completed phase summaries. This document is the place that explains the rename for
anyone who reads those older docs and wonders why current code doesn't match.

## How this was verified

Full solution rebuild (clean, no errors), SPA `tsc --noEmit` (clean), the full non-integration suite
(81 tests), the full integration suite (19 .NET tests, including the two new Phase 7 tests exercising
real spawned `DataSync.TaskRunner` processes and the renamed SQLite column), and the Playwright
golden-path suite (7 tests) — all passing after the rename, confirming no reference to the old names
was missed anywhere in the pipeline from SPA dropdown through to the persisted SQLite schema.
