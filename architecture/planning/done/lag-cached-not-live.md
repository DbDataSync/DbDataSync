# Reader lag must read only the state database — caching what's currently computed live

**Status: resolved — ready for an implementation phase doc.**

## The problem, confirmed by reading `ReaderLagService.cs`

Phase 86 built the Monitoring tab and list column on top of phase 85's `ReaderLagService`, and that
service queries the **live source system**, not just the state database, on every call:

- **CDC** (`CdcLagAsync`): the mapping's own applied position has no cached time anywhere — the code
  comment says so explicitly ("there is nowhere shared to cache it") — so `counters.MapSourceTimeAsync`
  calls `sys.fn_cdc_map_lsn_to_time` against the live source every time lag is requested. If the gate
  hasn't polled that group yet, it also falls back to `counters.FetchAsync`, a second live call.
- **Change Tracking** (`ExactChangeTrackingLagAsync`): **two** live calls to `sys.dm_tran_commit_table`
  on the source per request — one for the mapping's applied version, one for the current version.

The Monitoring tab polls every 30 seconds (`MetricsCard`'s cadence) and shows every mapping in a
replication; the replications list calls the same bulk endpoint per row. Viewing a status screen is
generating recurring live load against every source system it reports on — the opposite of what a
monitoring screen should cost, and directly against the requirement that these screens work entirely off
the state database. Phase 86's "sequential, not parallel" mitigation only avoids a concurrency spike; it
does not stop the querying, and does nothing about the 30-second recurrence or the list page's per-row
multiplication.

## The fix: cache both sides of every comparison, at the moment each is genuinely free

Phase 85 already solved half of this for CDC — the *current/group* position's mapped time is captured
once per gate tick, on the connection the gate already opened for its own reason, and stored in
`ChangeCheckHistory.SourceTimeUtc`. The two remaining gaps get the same treatment:

### 1. A mapping's own applied-position time, captured when the watermark advances

`RunExecutor.cs:732` calls `state.SetWatermark(task.Name, mapping.Name, watermarkKey, newWatermark)`
after a pass completes — on a connection to the source that pass already had open for the real work.
That is the free moment: the reader that just produced `newWatermark`/`WatermarkAfterRead` already
knows how to map its own position to a time (`MsSqlCdcReader` via `MsSqlCdcCatalog.MapLsnToTimeAsync`,
`MsSqlChangeTrackingReader` via the `dm_tran_commit_table` query phase 85 added), on the same
connection, for the position it is about to persist anyway.

- `ReadResult` (`DataSync.Drivers.Abstractions`) gains an optional mapped-time field, set by the CDC and
  Change Tracking readers alongside the watermark they already return. Every other reader leaves it null
  — no capability to declare, this is plumbing the two readers that already have one participate in.
- `ChangeWatermarks` (phase 74's table) gains a nullable time column, written in the same call that
  writes the watermark, not a separate one — a position and its mapped time should never disagree about
  which pass they came from.
- The value has to travel the loopback protocol `RunExecutor` already uses to reach the state store it
  doesn't own (phase 39's single-writer model): `IRunnerState.SetWatermark`, `LocalRunnerState`,
  `RemoteRunnerState`, `StateProtocol.SetWatermarkRequest`, `RunnerStateEndpoints`'s `/set-watermark`,
  and `JournalRecovery`'s replay of the same operation — six call sites, all already threading
  `taskName`/`mappingName`/`sourceTable`/`watermark` through in lockstep; this adds one more value to
  the same trip rather than a new one.
- **Once captured, this value never needs to change.** "When did this specific position commit" is a
  fixed historical fact the moment it's known — caching it right after the pass that produced it,
  while the position is still current, is strictly *more* reliable than a later live re-query, not a
  weaker substitute. It also means Change Tracking's applied side stops depending on
  `dm_tran_commit_table`'s rolling retention window at all: captured promptly, it's captured before the
  window could ever have aged it out.
- **No backfill.** A mapping that hasn't run since this ships has no cached value until its next pass —
  the same no-backfill precedent every prior phase in this area (72 through 85) has used, for the same
  reason: there is nothing to backfill it *from*.

### 2. Change Tracking's current-position time, captured on the gate's own tick

`DriverChangeCounterSource.FetchAsync`'s Change Tracking branch fetches the raw version and stops —
phase 85 deliberately left `ChangeCheckHistory.SourceTimeUtc` null here, reasoning the DMV lookup was
"a query of its own, better made on demand." That reasoning is what this doc is reversing: "on demand"
is exactly the live-query-from-a-status-screen problem. Extend that branch to also run the
`dm_tran_commit_table` query for the version it just fetched, on the connection it already opened, and
store the result the same column CDC already populates. One more query per gate tick per group — not
per lag request, not per screen view.

### `ReaderLagService`, after both are in place

Reads only `ChangeWatermarks` (the new column) and `ChangeCheckHistory.SourceTimeUtc` — no
`MapSourceTimeAsync`/`FetchAsync` calls left in it at all. **Remove the CDC live-fallback path
entirely** (`CdcLagAsync`'s fallback to `counters.FetchAsync` when no history row exists) — the
requirement is "entirely off the state database," not "off the state database except on a cold start,"
and a cold-start mapping already has a well-defined, correct answer: "no data yet," the same state a
mapping with no watermark at all already reports.

The Change Tracking estimate (the `ChangeCheckHistory`-crossing-row reconstruction) **stays** — it's
still the honest answer for a `ChangeCheckHistory` row written before this ships (no `SourceTimeUtc`
yet, same no-backfill reasoning) or for any future gap where the DMV capture at gate-tick time itself
fails. It stops being the *routine* path for the current-position side; it remains the fallback for
exactly the cases a cache always has: missing or not-yet-populated data.

## What this phase should not do

- Change the live queries CDC/Change Tracking readers make for the actual replication work — this is
  entirely about what a *lag read* costs, not what a *pass* costs.
- Extend caching to Watermark mode or any other reader — unaffected either way, still out of scope per
  phase 85/86's own scoping.
- Backfill either new cache — no data to backfill from, consistent with every prior phase here.

## How to verify

- A test asserting a mapping's applied-position time is written in the same call as its watermark, and
  that `ReaderLagService` never calls `IChangeCounterSource` at all once both caches exist.
- A test asserting `ChangeCheckHistory.SourceTimeUtc` gets populated for a Change Tracking group's gate
  tick, not just a CDC one.
- A test asserting a mapping with a watermark but no cached applied-time (pre-migration row) reports
  "no data yet," not an error and not a live fallback.
- A test asserting the journal-replay path (`JournalRecovery`) carries the new value through correctly —
  a runner that journals its watermark offline and gets replayed later shouldn't lose the mapped time
  that came with it.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean (no SPA
  change expected — the response shape `ReaderLagService` produces is unchanged, only where its values
  come from).

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-087-cached-reader-lag.md`.
