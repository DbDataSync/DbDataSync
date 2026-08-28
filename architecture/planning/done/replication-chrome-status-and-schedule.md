# Replication detail chrome: persistent Status/Schedule cards, and Save in the toolbar

**Status: resolved 2026-08-28 — arrived fully specified.**

## The note as given

"Save settings" for a replication should live in the toolbar, not below the Pipeline card. The Status and
Schedule cards should clearly identify themselves as the *replication's* status/schedule, and should be
visible on every replication page — Overview, Table Mappings, Runs, and Version Control alike, not just
Overview. Schedule should move its Enabled toggle into the card header's corner. Both Status and Schedule
should carry an accent color reflecting enabled/disabled — orange for disabled, green for enabled. Status
should show any currently running process for the replication, its PID, and memory/CPU usage, where that's
easy to retrieve.

## Grounding in the current code

- **`ReplicationDetailPage.tsx`** is already the shared chrome for all four tabs (`Overview`,
  `Table Mappings`, `Runs`, `Version Control`) — a layout route rendering `AppShell` with a persistent
  tab bar and toolbar (`actions`: Backfill…, Run Now, Delete), and an `Outlet` that swaps per tab. Its own
  doc comment: "The chrome does not unmount when the tab changes" — this is exactly the mechanism that
  already carries Backfill/Run Now down to the Runs panel, and is the natural place for Status/Schedule
  and Save to live too.
- **Today, Save settings, Schedule, and Enabled all live inside `OverviewPanel.tsx`** — Save is a button
  inside the Pipeline card's body; Schedule (mode, frequency/cron, and the Enabled toggle) is a card in
  OverviewPanel's own 288px right column, both scoped to `OverviewPanel`'s local `draft`/`setDraft`/
  `upsert` state. Neither exists on the other three tabs at all.
- **There is no Status card today.** `run-metrics.md` deliberately deferred a "health rollup" pending
  connection tests plus recent run outcomes composing into one notion of health — this is different and
  simpler: literal live-process telemetry, not a computed health judgment.
- **The data is already sitting in memory, server-side.** `ProcessSupervisor.cs` tracks one live `.NET
  Process` handle per replication name (`_workers: ConcurrentDictionary<string, Process>`), spawned by
  `EnsureWorkerRunning`. Its own doc comment: "Tracked per replication name, not per run: one worker
  process claims and drains a replication's pending WorkQueue items" — so today's architecture holds at
  most **one** process per replication, not an open-ended list. `Process.Id` (PID),
  `Process.WorkingSet64` (memory), and `Process.TotalProcessorTime` (CPU time) are trivial reads off that
  same handle — this is a new read-only endpoint exposing existing state, not new instrumentation.

## Design decisions

### Save moves to the toolbar, which means lifting the draft up

`OverviewPanel`'s `draft`/`setDraft`/`upsert` state moves up into `ReplicationDetailPage`, exposed
through `ReplicationOutletContext` (currently `{ replicationName, command }`, gaining read/write access to
the draft). `ReplicationDetailPage`'s toolbar (`actions`) gains a **Save settings** button, enabled and
showing pending state the same way the current one does — just relocated, and now backed by state one
level up so it can be reached from outside `OverviewPanel`.

### Status and Schedule become persistent chrome, not Overview content

Both cards move out of `OverviewPanel`'s two-column layout and into `ReplicationDetailPage` itself,
rendered outside the `Outlet` — the same 288px right-rail region `OverviewPanel` already uses for
Schedule today, hoisted up one level so it survives a tab switch instead of only existing on Overview.

### Enabled moves to the replication header, not the Schedule card

**Refined 2026-08-28.** Enabled does not move into Schedule's card header — it moves into the
*replication's* own header, the persistent chrome shared by all four tabs (`ReplicationDetailPage`'s
title/crumb area), separate from any card. The reason is legibility, not just placement: Schedule's other
fields (mode, frequency/cron) stay part of the batched Save-settings draft, while Enabled autosaves
immediately — keeping the toggle inside the Schedule card, even in the header corner, would visually
suggest it belongs to the same save gesture as the fields beside it. Pulling it out to the replication
header makes the independence obvious rather than something the operator has to already know.

Both Status and Schedule still get a top-border accent color reflecting the replication's enabled state —
**green when enabled, orange when disabled** — reusing the same accent-border mechanism phase 44
introduced for source/target identity, just driven by a different signal. New tokens alongside
`--accent-source`/`--accent-target`, e.g. `--accent-enabled`/`--accent-disabled`. Only the toggle control
itself relocates to the header; the accent stays on the cards, since the cards are what the state
visually describes.

### Status: the running process, if any — PID, memory, CPU

A new card showing the replication's currently running worker process, when one exists: PID, memory
(working set), and CPU time, read directly off `ProcessSupervisor`'s already-tracked `Process` handle via
a new endpoint. Today's architecture guarantees at most one process per replication, so the card shows
zero-or-one process rather than a list — written so it doesn't assume that stays true forever, but not
built to handle a case the current supervisor doesn't produce.

## Resolved: Enabled autosaves

**Enabled/disabled saves immediately on toggle** — not part of the batched "Save settings" draft. This is
scoped to the toggle specifically; the rest of Schedule (mode, frequency/cron) was not part of the ask and
stays in the batched draft alongside Pipeline/Endpoints/Scripts, saved by the toolbar's Save settings
button. Enabled needs its own direct mutation (e.g. a dedicated `PATCH`/small endpoint, or reusing the
existing upsert with just `enabled` changed and immediately committed) — it does not wait for, and is not
reverted by, the rest of the draft being unsaved or discarded.

This also resolves the risk the open question named: an operator toggling Enabled from the Runs or Version
Control tab now sees it actually take effect immediately, rather than sitting invisibly in a draft that
only a Save click elsewhere would commit.

---

# Outcome

Agreed, as `implementation/todo/phase-046-replication-chrome-status-and-schedule.md`.
