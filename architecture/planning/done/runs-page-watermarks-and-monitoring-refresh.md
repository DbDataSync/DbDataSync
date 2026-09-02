# Runs page: PID, watermark timestamps via polling history; a shared 10s refresh with a countdown; lag's "as of" time

**Status: resolved — ready for an implementation phase doc.**

## Four asks, confirmed against the current code

1. **Show `Pid` on the runs page.** Already on the wire (`TaskRunRecord.Pid`, `Models.cs`) and already
   typed in the SPA (`types.ts:715-750`) — just never rendered in `RunsPanel.tsx`.

2. **Show `PreviousWatermark`/`NewWatermark` as timestamps, with the raw value in a tooltip.** The C#
   wire type already carries both (`Models.cs`); the SPA type doesn't declare them yet, and nothing
   renders them. **Resolved via question**: don't look them up in `ChangeWatermarks` — that table is
   current-value-only (`ChangeWatermarks.cs`'s own comment: "one row per (TaskName, MappingName,
   SourceTable), overwritten"), so it only ever matches the run that most recently advanced the
   watermark; every older run's stored value has nothing to match. Use **`ChangeCheckHistory`**
   instead — the per-`(ConnectionName, SourceDatabase, SourceKind)` polling history phase 75 built,
   which actually spans time. `ChangeCheckStore.FindEarliestCheck` (`ChangeCheckStore.cs:177-200`)
   already does exactly the lookup this needs — the earliest history row whose `Value` has reached a
   target, via a delegate comparison (not raw SQL, for the same "9 orders above 10 as text" reason
   `ChangeCounters.Compare` exists) — built for Change Tracking's estimated-lag crossing point, and
   generalizable as-is to an arbitrary target value: pass
   `value => ChangeCounters.Compare(readerKind, value, target) >= 0` instead of the applied-watermark
   comparison it's used for today. The matched row's `SourceTimeUtc` (preferred, exact where the engine
   stated it — populated for CDC since phase 85, for Change Tracking since phase 87) or `CheckedAtUtc`
   (fallback, the poll time) is the displayed timestamp; the run's own stored `PreviousWatermark`/
   `NewWatermark` string is the tooltip.

   **Bounded by retention, honestly.** `ChangeCheckHistory` is age-purged (`RunPruningService`,
   `DataSync:ChangeCheckRetentionDays`, default 7 days). A run older than that window has no crossing
   row left to find — it shows no timestamp, same "no data yet" discipline used everywhere else in this
   feature area, not an error and not a fabricated value.

3. **10-second refresh, with a countdown, on the monitoring panels.** **Resolved via question**: scoped
   to the three panels this arc has actually built — `MetricsCard` (currently 30s), the Monitoring/lag
   tab (currently 30s), and `RunsPanel` (currently push-driven via SignalR with no fixed poll, plus a
   1.5s backstop only while a run is actively being watched — this phase adds a 10s poll *alongside*
   that, not instead of it). `useReplicationStatus` (5s), `useNotifications` (15s), and
   `useVerificationResults` (5s) are explicitly left alone — not part of this arc, not asked for.

   No existing countdown/timer UI exists anywhere in this codebase (checked). `AppShell` (`AppShell.tsx`)
   is one shared header across every page, with a per-page `actions` slot in its `.right` area (already
   used for page-specific controls, alongside the always-present `NotificationBell`/`SignedInAs`) — the
   natural place for each of the three panels to render its own countdown, since they're independent
   hooks with independent mount times, not one synchronized global timer. A new reusable countdown
   hook/small component is needed; nothing to extend.

4. **The Monitoring tab shows the "as of" time behind its lag figures.** `ReaderLagService` already
   computes exactly this and throws it away: `CdcLagAsync` reads `checks.GetLatestCheck(...)` (a
   `ChangeCheck` carrying both `CheckedAtUtc` and `SourceTimeUtc`) and uses only `.SourceTimeUtc`,
   discarding `.CheckedAtUtc` entirely (`ReaderLagService.cs`). Neither `ReaderLag` nor `MappingLag` nor
   `ReplicationLag` (`types.ts:881-905`, `ReplicationLagController.cs`) exposes any "as of" field today.
   This is the same concept for both mechanisms — "when did we last ask the source what its current
   position was" — and it's per-mapping (or per-group), not one figure for a whole replication, since a
   replication's mappings can span different source databases/kinds each polled on their own schedule.

## What this phase should build

- `Pid` rendered in `RunsPanel.tsx`.
- `previousWatermark`/`newWatermark` added to the SPA's run-record type; a new lookup (reusing
  `FindEarliestCheck` generalized as above) exposed via whatever API shape already returns run history,
  rendering the resolved timestamp with the raw value as a tooltip. Runs outside `ChangeCheckHistory`'s
  retention window show no timestamp.
- A reusable countdown component/hook, rendered via each of the three panels' own page into `AppShell`'s
  `actions` slot; all three standardized to a 10-second interval (`RunsPanel` gains this as an addition
  to its existing SignalR push, not a replacement).
- `ReaderLag`/`MappingLag`/`ReplicationLag` gain an "as of" timestamp (`CheckedAtUtc`, or `SourceTimeUtc`
  when present, matching whichever the lag figure itself preferred), surfaced per mapping in the
  Monitoring tab.

## What this phase should not do

- Add history to `ChangeWatermarks` itself, or any new schema at all — everything needed already exists
  in `ChangeCheckHistory`.
- Change the refresh cadence of anything outside the three named panels.
- Build a single global, cross-page synchronized countdown — each panel's countdown reflects its own
  polling cycle.

## How to verify

- A test asserting a recent run's watermark timestamp resolves via the crossing-row lookup, using
  `SourceTimeUtc` when the matched row has one and falling back to `CheckedAtUtc` otherwise.
- A test asserting a run whose watermark predates `ChangeCheckHistory`'s retention window shows no
  timestamp, not an error or a fabricated one.
- A test/story confirming the countdown resets correctly on each of the three panels' own refetch, and
  that changing one panel's timer doesn't affect another's.
- A test asserting the lag "as of" time matches the `ChangeCheckHistory` row actually used in that
  mapping's comparison, not a different one.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-088-runs-watermarks-and-refresh.md`.
