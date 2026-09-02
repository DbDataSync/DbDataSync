# Phase 88 — Runs page: PID, watermark timestamps, shared 10s refresh, lag's "as of" time

**Status**: Done.
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

---

## Outcome

**Shipped.** Four small gaps, and one of them turned out to have a wrong answer hiding in it.

### What was built

`Pid` is a column in `RunsPanel`. `previousWatermark`/`newWatermark` joined the SPA's run record
and are rendered as a `from → to` pair of times, with the raw source position in each end's
tooltip. `RunWatermarkTimeService` derives those times through a new
`ChangeCheckStore.FindEarliestChecks`, behind `GET api/replications/{name}/runs/watermark-times`.
`RefreshCountdown` and `ShellActions` are new shared components; `MetricsCard`, the Monitoring tab
and `RunsPanel` all refresh on `MONITORING_REFRESH_MS` and each renders its own countdown into the
shell. `ReaderLag`/`MappingLag` gained `AsOfUtc`, rendered under each mapping's lag figure.

### The bug in the doc's own lookup rule

The doc says to call the crossing-row lookup with
`value => ChangeCounters.Compare(readerKind, value, targetWatermark) >= 0` and to treat no match as
no timestamp. Implemented literally that never returns no match, and dates every expired run
wrongly. Counter values only climb, so for a target *below* the oldest row the history still holds,
`>=` matches that oldest row — and its timestamp is not when the source reached the target, it is
whenever the purge last ran. Every run older than `ChangeCheckRetentionDays` would have come back
stamped with roughly the same recent time, on a screen whose whole point is telling old passes from
new ones. The retention case the doc wanted to be blank would have been the confident-looking one.

A crossing is only datable by a row that a strictly earlier row had not yet reached. `FindEarliestChecks`
therefore discards any target the very first retained row already satisfies, which is the one
behavioural difference from `FindEarliestCheck` and is documented on it as such: phase 85 wants an
*anchor* and the oldest row is a fine one, this wants a *date* and it is not. It costs the genuine
first poll of a brand-new install, which is indistinguishable from a purged one and is reported as
unknown — the direction to be wrong in, since a missing timestamp renders as missing and a
fabricated one renders as fact.

### Deviations from the doc

**A companion endpoint, not fields on the run record.** The doc left the API shape open. `TaskRunRecord`
is `DataSync.State`'s own durable record of a run, written by the runner; these timestamps are neither
stored nor knowable when a run ends — they are derived on read from history written afterwards, and
they stop existing when that history is purged. Putting them on that record would make a durable row
carry a value that changes as the retention window moves. The endpoint takes the same `kind`/`limit`
as the history endpoint so a client asking both questions gets answered about one page.

**`FindEarliestCheck` was not generalised in place; a batched sibling was added.** The doc's phrasing
suggests reusing the existing method per watermark. A page of fifty runs is up to a hundred targets,
and that method streams a group's history from the oldest row until it matches — so a hundred calls
is a hundred scans of a table holding a row per tick per group, refreshed every ten seconds by the
panel this phase also speeds up. `FindEarliestChecks` reads the rows once and tests every target
against each row as it passes, so the cost is the history's size rather than the history's size times
the page's. The service buckets targets per source group first, since every run of a mapping shares
one and every mapping on the same database by the same mechanism shares it too — a fifty-run page of
one replication is one pass.

**`RunsPanel`'s countdown reports the interval actually in force.** While a run is being watched the
panel is on the 1.5-second backstop, and a countdown ticking down from ten beside it would be
describing a schedule the panel is not on.

### Judgment calls

- **`AsOfUtc`**, a timestamp rather than milliseconds, unlike the two figures beside it. Those are
  durations a client does arithmetic on; this is an instant a client renders.
- **It is reported even where the figure is not.** A mapping whose position cannot be placed still
  answers "when did we last see the source", and that is what separates a stalled poller from a
  mapping that has not run — two absences the blank cell used to conflate.
- **The countdown is per panel, portalled into the shell** through a new `ShellActions`. The `actions`
  prop works for a page that knows its own chrome; a countdown belongs to a panel three levels down a
  routed outlet, and threading one up would have meant every intermediate component carrying a prop
  about a child it does not otherwise know about.
- **Driven by react-query's `dataUpdatedAt`, not by a timer of its own.** Anything that fetches early
  moves the real next refresh, and a countdown keeping its own schedule would drift and then lie for
  as long as the tab is open.
- **N+1 against `ChangeCheckHistory`, twice over.** Besides the batching, the service drops targets
  above the group's newest recorded value before scanning. Those cannot resolve, and they are the
  common case — the top row of a run list routinely read past the last position the gate wrote down —
  so left in they would hold the scan open to the end of the table on every request.
- **The watermark-times query keeps the ten-second cadence even during a live watch.** Its inputs are
  written once per scheduling tick, so asking every 1.5 seconds would re-derive an answer nothing has
  changed. Its key is nested under the run history's, so every existing invalidation — including the
  hub's completion — refreshes it anyway, which is the moment a new watermark actually appears.

### The fixture bug the multi-run test found

`WorkQueueStore.Enqueue` is deduplicated against a partial unique index over the in-flight statuses,
and returns the *existing* run's id rather than a new one — correctly; that index is the point.
Completing the run does not close the queue row. A test helper that enqueued and completed three
consecutive passes therefore produced one run overwritten three times, and the test asserting a page
of run history was asserting against a page of one. The helper now claims and `MarkDone`s the item,
which is what a TaskRunner does.

### How it was verified

- `RunWatermarkTimeTests` (new, 7): the crossing row's `SourceTimeUtc` preferred and its
  `CheckedAtUtc` used where the row has none — both in one test, since the point is that they are
  the same lookup; the retention case above, asserting null for the half below the window and a real
  time for the half inside it; a target past the newest reading; a run that made no position durable
  and a mapping whose reader has no source position, both absent rather than empty; and a three-run
  page whose shared boundaries are dated identically on both sides.
- `ReaderLagTests` (5 new): the "as of" naming the exact row each figure used — CDC skipping a newer
  poll that found no position, the same one its figure skips; Change Tracking's exact path reporting
  the engine's commit time rather than the poll time, and its estimate reporting the poll time; the
  timestamp present where the figure is not; and null for a reader in no polling group.
- `runs-watermarks-refresh.spec.ts` (new, 4, Playwright): PID and both watermark ends with their raw
  values in tooltips and three dashes meaning three different things; three countdowns on three
  cycles, staggered by a tab click so the metrics card demonstrably does not reset on navigation; a
  full ten-second cycle down and back with the request count bounded; and Run Now switching the
  panel to the 1.5-second backstop, which is the layering stated as an assertion.
- Full Playwright suite **53 passed** — the golden path's 45 and phase 86's 4 unaffected by the two
  new columns, the countdown in the chrome of every detail screen and the new line under each lag
  figure. Screenshots `62-runs-pid-and-watermarks.png` and `63-refresh-countdowns.png`.
- Full suite: `Category!=Integration` **1128 passed, 18 failed**; `Category=Integration` **218
  passed, 0 failed**. `tsc -b`, the SPA build and `oxlint` clean on every file this phase touched.

### Pre-existing failures, confirmed as such

The 18 are the same 18 phase 86 recorded, and the set was diffed against a run of the tree before
this phase's first edit: identical names, 1116 passed then against 1128 now — the twelve new tests
and nothing else. None is in a file this phase touched. Nine Windows-only tests across
`DataSync.Certificates.Tests` and `CertificateExpiryServiceWindowsTests`, the whole of
`AdminCertificateServiceWindowsTests`, the two `AdminConfigControllerTests` file-source tests, and
`InviteCommandTests.Invite_AgainstAnMsSqlConfiguredRepo_Succeeds`.

The first full Playwright run had one failure — golden path 41, on
`SqlException: Login failed for user 'sa'` from the change poller. `golden-path.spec.ts` on its own
then passed all 45, and the full suite passed all 53 on the next run: a transient against the test
SQL Server, in a file this phase does not touch.

### What this phase did not do

No new schema — `ChangeCheckHistory` had everything. No change to `useReplicationStatus`,
`useNotifications` or `useVerificationResults`. No single global countdown. And no history added to
`ChangeWatermarks`, which remains current-value-only and remains the wrong table to ask.
