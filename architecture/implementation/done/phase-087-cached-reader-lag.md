# Phase 87 — Cache both sides of reader lag; stop querying the source to render a status screen

**Status**: Done.
**Plan reference**: `architecture/planning/done/lag-cached-not-live.md`

## The gap

`ReaderLagService` queries the live source system on every call — `sys.fn_cdc_map_lsn_to_time` for
CDC's applied position, `sys.dm_tran_commit_table` twice for Change Tracking's applied and current
positions — because neither is cached anywhere today. The Monitoring tab (phase 86) polls every 30
seconds across every mapping in a replication, and the replications list calls the same bulk endpoint
per row: viewing a status screen generates recurring live load against every source it reports on. This
phase caches both sides of every comparison so `ReaderLagService` reads only the state database.

## What to build

### `ReadResult` gains an optional mapped-time field

`DataSync.Drivers.Abstractions` — a nullable field alongside `NewWatermark`/`WatermarkAfterRead`
(`ReadResult.cs`), set only by readers that can produce it.

### CDC and Change Tracking readers populate it

`MsSqlCdcReader` — the mapped time of the LSN it's about to return as the new watermark, via
`MsSqlCdcCatalog.MapLsnToTimeAsync`, on the connection the pass already has open. `MsSqlChangeTrackingReader`
— the equivalent `dm_tran_commit_table` lookup phase 85 built for `ExactChangeTrackingLagAsync`, reused
here for the version it's about to persist. Every other reader leaves the field null.

### `ChangeWatermarks` gains a mapped-time column

Nullable, written in the same call that writes the watermark — `RunExecutor.cs:732`'s
`state.SetWatermark(...)` call gains one more parameter, carrying the value `ReadResult` just returned.
Thread it through every call site in the chain (all six already move
`taskName`/`mappingName`/`sourceTable`/`watermark` together, so this is one more parameter at each, not
new plumbing):

- `IRunnerState.SetWatermark`
- `LocalRunnerState.SetWatermark` → `ChangeWatermarkStore.SetWatermark`
- `RemoteRunnerState.SetWatermark` → `StateProtocol.SetWatermarkRequest`
- `RunnerStateEndpoints`'s `/set-watermark` handler
- `JournalRecovery`'s replay of `JournalOperation.SetWatermark`

No backfill — a mapping that hasn't run since this ships has no cached value until its next pass, same
as every prior phase in this area.

### Change Tracking's current-position time, captured on the gate's own tick

`DriverChangeCounterSource.FetchAsync`'s `MsSqlDriverKinds.ChangeTracking` branch: after fetching the
raw version, also run the `dm_tran_commit_table` query for it, on the same connection, and populate
`ChangeCheckHistory.SourceTimeUtc` the same way the CDC branch already does. This reverses phase 85's
"better made on demand" call for this specific value — that reasoning is exactly what created the
live-query problem this phase fixes.

### `ReaderLagService` rewritten to read only cached values

- CDC applied time: the new `ChangeWatermarks` column, not `counters.MapSourceTimeAsync`.
- CDC current time: unchanged — already `ChangeCheckHistory.SourceTimeUtc`.
- Change Tracking applied time: the new `ChangeWatermarks` column.
- Change Tracking current time: `ChangeCheckHistory.SourceTimeUtc`, now populated for Change Tracking
  rows too.
- **Remove `CdcLagAsync`'s live-fallback branch entirely** (`counters.FetchAsync` when no history row
  exists) — a cold-start mapping with no cached data reports "no data yet" (`Supported: true`, null
  figures), the same state already used elsewhere in this service, not a live escape hatch.
- The Change Tracking `ChangeCheckHistory`-crossing-row estimate stays, as the fallback for a
  pre-migration row or a failed gate-tick capture — no longer the routine path, still a real one.

## What this phase should not do

- Touch what CDC/Change Tracking readers query for the actual replication pass — only what a lag *read*
  costs.
- Extend any of this to Watermark mode or other readers.
- Backfill either cache.

## How to verify

- A test asserting `ReaderLagService` makes zero calls to `IChangeCounterSource` once both caches are
  populated (a mock/fake that throws if called would prove this directly).
- A test asserting the applied-time value written by `SetWatermark` matches what the reader's
  `ReadResult` returned for that exact pass.
- A test asserting `ChangeCheckHistory.SourceTimeUtc` is now populated for `ChangeTracking` rows, not
  just `Cdc`.
- A test asserting a watermark with no cached mapped-time (pre-migration row) reports "no data yet," not
  an exception and not a live query.
- A test asserting `JournalRecovery`'s replay path carries the mapped-time value through correctly for a
  runner that journalled offline.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

## Outcome

**Shipped.** `ReaderLagService` has no `IChangeCounterSource` and no `async` method. The property the
phase exists for is not a rule anyone has to keep — it is a dependency that is not there and a
signature that could not await one.

### What was built

`ReadResult` gained `NewWatermarkTimeUtc`, `BoundedReadPosition` gained `ReachedTimeUtc`, and
`WatermarkTimeAfterRead` pairs each time with the position that pass will actually store.
`MsSqlCdcReader` and `MsSqlChangeTrackingReader` fill them on the connection the pass already has
open. `ChangeWatermarks` gained a nullable `WatermarkTimeUtc`, written in the same `Upsert` statement
as `Watermark` and read back by the new `ChangeWatermarkStore.GetAppliedPosition` as one
`AppliedPosition` record. The value travels the loopback chain — `RunExecutor` → `IRunnerState` →
`LocalRunnerState`/`RemoteRunnerState` → `StateProtocol.SetWatermarkRequest` →
`RunnerStateEndpoints` → `JournalRecovery` — as one more defaulted parameter at each of the six sites
that already move `taskName`/`mappingName`/`sourceTable`/`watermark` together.
`DriverChangeCounterSource.FetchAsync`'s Change Tracking branch now also maps the version it fetched,
so `ChangeCheckHistory.SourceTimeUtc` is populated for both mechanisms. `CdcLagAsync`'s live-fallback
branch is gone.

### `BoundedReadPosition.ReachedTimeUtc` — the part the doc did not ask for, and the phase needs

The doc names only `ReadResult`'s field. That is not enough. A capped pass stores the position it
*reached*, not the window's end, and those are two positions with two different commit times. A plain
`Bounded?.ReachedTimeUtc ?? NewWatermarkTimeUtc` would hand back the window end's time whenever the
reached one failed to map — a time describing a position the pass did not store, on a mapping
draining a backlog under a row cap, which is precisely the mapping furthest behind reporting itself
as caught up. So `WatermarkTimeAfterRead` keys off `Reached` rather than coalescing, and each reader
sets `ReachedTimeUtc` in the same place it sets `Reached`, from the same last row, on the connection
that just streamed it. A time and a position that disagree about which pass they came from are worse
than no time at all.

### Deviations from the doc

- **The quiet-mapping branch maps too.** `MsSqlCdcReader`'s nothing-new path returns the stored
  position unchanged; if it returned no time with it, a caught-up mapping — including every row
  written before the column existed — would never acquire one and would report "no data yet" for
  ever. The doc's no-backfill rule says a mapping catches up on its next pass, and this is what makes
  that true for a mapping whose next pass finds nothing.
- **A failed mapping never fails a pass.** Both readers wrap the lookup in a `catch` that returns
  null (rethrowing on cancellation). A lag figure is a question about a replication; the replication
  is the thing itself. This is the same judgement `ReaderLagService` used to make about these calls,
  moved to the site where the value is now produced.
- **`SetWatermark` overwrites the time on every pass, including with null.** A reader that stops
  being able to state one clears the stale value rather than leaving it to be read as current.
- **`GetAppliedPosition` is one row read, not `GetWatermark` plus a lookup**, for the reason the two
  are written together: a pair fetched in two queries can straddle a pass that ran between them.

### The state I found the inherited work in

Substantially complete and, on review, correct — including both tests the interrupted session was
described as mid-way through. The `ReadResult` pairing tests were already in `BoundedReadTests`
(five, covering the coalesce bug directly) and the journal-replay tests already in
`JournalRecoveryTests` (two, one carrying a time and one from a runner older than the field). The
tree built clean and its unit tests passed. What was actually missing was verification at three
seams, and one deletion that should not have happened.

### What was finished here

- **`ReaderLagTests.TheEndpointIsNotFoundForAMappingThatDoesNotExist`, restored.** The session had
  deleted it while making the action synchronous. Nothing in this phase makes an unknown mapping a
  different answer, and dropping the async is exactly the edit that could have turned a 404 into an
  empty 200 a screen renders as "no lag data".
- **`ChangeCounterSourceIntegrationTests` (new, 1, `Category=Integration`)** — the doc's third
  verification bullet, which nothing covered: a real Change Tracking database, through the API's own
  DI, asserting `FetchAsync` returns a `SourceTimeUtc` and that it is the time an independent
  `MapVersionToTimeAsync` gives for *that* version. `MsSqlChangeTrackingVersionTimeTests` proved the
  DMV lookup works; nothing proved the gate's branch now makes it.
- **Reader-side pairing against a real server (4 new integration tests).** `BoundedReadTests` pins
  the pairing *rule* on a hand-built `ReadResult`; it cannot show that either reader fills the fields
  from the right position. Two tests in `MsSqlChangeTrackingReaderTests` and two in
  `MsSqlCdcReaderTests` assert the returned time equals an independent mapping of
  `WatermarkAfterRead` — including a capped pass, where `NewWatermark` and `WatermarkAfterRead`
  differ and only one of them has the right time, and the CDC nothing-new pass that keeps a quiet
  mapping's cache alive.

### The `BoundedRead.cs` question

Genuine, not drift. `BoundedRead.cs` and `BoundedReadTests.cs` showed modified because
`BoundedReadPosition` is where `ReachedTimeUtc` belongs and `BoundedReadTests` is where `ReadResult`'s
pairing property is tested — both are this phase's work, sitting in files phase 84 created. Nothing
about phase 84's row-bounding behaviour changed.

### How it was verified

- `BoundedReadTests` (+5): the pairing in all four states, including the coalesce that would report a
  backlogged mapping as caught up.
- `ChangeWatermarkStoreTests` (+4): written in the same call, read back together, a null clearing an
  earlier time, and a mapping that has never run.
- `JournalRecoveryTests` (+2): a journalled time replayed, and an entry from before the field existed
  replaying as a watermark rather than being refused — the treatment the missing *mapping name* one
  field along correctly gets, and this one correctly does not.
- `ReaderLagTests` (22): every figure recomputed from the two caches, and the three absences —
  no history time, no cached applied time, never read — all reporting `Supported: true` with null
  figures. `TheEndpointReportsAnExactFigureWithoutEverReachingTheSource` is the phase's regression
  test: over HTTP, through the real `DriverChangeCounterSource` and a connection pointing at a server
  that is not there, an exact seven minutes comes back.
- New integration coverage as above (5).
- Full suite: `Category!=Integration` **1116 passed, 18 failed**; `Category=Integration` **218
  passed, 0 failed**. `tsc -b` and the SPA build clean, with no SPA change — the response shape is
  unchanged, only where its values come from.

### Pre-existing failures, confirmed as such

All 18 reproduce on a clean tree at `main`, and none is in a file this phase touched. They are phase
85's nine plus the nine `AdminCertificateServiceWindowsTests` that arrived with phase 83 after that
count was taken: Windows-only certificate tests that cannot pass on Linux, the two
`AdminConfigControllerTests` file-source tests, and `InviteCommandTests.Invite_AgainstAnMsSqlConfiguredRepo_Succeeds`.

`BackfillIntegrationTests.Backfill_ForAnUnknownMapping_Is404` failed once in a whole-solution
integration run and passed on every subsequent run, alone and in its full suite — flakiness under
projects sharing one SQL Server, not a regression.

### What this phase did not do

No backfill of either cache, no extension to Watermark mode or any other reader, and no change to what
a replication *pass* queries. Watermark mode's timestamp lag remains the fast-follow phase 85 named.
