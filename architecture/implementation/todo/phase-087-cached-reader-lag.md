# Phase 87 — Cache both sides of reader lag; stop querying the source to render a status screen

**Status**: Not started.
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
