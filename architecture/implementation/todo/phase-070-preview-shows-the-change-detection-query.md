# Phase 70 — Preview SQL doesn't show how a bounded-window reader checks for changes

**Status**: Planned, not started — root cause confirmed by reading both readers' `DescribeAsync`.
**Plan reference**: none — a confirmed gap in already-shipped preview code, not a new design.

## The gap

Preview SQL is supposed to show every statement a pass would actually run (`architecture.md`/phase 37).
For the plain **Watermark** reader, it does — `WatermarkReader.DescribeAsync` returns two
`PreviewStatement`s: one for "read the highest watermark value, which becomes the next pass's bound,"
and one for the actual incremental row read.

**SQL Server Change Tracking and CDC don't do the second half.** Both readers, per
`change-tracking-strategies.md`'s bounded-window pattern, have to ask the source for its current end
position before reading — `MsSqlChangeTrackingReader.GetCurrentVersionAsync` runs
`SELECT CHANGE_TRACKING_CURRENT_VERSION();`, `MsSqlCdcReader`'s equivalent (via `MsSqlCdcCatalog
.GetMaxLsnAsync`) runs `SELECT sys.fn_cdc_get_max_lsn();`. Both `DescribeAsync` implementations already
call these — their own comments say so explicitly ("fetched here purely to describe the statement, same
as ReadIncrementalAsync would fetch it to run one") — but only to resolve `@targetVersion`/`@toLsn` into
a literal value baked into the one incremental-read statement shown. **The boundary query itself is never
returned as its own `PreviewStatement`.** An operator looking at Preview SQL for a Change-Tracking- or
CDC-backed mapping sees the row-read statement with an already-resolved version/LSN number in it, with no
visibility into how that number was obtained — "how the agent checks for changes" is invisible for
exactly the two readers where that check is a real, separate query against the source.

## The fix

Both `DescribeAsync` methods add a second `PreviewStatement`, ahead of the existing incremental-read one,
for the boundary query — the same two-statement shape `WatermarkReader` already uses:

- **`MsSqlChangeTrackingReader.DescribeAsync`**: a statement for `SELECT
  CHANGE_TRACKING_CURRENT_VERSION();`, titled along the lines of "Ask the source for its current change-
  tracking version, which bounds this pass" — mirroring `WatermarkReader`'s "Read the highest
  '{watermarkColumn}'..." wording.
- **`MsSqlCdcReader.DescribeAsync`**: a statement for `SELECT sys.fn_cdc_get_max_lsn();`, same treatment.

The existing incremental-read statement's `@targetVersion`/`@toLsn` parameter declarations are unchanged
— they still need a concrete value for a query tool to run the statement standalone; the fix is additive
(a new statement shown *before* it), not a change to the existing one.

## What this phase does not build

- Any change to `WatermarkReader` — already correct, the reference shape for this fix.
- Any change to the actual read/bounded-window logic in `ReadChangesAsync` for either reader — this is
  preview-only, describing what already runs.
- Coverage for CDC/Change-Tracking's other preview branches (no primary key, no stored watermark, no
  max LSN yet) — those already return early with an explanatory statement and aren't missing anything.

## How to verify when built

- A Change-Tracking-backed mapping's Preview SQL shows two source-read statements: the current-version
  query, then the incremental read — in that order.
- A CDC-backed mapping's Preview SQL shows the max-LSN query, then the incremental read.
- The incremental-read statement's declared parameters are unchanged from today (still resolved to real
  values, still runnable standalone in a query tool).
- The early-return branches (no PK, no watermark yet, no CDC max LSN) are unaffected — still one
  statement each, unchanged wording.
- Full suite green, including a Playwright check that Preview SQL for a CT/CDC mapping shows both
  statements.

## Open questions

None — this mirrors an already-correct pattern (`WatermarkReader`) onto two readers that only did half
of it.
