# Phase 88 — Runs page: PID, watermark timestamps, shared 10s refresh, lag's "as of" time

**Status**: Not started.
**Plan reference**: `architecture/planning/done/runs-page-watermarks-and-monitoring-refresh.md`

## The gap

Four independent, small gaps in the runs/monitoring UI arc this session has been building:
`Pid`/`PreviousWatermark`/`NewWatermark` are on the wire but unused in the SPA; the three monitoring
panels (`MetricsCard`, the Monitoring/lag tab, `RunsPanel`) poll at inconsistent intervals (30s, 30s, and
not-at-all-except-push, respectively) with no visible indication of when the next refresh happens; and
`ReaderLagService` computes an "as of" timestamp for every lag figure and throws it away before it
reaches the API.

## What to build

### `Pid`

Render it in `RunsPanel.tsx` — already typed (`types.ts`), already on the wire.

### Watermark timestamps, via `ChangeCheckHistory`'s crossing-row lookup

Add `previousWatermark`/`newWatermark` to the SPA's run-record type (present on `Models.cs`'s
`TaskRunRecord`, absent from `types.ts`). For each, resolve a display timestamp by generalizing
`ChangeCheckStore.FindEarliestCheck` (`ChangeCheckStore.cs:177-200`) — it already takes a
`Func<string, bool> matches` delegate; call it with
`value => ChangeCounters.Compare(readerKind, value, targetWatermark) >= 0` where `targetWatermark` is
the run's `PreviousWatermark` or `NewWatermark` string, instead of the applied-watermark comparison it's
used for in `ReaderLagService` today. Prefer the matched row's `SourceTimeUtc`; fall back to
`CheckedAtUtc` when the mechanism has no time mapping (or the DMV aged it out) for that row. No match
(retention window has aged past the run) → no timestamp, not an error. Render the resolved time in
`RunsPanel`, with the raw watermark string in a tooltip.

You'll need the mapping's `(ConnectionName, SourceDatabase, ReaderKind)` group to call this —
`ChangeSourceResolver.Describe` (already built, phase 85) is the existing way to get it from a
`ReplicationTaskConfig`/mapping name.

### A reusable countdown, three panels at 10s

A new hook/small component (no existing precedent in this codebase — check how `useRunMetrics`/
`useReplicationLag` structure their `refetchInterval` today and build something that both drives the
interval and exposes seconds-remaining for display). Render it via each panel's own page into
`AppShell`'s `actions` slot (`AppShell.tsx`'s `.right` area, alongside `NotificationBell`/`SignedInAs`) —
not one shared global timer, since each panel is an independent hook with its own mount time.

- `MetricsCard`: 30s → 10s.
- The Monitoring/lag tab (`useReplicationLag`): 30s → 10s.
- `RunsPanel`: gains a 10s poll **alongside** its existing SignalR push and 1.5s live-watch backstop, not
  replacing either.

### Lag's "as of" time

`ReaderLagService.CdcLagAsync` already reads `checks.GetLatestCheck(...)`'s `CheckedAtUtc` and discards
it; the Change Tracking path has the equivalent available from its own `checks.GetLatestCheck(...)` call.
Surface it on `ReaderLag` → `MappingLag` → `ReplicationLag` (`types.ts:881-905`,
`ReplicationLagController.cs`) as a per-mapping field — prefer `SourceTimeUtc` when the row has one
(matching whichever the lag figure itself used), `CheckedAtUtc` otherwise. Render it in the Monitoring
tab next to each mapping's lag figure.

## What this phase should not do

- Any new schema — `ChangeCheckHistory` already has everything this needs.
- Touch `useReplicationStatus`, `useNotifications`, or `useVerificationResults`'s cadence.
- One global cross-page countdown — three independent ones.

## How to verify

- A test asserting a recent run's watermark timestamp resolves correctly (exact via `SourceTimeUtc` when
  available, otherwise `CheckedAtUtc`), and that a run outside `ChangeCheckHistory`'s retention window
  shows no timestamp rather than an error or a fabricated one.
- A test/story confirming each of the three panels' countdown resets on its own refetch independently of
  the other two.
- A test asserting `RunsPanel` still receives SignalR pushes correctly with the new 10s poll layered on
  top — no double-fetch storms, no dropped live updates.
- A test asserting the lag "as of" field matches the exact `ChangeCheckHistory` row used in that
  mapping's own comparison.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
