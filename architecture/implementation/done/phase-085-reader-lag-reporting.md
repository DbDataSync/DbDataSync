# Phase 85 — Reader lag: CDC's real duration, and Change Tracking's two figures

**Status**: Not started.
**Plan reference**: `architecture/planning/done/reader-lag-reporting.md`, which builds the general
"reader declares a lag capability" design `architecture/planning/todo/run-lag.md` sketches, scoped to
CDC and Change Tracking now (Watermark mode is a separate fast-follow, structurally different — see the
plan doc's "Scope, resolved").

## The gap

Nothing computes a staleness figure for any replication today. `run-lag.md` names CDC and Change
Tracking as two of the mechanisms with *some* comparable "now," and this phase builds both — CDC as a
real duration via the engine's own LSN-to-time mapping, Change Tracking as an exact version count plus a
separately-labeled *estimated* duration reconstructed from this system's own polling history, since
Change Tracking has no engine-side equivalent to `fn_cdc_map_lsn_to_time`.

The naive definition — "now minus our last applied change's time" — is wrong for both: it grows forever
on a quiet, fully-caught-up source. Every figure below compares against the source's own current
position instead, never wall-clock now.

## What to build

### Schema: `ChangeCheckHistory.SourceTimeUtc`

Nullable column (`Migrations.cs`). Populated only for `SourceKind = Cdc`, via
`sys.fn_cdc_map_lsn_to_time(maxLsn)` in `DriverChangeCounterSource.FetchAsync`
(`ChangeCounterSource.cs`), on the same round-trip that already fetches the max LSN and has already
switched to the right database. Null for `ChangeTracking` rows — a version count has no time mapping.
`ChangePollingGate.RecordCheck` (`ChangePollingGate.cs:151`) gains one more column to write.

### CDC lag

A method returning `TimeSpan?` for a mapping: look up its current `ChangeWatermarks` position (phase
74, keyed by `MappingName`; null → return null, first pass has nothing to compare), map it to a time via
`sys.fn_cdc_map_lsn_to_time` (on demand, mapping-specific), compare against the latest
`ChangeCheckHistory.SourceTimeUtc` for that mapping's `(ConnectionName, SourceDatabase, Cdc)` group
(falling back to a live fetch through `IChangeCounterSource`, extended to also return the mapped source
time, when no usable history row exists), clamp negative results to zero.

### Change Tracking: exact figure

A method returning a version-count `long?` (or similar — your call on the exact numeric type,
consistent with how `ChangeWatermarks`/`ChangeCheckHistory` already store versions): the group's latest
`ChangeCheckHistory.Value` for `SourceKind = ChangeTracking` minus the mapping's own stored
`ChangeWatermarks` version. Both values already exist; no new fetch, no estimation.

### Change Tracking: time figure — exact via `dm_tran_commit_table`, falling back to an estimate

**Try the exact path first.** `sys.dm_tran_commit_table` maps a commit sequence number to its commit
time, and Change Tracking's `SYS_CHANGE_VERSION` values are commit sequence numbers:

```sql
SELECT tc.commit_time
FROM sys.dm_tran_commit_table AS tc
WHERE tc.commit_ts = @version;
```

This is exact, the same role `sys.fn_cdc_map_lsn_to_time` plays for CDC — but `dm_tran_commit_table` is
a DMV holding a bounded, rolling window of recent commits, not indefinite history. A version old enough
to have aged out returns no row: a real "too old to map" answer, not an error, and the mirror of CDC's
own `cdc.lsn_time_mapping` retention limit.

**Fall back to the estimate when the DMV has aged the version out — not otherwise.** A method returning
`TimeSpan?`, explicitly and separately labeled "estimated" wherever it surfaces (API field name, any
future UI text) — never merged with or presented identically to the exact DMV path or to CDC's exact
figure. Scan the mapping's group's `ChangeCheckHistory` rows for `SourceKind = ChangeTracking`, ascending
by `CheckedAtUtc`, for the first row whose `Value` (parsed as `long` in application code — not compared
as text in SQL, and not compared as text across engines; see the plan doc's note on why) is `>=` the
mapping's own applied version. That row's `CheckedAtUtc` is the estimate's anchor;
`estimatedLag = latestGroupCheckedAtUtc - anchorCheckedAtUtc`, same zero-clamp discipline as CDC. A
mapping with neither a DMV answer nor a usable history row returns null, not a synthesized zero or an
exception.

**The response shape must say which path answered.** A caller can't treat an exact `dm_tran_commit_table`
result and a `ChangeCheckHistory`-estimated one as interchangeable — pick a shape (a discriminated
field, separate nullable properties, whatever fits this codebase's existing API conventions) that makes
the distinction impossible to lose, not just documented.

### API

Expose all three figures (CDC's `TimeSpan?`, Change Tracking's exact version-count and estimated
`TimeSpan?`) so a future notifications latency trigger or UI figure can consume them. Match this
codebase's existing per-mapping-status API shape rather than inventing a new pattern — check what's
already returned alongside a mapping's run history before adding a parallel endpoint.

## What this phase should not do

- Watermark mode's lag (timestamp-column-based) or BatchReload's "not applicable" declaration — same
  interface, later phase.
- The notifications latency trigger, or a UI card/column — separate work, not required to prove these
  computations.
- Postgres (phase 34) — unaffected.

## How to verify

- CDC: non-null `SourceTimeUtc` only for `Cdc` history rows; zero-not-negative clamping; no false growth
  on a quiet caught-up source (the specific bug the naive definition would have); live-fetch fallback
  when no usable history exists.
- Change Tracking exact (version count): version-count arithmetic against known stored/current values.
- Change Tracking time, exact path: a version still within `dm_tran_commit_table`'s window resolves via
  the DMV join and is labeled exact, not estimated.
- Change Tracking time, fallback path: a version the DMV no longer holds falls back to the
  `ChangeCheckHistory` estimate; a built `ChangeCheckHistory` sequence crossing a mapping's applied
  version at a known tick, asserting the estimate lands on that exact tick's timestamp; a caught-up
  mapping reporting zero; a mapping with neither a DMV answer nor enough history reporting null, not
  zero or an exception; a small-numbers case (`Value` like `"9"` vs `"10"`) proving the parse-as-`long`
  comparison is actually used, not a text comparison that would order them wrong; and a test proving the
  exact and fallback paths are distinguishable in the response shape, not just in code.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

---

## Outcome

**Shipped.** Three figures over two mechanisms, and the one the doc was revised for — Change
Tracking's time — is exact far more often than the original design assumed.

### What was built

`ReaderLagService` computes a mapping's staleness; `ChangeSourceResolver` answers "which source group
is this mapping in, and where is its own position kept" for both it and `ChangePollingGate`, shared
rather than duplicated so a mapping is never grouped one way for the skip decision and another for the
figure reported about it. `GET /api/replications/{name}/table-mappings/{mapping}/lag` returns
`MappingLag`.

`ChangeCheckHistory` gained `SourceTimeUtc` and the group index phase 75 deliberately deferred until a
reader for it existed. `ChangeCheckStore` gained `GetLatestCheck` and `FindEarliestCheck`.
`MsSqlCdcCatalog.MapLsnToTimeAsync` and `MsSqlChangeTrackingReader.MapVersionToTimeAsync` are the two
engine-side mappings everything exact rests on.

### The doc's revision, implemented — and one thing it did not say

`sys.dm_tran_commit_table` works exactly as the revised doc claims, confirmed against a real server
rather than taken on trust: `MsSqlChangeTrackingVersionTimeTests` asserts a recent version maps to a
real time, and that two versions in order map to times in that order — the property lag actually
depends on, and one that a mapping returning plausible-but-unrelated times would still have passed the
first test without.

**Both ends of the subtraction come from the DMV, or neither is used.** The doc says to map the
mapping's own version and does not say what the other end of an exact Change Tracking lag is.
Subtracting an engine-stated commit time from `CheckedAtUtc` — the wall-clock instant a poll happened
to run — would produce a figure whose error is the polling interval, which is precisely the error the
*estimate* owns and names, reported in the field that promises there is none. So the exact path maps
both the mapping's version and the group's current one, and falls to the estimate if either will not
place. `ChangeTrackingTime_NeverMixesAnEngineTimeWithAPollTime` pins this.

The mapping's own version is looked up first because it is the older of the two and therefore the one
that ages out; when it has, the second query is not worth making. So the common case for a mapping
that is even roughly keeping up costs two round-trips, and a mapping far behind costs one.

**The response shape needed no discriminated field.** `exactLagMs` and `estimatedLagMs` are separate
nullable properties and are never both populated, so the distinction cannot be lost by a consumer that
reads only one of them — and one that coalesces them has said in writing that it does not mind. Which
field arrives is itself the answer to "how good is this figure", and the same Change Tracking mapping
moves between them as it falls behind.

### Deviations from the doc

**`SourceTimeUtc` stays null on Change Tracking rows**, as the doc's schema section says — but not for
the reason that section gives. That reason ("a version count has no time mapping") is now false, and
the migration comment says so. The column stays CDC-only because CDC's mapping is a free rider on a
round-trip the gate was already making, while the DMV lookup is a query of its own: worth making when
somebody asks for a lag figure, not once per tick for every group whose figure nobody reads. Doing it
on demand also keeps the gate's cost per tick exactly what phase 75 measured.

**An SPA type, and no UI**, matching this repo's convention that the API client mirrors every endpoint:
`MappingLag` in `types.ts` and `tableMappings.lag` in `client.ts`. No component consumes it yet — a
card or column is scoped out — but the next phase that wants one does not also have to discover the
shape.

### Two bugs in the inherited work, and one in a doc comment

This phase's implementation was largely written by an earlier session that was interrupted by an
infrastructure error, and reviewing it turned up three things worth recording:

- **`MsSqlCdcLsnTimeTests` could never have passed.** It enabled CDC at the database level only, on
  the claim — stated in its own class doc — that the capture job populates `cdc.lsn_time_mapping`
  whether or not a table is captured. It does not: with no capture instance to scan there is nothing
  to write, `fn_cdc_get_max_lsn()` stays null, and both tests spent 90 seconds reaching a timeout whose
  message blames SQL Server Agent. Agent was running the whole time. Now it captures a table, reusing
  `MsSqlCdcReaderTests`' deadlock-retry shape, and both pass in twelve seconds.
- **`AReaderWithNoDatabaseWideCounter_SaysSoRatherThanReportingNothing` 400'd in setup**, because the
  Watermark reader requires a `watermarkColumn` and the fixture never supplied one. The endpoint was
  right to reject the task; the test wanted a reader with no database-wide counter, not one with an
  incomplete configuration, and those are different answers.
- **`MapLsnToTimeAsync` had been inserted between `ToWatermark`'s doc comment and `ToWatermark`**,
  leaving two `<summary>` blocks on the new method and none on the old one.

My own first attempt at the "aged out" integration test asserted that version `1` no longer maps. It
does: `MsSqlTestDatabase` sets `CHANGE_RETENTION = 2 DAYS` with `AUTO_CLEANUP = OFF`, so nothing has
aged out of a database this young, and asserting on an old version there tests the fixture's retention
settings rather than the code. The test now reaches an absent row from above instead, which exercises
the identical path — no row, so no time.

### Judgment calls

- **An unreachable source costs Change Tracking's figure its precision, not its existence.** CDC has
  nowhere to go when the server will not answer and returns null; Change Tracking falls back to the
  estimate, which needs no source at all. Asking how far behind something is must never be able to
  fail the screen it is on.
- **`GetLatestCheck(requireSourceTime: true)` for CDC's far end.** The latest row that *has* a time,
  not the latest row: a group whose most recent poll found no position (the capture job stopped) still
  has an earlier reading to measure against, and reporting nothing there would hide a lag that is
  growing for exactly that reason.
- **The crossing scan compares parsed `long`s in C#, not `>=` in SQL.** `Value` is text and this store
  runs on three engines, all of which order `"9"` above `"10"`;
  `ChangeTrackingEstimated_ComparesVersionsAsNumbersRatherThanAsText` uses small numbers because that
  is where a text comparison goes wrong first and where a fresh install lives.

### How it was verified

- `ReaderLagTests` (new, 22): the CDC figures including the quiet-caught-up case the naive definition
  would have got wrong, the zero clamp on both sides, the live-fetch fallback; Change Tracking's
  version count; and the exact/estimated pair asserted in **both** directions — an available exact
  answer taken with the estimate left null, and an unavailable one producing an estimate with the exact
  field left null — because a shape whose purpose is keeping two precisions apart only does that if
  neither is reachable through the other's field.
- `MsSqlChangeTrackingVersionTimeTests` (new, 3, `Category=Integration`) and `MsSqlCdcLsnTimeTests`
  (2, `Category=Integration`): both engine mappings against a real server, including version ordering
  and the null-not-error answer for a position neither will place.
- `ChangePollingGateTests` (9) carries phase 84's
  `ACdcMappingDrainingUnderARowCap_IsDispatchedOnEveryTickUntilItCatchesUp`, which that phase left
  uncommitted here rather than split one file across two phases' commits. It survived the rebase and
  ships in this phase's commit, as its retrospective said it would.
- Full suite: `Category!=Integration` **1074 passed, 9 failed**; `Category=Integration` **213 passed,
  0 failed**. `tsc -b` and the SPA build clean.

### Pre-existing failures, confirmed as such

None of the nine are in a file this phase touched.

- The three phase 84 confirmed in a clean tree, unchanged here:
  `InviteCommandTests.Invite_AgainstAnMsSqlConfiguredRepo_Succeeds` and the two
  `AdminConfigControllerTests` file-source tests. Still phase 79/81 territory.
- Six Windows-only certificate tests that arrived with phase 82 (`7ec1782`) and cannot pass on Linux:
  five in `DataSync.Certificates.Tests` (`CertificateStoreWindowsTests`,
  `PendingEnrollmentKeysWindowsTests`) and `CertificateExpiryServiceWindowsTests` in
  `DataSync.Api.Tests`.

### What this phase did not do

Watermark mode's timestamp lag and BatchReload's "not applicable" declaration remain the fast-follow,
on this same shape. The notifications latency trigger and any UI surface remain unstarted; the
computation and its endpoint are now in place for both.
