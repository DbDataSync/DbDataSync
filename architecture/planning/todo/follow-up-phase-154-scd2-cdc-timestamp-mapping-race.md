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
