# Follow-up (phase 154): a CDC timestamp-mapping race in `Scd2CdcGuaranteedDeliveryIntegrationTests`

**Found** while verifying phase 154's own CI run — a real, unrelated failure on the very next
`dotnet-integration` run after phase 154 landed (`35284322323`, triggered by the
`BulkLoadIntegrationTests` fix, not by phase 154 itself; phase 154's own run, `35283992102`, was fully
green). Not investigated further — this doc exists so it isn't lost the way phase 134's own follow-ups
were before this process existed (`architecture/implementation/README.md`'s "Follow-up work gets its own
doc" section).

## What failed

`DbDataSync.Drivers.MsSql.Tests.Scd2CdcGuaranteedDeliveryIntegrationTests.APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation`,
at `Scd2CdcGuaranteedDeliveryIntegrationTests.cs:308`:

```
Assert.NotEqual() Failure: Values are equal
Expected: Not 2026-09-17T22:56:47.9500000
Actual:       2026-09-17T22:56:47.9500000
```

That's `Assert.NotEqual(id1[0].ValidTo, id1[1].ValidTo);` — the test updates the same row (`Id = 1`)
twice, calling `CdcCaptureJob.ScanAsync` between the two updates specifically so each lands as its own
CDC-mapped transaction with its own `__$start_lsn`/mapped time (see the test's own extensive comment
around line 291 explaining why it asserts internal consistency rather than an independently re-queried
`sys.fn_cdc_map_lsn_to_time` value). This run, both updates' `ValidTo` — sourced from the same per-row
`ChangedAtColumn`, in turn from CDC's own mapped time — came out identical.

## What's known, and what isn't

- `CdcCaptureJob.ScanAsync` (`tests/DbDataSync.Drivers.MsSql.Tests/CdcCaptureJob.cs`) stops the Agent
  capture job, forces a synchronous log scan, and waits for it to release the log reader — its own doc
  comment says "everything committed before this returns is captured and its LSN mapped in
  `cdc.lsn_time_mapping`." The test calls it once after each of the two updates, which is presumably
  meant to guarantee two distinct mapping points.
- The test's own comment already documents that CDC's time-mapping function *interpolates* between
  points `cdc.lsn_time_mapping` holds, and can shift values for already-committed LSNs when a later scan
  adds new points — i.e., the author already knew this mapping isn't a precise, monotonic clock reading.
- **Not established**: whether the two `ScanAsync` calls in this test can genuinely produce mapping
  points at the *same* timestamp under fast, back-to-back execution (a CI-speed race — plausible if
  `cdc.lsn_time_mapping`'s own granularity or the underlying transaction commit/log-scan timing doesn't
  guarantee separation at the resolution this assertion needs), or whether something about `ScanAsync`'s
  own stop/wait/restart sequence occasionally fails to force the second update into a genuinely separate
  scan (a bug in the test helper, not CDC's own timing).
- **Not established**: whether this has failed before. Not cross-checked against older CI runs; this
  doc is written from the one occurrence phase 154's own verification happened to surface.

## Why this wasn't fixed here

Unrelated to phase 154 (no file this failure touches was changed by that phase) and unrelated to the
`BulkLoadIntegrationTests`/`LibraryLoadTests` fixes made alongside it. Diagnosing a real timing
mechanism in SQL Server's own CDC log-to-time mapping is a different, deeper investigation than either of
those — worth its own pass, not a rushed guess bolted onto unrelated work.

## Open questions for whoever picks this up

- Does this reproduce reliably, or was it a one-off? Worth checking recent `dotnet-integration` history
  for the same assertion/line number before assuming either.
- If it's a genuine race: does `ScanAsync` need a stronger guarantee (e.g., asserting the new mapping
  point's own timestamp advanced past the previous one, not just that the scan completed), or does the
  test need to tolerate two updates landing in the same mapped-time bucket the way
  `PrimaryAndBulkLoad_TriggeredConcurrently_BothSucceed`'s sibling test now tolerates its own race outcome
  (see `architecture/implementation/done/phase-154-ci-integration-suite-onto-docker-compose.md`)?

## Second occurrence (phase 159 CI run `35482609356`, job `106003003998`)

A different test in the same class, the same shape of failure:
`ADuplicateKeyStartingOrEndingInADelete_LeavesTheSameVersionsTheRowByRowLoopDid`
(`Scd2CdcGuaranteedDeliveryIntegrationTests.cs`, ~line 357–412) failed
`Assert.True(id4[0].ValidTo < id4[1].ValidFrom, "the delete closed 'd0' before the re-insert opened 'd1'")`.
It deletes `Id = 4`, calls `CdcCaptureJob.ScanAsync`, re-inserts `Id = 4`, calls `ScanAsync` again, then
requires the delete's mapped time to be strictly before the re-insert's — the same "two `ScanAsync`
calls give two distinct, ordered mapping points" assumption that failed at line 308 above.

- Unrelated to that commit: nothing under `Drivers.MsSql`, `State` or `Scd2` changed, the previous commit's
  run was green, and the test class passes when run locally (2 of 2).
- This answers the first open question: it is **not** a one-off. It's two different tests in one class,
  both resting on `ScanAsync` producing strictly ordered `cdc.lsn_time_mapping` timestamps. That points at
  the shared assumption (or at `ScanAsync`), not at either test.
- Not yet checked: whether the failing pair landed on the *same* mapped timestamp (as at line 308) or in
  the wrong order. The assertion messages didn't print the values, so the CI log couldn't say. The three
  ordering assertions in the class (lines ~308, ~394, ~406) now print both timestamps, so the next
  occurrence is diagnosable from the log alone.
- Separately, two earlier integration failures (jobs `105730239424` and `105489949065`) were in
  `BulkLoadIntegrationTests`, a different flake — not this one.

## Third and fourth occurrences — and now the values (CI runs `35495888790` and `35496389094`, 2026-09-20)

The assertion messages added in `fb21188` did what they were for. From job `106040014595`, the failing test
`ADuplicateKeyStartingOrEndingInADelete_LeavesTheSameVersionsTheRowByRowLoopDid`:

```
the delete closed 'd0' before the re-insert opened 'd1'; d0 closed at 2026-09-20T07:18:47.2300000, d1 opened at 2026-09-20T07:18:47.2300000
```

**The two mapped times are identical** — not out of order. So the question left open above is answered for this
test: a `ScanAsync` between the delete and the re-insert did **not** give them distinct `cdc.lsn_time_mapping` times,
because the two transactions committed within one clock tick (SQL Server's `datetime` resolves to 3.33 ms; a fast
runner does a delete, a scan and an insert well inside that). The same run (`35495888790`, job `106038621029`)
also failed `APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation` — the test that failed
originally, on `Assert.NotEqual(id1[0].ValidTo, id1[1].ValidTo)` — so that is four failures across two tests in
five days, and the mechanism is the same one.

What this does and doesn't say:

- It is a **test** assumption failing, not evidence of a product bug: a row deleted and re-inserted within one tick
  legitimately gets `ValidTo == ValidFrom`. The same test file already asserts exactly that equality for an *update*
  (`Assert.Equal(id5[0].ValidTo, id5[1].ValidFrom)` — "ends 'e0' and begins 'e1' at the same moment").
- Two ways to make the test deterministic, neither applied yet because they differ in what they claim: **(a)**
  separate the two operations by more than a tick (a delay of ≥ 10 ms between the delete's scan and the re-insert, and
  between the two updates at line ~308) so strict `<` / `!=` hold — keeps the assertion's meaning; **(b)** relax `<`
  to `<=` for the delete/re-insert, and drop the `!=` — but then the test no longer proves the delete closed *before*
  the re-insert opened, only that it did not close after. (a) is the smaller claim change.
- Not checked: whether `ScanAsync`'s stop/wait/restart sequence could itself be made to force distinct mapping points.
