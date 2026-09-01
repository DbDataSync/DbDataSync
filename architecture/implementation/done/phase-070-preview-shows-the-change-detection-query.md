# Phase 70 — Preview SQL doesn't show how a bounded-window reader checks for changes

**Status**: Complete.
**Plan reference**: none — a confirmed gap in already-shipped preview code, not a new design.

## The gap

Preview SQL is supposed to show every statement a pass would actually run (`architecture.md`/phase 37).
`WatermarkReader.DescribeAsync` did: two `PreviewStatement`s, one for "read the highest watermark value,
which becomes the next pass's bound," one for the actual incremental row read.

SQL Server Change Tracking and CDC did only half of it. Both readers have to ask the source for its
current end position before reading — `SELECT CHANGE_TRACKING_CURRENT_VERSION();` and `SELECT
sys.fn_cdc_get_max_lsn();` — and both `DescribeAsync` implementations already ran that query, but only to
resolve `@targetVersion`/`@toLsn` into a literal baked into the one statement shown. The boundary query
itself was never returned as its own statement, so an operator saw a row-read statement with an
already-resolved version/LSN in it and no account of where that number came from.

## What was built

**`MsSqlChangeTrackingReader.DescribeAsync`** and **`MsSqlCdcReader.DescribeAsync`** each return a second
`PreviewStatement`, ahead of the existing incremental-read one, for their boundary query — the same
two-statement shape `WatermarkReader` already used, with the same wording convention ("Ask the source for
its current change-tracking version, which bounds this pass" / "…its current maximum LSN…") and the same
kind of note about why the boundary is taken before the rows rather than derived from them.

**The boundary SQL became a named constant in each reader, shared with the method that runs it** —
`MsSqlChangeTrackingReader.CurrentVersionStatement` (private) and `MsSqlCdcCatalog.MaxLsnStatement`
(public, since `GetMaxLsnAsync` lives there and `MsSqlCdcReader` describes it). Not cosmetic: a preview
whose statement text is a second copy of the string the reader executes is exactly the "preview that
looks authoritative and has drifted" `IStatementPreview`'s own doc comment argues against. One constant,
one text, both callers.

The existing incremental-read statements are untouched — same title, same SQL, same declared parameters
resolved to real values, still runnable standalone in a query tool.

## How it was verified

- `MsSqlCdcReaderTests.ThePreviewShowsTheMaxLsnQuery_BeforeTheReadThatUsesIt` (new, integration): after a
  real pass has stored a position, `DescribeAsync` returns exactly two statements, the max-LSN query
  first, and the read second still declaring both `@storedLsn` and `@toLsn`.
- `PreviewIntegrationTests.AfterAPass_ThePreviewShowsTheIncrementalReadRatherThanTheFullLoad` (updated,
  integration, Change Tracking end to end through the API): asserts both statements and their order,
  where it previously asserted a single source-read statement.
- Full suite: unit 859 passed / 0 failed; integration 269 passed / 0 failed.

## Decisions and notes

- **The early-return branches are unchanged and deliberately so** — no primary key, no stored watermark,
  no capture instance, no CDC max LSN. Each still returns its one explanatory statement. A pass that
  fails before issuing anything, or one doing a full load, has no bounded window to describe; adding a
  boundary statement there would describe a query that never runs.
- **CDC's ordering constraint**: the max-LSN statement can only be added after the `maxLsn is null`
  check, not before it. That branch's whole message is "the capture job has not run" — showing the
  boundary query above it would suggest a window that does not exist.
- **A flaky teardown, not a regression**: `RunExecutorIntegrationTests` failed once in a full integration
  run with a deadlock inside `DisposeAsync`'s `DROP DATABASE`, and passed on a rerun of the same suite.
  Unrelated to this phase (no shared code path) and left alone rather than fixed under this number.

## What this phase did not build

- Any change to `WatermarkReader` — already correct, and the reference shape this fix copied.
- Any change to the readers' actual read/bounded-window logic — preview-only.
- A Playwright check. The two readers' preview output is covered end to end through the API
  (`PreviewIntegrationTests` drives the real SPA-facing endpoint against real SQL Server), which is where
  the assertion has teeth; a browser check on top would re-assert the same strings one layer further out.
