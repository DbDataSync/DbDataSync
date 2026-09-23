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

## Applied (2026-09-20): option (a) — separate the operations by a clock tick

`Scd2CdcGuaranteedDeliveryIntegrationTests` now waits 30 ms (`NextClockTickAsync`) before each operation whose mapped time
must differ from the previous one: the second update of Id 1, the re-insert of Id 4 after its delete, and the delete of Id 5
after its update. 30 ms is ten times the 3.33 ms `datetime` tick, so the two commits cannot share one. The assertions keep their
meaning (strict `<` / `!=`); nothing in the product was touched, and the class passes locally (2 of 2, three runs). **Not proven:**
that this ends the failures — it can only be shown by CI staying green on this class over several runs, which is the thing to
watch. The other CDC failure seen the same day (`MsSqlCdcReaderTests.ChangesFromEarliest_…`, `Assert.Single() … 2 items`) is a
different test and is not addressed here.

## The 30 ms fix's own theory was falsified (2026-09-22)

CI run `35776177912`, 2026-09-22, failed `APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation`
with both mapped times identical — **with the 30 ms gap in place**. That rules out the theory the fix was built on: whatever
actually governs how often a mapping point advances in `cdc.lsn_time_mapping` is not the `datetime` column's 3.33 ms rounding,
or a fixed delay long enough on one runner would have been long enough on this one too. Waiting longer is guessing at an
unknown granularity; the fix below checks instead of guessing.

## Applied (2026-09-22): wait for a verified new mapping point, not a fixed delay

`CdcCaptureJob.ScanAsync` now returns the latest `tran_end_time` from `cdc.lsn_time_mapping` it produced, and a new
`CdcCaptureJob.ScanUntilPastAsync(connection, after)` re-scans (up to 30s, 100ms between attempts) until a scan actually
produces a mapping point strictly after `after`, throwing with `DiagnoseAsync`'s output if it never does.
`Scd2CdcGuaranteedDeliveryIntegrationTests`'s three delay-then-scan call sites (the second update of Id 1, the re-insert of
Id 4, the delete of Id 5) now capture the prior operation's returned mapped time and call `ScanUntilPastAsync` with it instead
of sleeping a guessed duration; `NextClockTickAsync` is removed. This proves the property the assertions actually need (two
distinct mapping points) rather than betting a delay is long enough, on this runner and every future one. **Not proven:** that
CI stays green on this class over several runs — that's still the thing to watch, and if `ScanUntilPastAsync` itself times out
often, that's new information about the real granularity worth its own follow-up.

## `ScanUntilPastAsync` itself timed out (2026-09-23, run `35854242228`) — widened, not re-guessed

`ADuplicateKeyStartingOrEndingInADelete_LeavesTheSameVersionsTheRowByRowLoopDid` failed with `ScanUntilPastAsync`'s own
`TimeoutException` after its 30s deadline — "new information about the real granularity worth its own follow-up," exactly
as predicted above. What the message showed: every scan attempt in the window completed without error (no `IsScanBusy`
exhaustion, no unhandled `SqlException`) and reported the identical `latest` value throughout — the polling loop itself
was working correctly, it just never observed the write within 30 real seconds. Given tests in this assembly already run
one at a time (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`, `AssemblyInfo.cs`), that rules out
CDC-capture contention *between test classes* — the likelier story is CDC's own log-scan catch-up latency occasionally
running long under the CI runner's I/O contention (six database containers sharing one host), which a forced `sp_cdc_scan`
reduces but does not eliminate.

Considered and dropped, checked live rather than assumed: a diagnostic comparing `sys.fn_cdc_get_max_lsn()` against
`cdc.lsn_time_mapping`'s own `MAX(start_lsn)`, on the theory the former reads the live transaction log independent of
CDC's own capture. A throwaway probe against a real CDC-enabled table showed the two are identical before and after a
scan — `fn_cdc_get_max_lsn()` is sourced from `cdc.lsn_time_mapping` itself, so that comparison would always read "nothing
unmapped." Not shipped.

**Applied**: `ScanUntilPastAsync`'s deadline widened 30s → 90s — the same shape as this repo's own `c66b834`
(`UpdateConfirmationServiceTests`'s deadline raise for a slow Windows runner): patience spent only when a run is about to
fail, not a cost on the happy path. `DiagnoseAsync` now also reports how many scan attempts ran and
`cdc.lsn_time_mapping`'s total row count, so a future timeout (if any) can distinguish "many fast attempts, genuinely
nothing new" from "attempts themselves were slow" — the one thing this occurrence's own diagnostics couldn't say. **Not
proven**: whether 90s is enough under worse contention than this one occurrence saw — still the thing to watch.

## The 90s widening was falsified too (2026-09-23, run `35901453430`) — the wait was hammering its own dependency

A different test in the same class, `APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation`,
timed out at the full 90s — with the sharper diagnostics this time saying something the 30s occurrence couldn't:
**777 scan attempts across the full 90 seconds, every single one reporting the identical, unmoved `latest` value**
(`cdc.lsn_time_mapping did not record a transaction after 2026-09-23T18:22:32.3670000 within 90s (777 scan attempts,
latest seen: 2026-09-23T18:22:32.3670000)`). This falsifies the "CDC log-scan catch-up latency occasionally runs long"
theory the 90s widen was built on: a genuine catch-up lag would not survive 777 real, cheap attempts (≈115ms apart)
without ever budging once. That many fast attempts finding nothing is a real stall, not a slow-but-eventually one —
widening the deadline again would only spend more CI time arriving at the same failure.

**What was actually wrong, found by re-reading `ScanUntilPastAsync`'s own loop, not by waiting for more data**: every
100ms retry called the *whole* `ScanAsync` — re-stop the (already-stopped) capture job, re-scan, then re-release the log
reader via `sp_repldone @xactid = NULL, ..., @reset = 1`. That meant a stuck wait re-issued `sp_repldone @reset = 1`
against the exact capture mechanism it was waiting on, hundreds of times, with nothing new consumed between calls.
`sp_repldone` is shared infrastructure with transactional replication, whose actual job is advancing a "how far has this
been consumed" marker — this doc does not claim certainty about its exact interaction with CDC's own internal
bookkeeping under repeated, rapid, out-of-band calls, but "stop hammering the mechanism you are waiting on with a call
whose whole job is marking things as already handled" is strictly safer than guessing at a longer timeout a third time.

**Applied**: `CdcCaptureJob` split into `ScanOnceAsync` (just `sp_cdc_scan` + read the latest mapped time — the
*repeatable* part) and `ReleaseLogReaderAsync` (`sp_repldone @reset = 1` — the *cleanup* part). `ScanAsync` (the
single-shot callers) still does stop → scan-once → release, unchanged in effect. `ScanUntilPastAsync` now stops the
capture job **once**, loops `ScanOnceAsync` alone for its retries, and releases the log reader **once** via a `finally`
whether it succeeds or times out — not once per 100ms poll. Verified: the full `DbDataSync.Drivers.MsSql.Tests` project
(261/261) and the three CDC-touching classes run three more times back to back, all green — this environment's own
idle/fast containers don't reproduce the original stall either way, so this is proof of no regression, not proof of the
fix; the real proof is whether this class of failure recurs on CI. **Not proven**: whether the hammering theory is
actually correct — only that it's a real, justified inefficiency this removes regardless, and a materially different
change from "wait longer" the next occurrence (if any) will distinguish from a still-open mystery.
