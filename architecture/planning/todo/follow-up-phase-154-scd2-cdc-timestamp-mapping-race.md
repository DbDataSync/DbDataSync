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

## 2026-09-23: three more real recurrences in one day, two more fixes — still not closeable

Checked directly against real CI run history (`gh run list`/`gh run view`), not assumed from commit messages:

- **~05:18**, run before `7fca259`: `ScanUntilPastAsync`'s own 30s window was exceeded — a timeout, not the
  identical-mapped-time bug this doc's fixes address — on an unrelated docs-only push. The very next push
  (no code change) went green. Logged in the flake catalogue, not acted on here per its own commit message.
- **17:40**, run `35896673883`: `APassWithDuplicateAndSingletonKeys_...` failed again —
  `System.TimeoutException: cdc.lsn_time_mapping did not record a transaction ... CDC scan errors: 1 (last:
  Another connection with session ID 79 is already running 'sp_replcmds' for Change Data Capture ...)`.
- **18:24**, run `35901453430`: the identical test, identical failure shape, no `sp_replcmds` contention
  logged this time (`CDC scan errors: 0`) but still no new mapping point within the 90s/777-attempt window.

Two more fixes landed the same day, in order: `6572bd9` ("Widen `ScanUntilPastAsync`'s deadline and sharpen
its diagnostics, not guess again") and `4916d9a` ("Stop `ScanUntilPastAsync` from hammering its own
dependency every 100ms") — the second directly targets the `sp_replcmds` contention the 17:40 failure's own
error message named: polling every 100ms was itself part of what was contending for the capture job's lock.

**Since `4916d9a`**: 2 consecutive green `dotnet-integration` runs (`35904228990`, `35907644383`), confirmed
via `gh run view`, not assumed. Real progress, not yet this doc's own "several consecutive runs" bar —
three distinct failure shapes surfaced in one day is exactly the reason not to call this closed on two
green runs. Stays open; next recurrence (or its continued absence) is the thing to watch.

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

## 2026-09-23 (again): the exact `sp_replcmds` collision recurred through the "stop hammering" fix — both tests disabled

CI run `35947908766` (commit `763c6e7`, an unrelated query-source feature push — nothing in that commit touches
`Scd2CdcGuaranteedDeliveryIntegrationTests`, `CdcCaptureJob`, or CDC provisioning), job `dotnet-integration`:
**both** tests in this class failed, in the same run, with the same signature `4916d9a` ("stop hammering
`sp_repldone`") was supposed to have fixed:

```
APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation:
  cdc.lsn_time_mapping did not record a transaction after 2026-09-24T02:40:24.7200000 within 90s
  (796 scan attempts, latest seen: 2026-09-24T02:40:24.7200000). capture jobs enabled: 1;
  CDC scan errors: 1 (last: Another connection with session ID 79 is already running 'sp_replcmds'
  for Change Data Capture in the current database.); lsn_time_mapping rows: 5

ADuplicateKeyStartingOrEndingInADelete_LeavesTheSameVersionsTheRowByRowLoopDid:
  cdc.lsn_time_mapping did not record a transaction after 2026-09-24T02:41:55.2170000 within 90s
  (832 scan attempts, latest seen: 2026-09-24T02:41:55.2170000). capture jobs enabled: 1;
  CDC scan errors: 1 (last: Another connection with session ID 79 is already running 'sp_replcmds'
  for Change Data Capture in the current database.); lsn_time_mapping rows: 10
```

This is the same "Another connection ... already running 'sp_replcmds'" message the 17:40 2026-09-23 occurrence
had, which `4916d9a` diagnosed as `ScanUntilPastAsync` re-issuing `sp_repldone @reset = 1` on every 100ms retry
and fixed by releasing the log reader once instead of every poll. That fix is still in place and still verified
locally (three back-to-back green runs of the full class at the time). The identical error message recurring
under it means either the fix only narrowed the window rather than closing it, or session ID 79 in this run
belongs to something else entirely contending for the same capture job (this environment shares CDC-enabled
databases across six containers per the earlier "I/O contention" theory) — not established, and not chased
further here.

**Six real fix attempts, over four days, have not produced a stable green class**: the 30ms clock-tick delay
(falsified), the verified-wait replacing it (still timing out), the 30s→90s deadline widen (falsified by 777
identical-latest attempts), the hammering fix for the exact error seen again just now, plus two deadline/CDC
scan-error diagnostics improvements along the way. Each fix was real and justified on its own evidence — none
of this doc's "Applied" sections were guesses — and each was independently falsified by the next occurrence.
That pattern, not any single failure, is the reason to stop here rather than attempt a seventh.

### Decision: both tests disabled, not deleted

Per policy going forward for any CDC-related test failure (recorded in `[[cdc-test-flake-policy]]`): a failing
CDC test gets its failure documented here (or in its own follow-up doc, if it's not already tracked), then is
disabled with `[Fact(Skip = "...")]` naming this doc, rather than left red or fixed-and-hoped. `dotnet-integration`
does not get to stay a coin flip while this is investigated properly, off the CI critical path.

- `APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation` — **disabled**, `763c6e7`'s
  follow-up commit.
- `ADuplicateKeyStartingOrEndingInADelete_LeavesTheSameVersionsTheRowByRowLoopDid` — **disabled**, same commit.

Both are the entire `Scd2CdcGuaranteedDeliveryIntegrationTests` class — nothing is left running in it.

### Bar for re-enabling: 20 consecutive clean runs, in isolation, before touching CI again

Whoever picks this up next does not get to re-enable on "the fix looks right" — every prior fix in this doc
looked right and was falsified by CI within days. Before flipping `Skip` back off:

1. Run the specific test **alone** (`dotnet test --filter FullyQualifiedName~<TestName>`, not the whole class
   or assembly) against a real SQL Server container, **20 consecutive times with no failure**. Isolation matters
   because the leading theory for the latest recurrence is contention from *something else* sharing the capture
   job — running solo removes that variable and tests the mechanism itself.
2. Only after 20/20 solo: run the full `Scd2CdcGuaranteedDeliveryIntegrationTests` class back to back a further
   few times (this doc's existing bar) to catch any within-class interaction.
3. Only after both: remove the `Skip`, push, and watch the next several `dev` `dotnet-integration` runs — the
   thing every prior "Applied" section in this doc called "not yet proven" and was each time proven wrong.

Fewer than 20 solo runs is exactly the amount of confidence every earlier "Applied" section in this doc already
had before being falsified. This is a stricter bar than this project's usual "verified" language, chosen
deliberately because that language has now been wrong six times on this exact class.

## A candidate seventh fix, found before re-reading this section (2026-09-24) — and held to the 20-run bar above, not exempted from it

Written from a separate pass over the same two 2026-09-23 recurrences the section above already used as its
own evidence (`35896673883` at 17:35, `35802533231` earlier the same day) — both, in fact, **predate**
`4916d9a` (18:41 the same day), so they are what motivated that fix, not a recurrence through it; the section
above's own real post-`4916d9a` evidence is `35947908766`, and that is the run any claim below has to survive,
not the two already explained.

Re-reading those two runs' own `CDC scan errors` diagnostic anyway surfaced something `4916d9a` didn't
address: `APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation` calls
`CdcCaptureJob.ScanAsync`/`ScanUntilPastAsync` **four times** in sequence
(`Scd2CdcGuaranteedDeliveryIntegrationTests.cs:265,273,276,285`). `4916d9a` made each of those four calls
release the log reader exactly once, via `finally` — an improvement over releasing once per 100ms poll — but
each of the four calls still independently releases-then-reacquires, so there are still three gaps *between*
calls, within one test, where the capture job's own restart (`StopCaptureJobAsync`/`EnableDbAsync`, run again
at the top of the next call) or an in-flight prior scan can land its own `sp_replcmds` checkout. `35947908766`
recurring with the identical "session ID 79" signature *after* `4916d9a` is consistent with this residual gap
being the thing `4916d9a` narrowed but didn't close — not proven to be it, since the other session's own
"something else contending for the same capture job" theory for that run wasn't ruled out either.

**Applied, as a candidate, not yet as a re-enable**: `ReleaseLogReaderAsync` is now `public`, and neither
`ScanAsync` nor `ScanUntilPastAsync` calls it anymore — every caller in this project holds one dedicated,
unpooled `SqlConnection` for a single test's whole lifetime (the same reason
`Scd2CdcGuaranteedDeliveryIntegrationTests`, `MsSqlCdcReaderTests` and `MsSqlCdcLsnTimeTests` all open theirs
with `pooled: false`), so the log reader never needs to be handed back to anyone until that one test is
genuinely done with CDC. Each of those three test classes' `DisposeAsync` now calls
`CdcCaptureJob.ReleaseLogReaderAsync` once, after `StopCaptureJobAsync`, best-effort — released exactly once
per test, in teardown, instead of once per call. A test that scans four times in a row no longer creates any
release/reacquire gap between those four calls for anything else to land in.

**This does not by itself clear the bar the section above set, and isn't allowed to** — that bar exists
specifically because every fix in this doc so far "looked right" first. See the next section for the actual
20-consecutive-solo-run result against both disabled tests, run against this change before either test is
re-enabled.
