# Phase 46 — Replication detail chrome: persistent Status/Schedule, and Save in the toolbar (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/replication-chrome-status-and-schedule.md`

## What this covers

1. Move "Save settings" from inside `OverviewPanel`'s Pipeline card into `ReplicationDetailPage`'s
   toolbar.
2. Move Status (new) and Schedule (existing) out of `OverviewPanel` and into the persistent chrome, so
   both are visible on all four replication tabs.
3. Enabled moves out of the Schedule card into the replication's own header, and autosaves independently
   of the batched draft; both Status and Schedule keep a green/orange accent for enabled/disabled.
4. A new Status card backed by a new endpoint exposing `ProcessSupervisor`'s already-tracked process
   handle (PID, memory, CPU).

## 1. Lifting the draft, and the toolbar Save button

`OverviewPanel.tsx` currently owns `draft`/`setDraft` (a `ReplicationTaskConfig` in progress) and
`upsert` (the save mutation) as local state. Move both up into `ReplicationDetailPage.tsx`, and extend
`ReplicationOutletContext` from `{ replicationName, command }` to also carry the draft and a setter (and
the save mutation, or a `save()` callback). `OverviewPanel` becomes a consumer of the outlet context for
these instead of an owner.

`ReplicationDetailPage`'s toolbar (`actions`, alongside Backfill…/Run Now/Delete) gains:

```tsx
<button className="btn btn-primary btn-chrome" onClick={save} disabled={upsert.isPending} data-testid="save-settings-button">
  {upsert.isPending ? 'Saving…' : 'Save settings'}
</button>
```

removed from its current spot inside `OverviewPanel`'s Pipeline card body.

## 2. Status and Schedule move to persistent chrome

Both cards move out of `OverviewPanel`'s 288px right column and into a new right-rail region in
`ReplicationDetailPage`, rendered as a sibling of the `Outlet` rather than inside it — so they persist
across tab changes instead of unmounting/remounting per tab. `AppShell`'s `children` prop currently takes
a single `ReactNode`; `ReplicationDetailPage` wraps `Outlet` and the new rail together into the layout it
passes as `children` (a flex row: main content left, rail right — the same shape `OverviewPanel` already
uses internally today, one level up).

Both cards should say plainly that they describe *the replication* (e.g. "Replication status",
"Replication schedule" as card titles), per the note's "clearly indicate."

## 3. Enabled moves to the replication header; Status/Schedule get the enabled/disabled accent

- **Enabled moves out of the Schedule card entirely, into the replication's own header** —
  `ReplicationDetailPage`'s title/crumb area (the `AppShell` `crumbs`/header region, persistent across all
  four tabs), not a card. Deliberately not "Schedule's card header corner": Schedule's other fields
  (mode, frequency/cron) stay in the batched Save-settings draft, and leaving Enabled inside that card —
  even in a corner — would visually imply it saves the same way as the fields next to it. Moving it to
  the replication header makes the independence visible rather than assumed.
- **Enabled autosaves.** Toggling it commits immediately: its own direct mutation, independent of
  `draft`/`upsert`. It does not wait for a Save click, and is not affected by discarding or leaving the
  rest of the draft unsaved. Simplest implementation is a small dedicated endpoint/mutation for just this
  field (e.g. `useSetReplicationEnabled(name, enabled)`) rather than routing it through the same upsert
  the batched draft uses, so toggling Enabled from a tab with no open draft (Runs, Version Control)
  doesn't accidentally save unrelated pending Pipeline/Endpoints/Scripts edits along with it.
- Schedule's card body keeps mode/frequency/cron, with no toggle of its own now — just the fields that
  belong to the batched draft.
- Both Status and Schedule keep the top-border accent reflecting enabled/disabled — green/orange, same
  tokens as before — even though the toggle that drives it no longer lives on either card.
- Add a top-border accent to both Status and Schedule cards, colored by the replication's `enabled`
  state — green when enabled, orange when disabled. New CSS tokens (`--accent-enabled`,
  `--accent-disabled`), applied the same way phase 44's `--accent-source`/`--accent-target` border accent
  works, so the visual language for "a colored top border means something about this card" stays
  consistent across the app rather than each phase inventing its own variant.

## 4. Status card and its endpoint

- **New endpoint** (e.g. `GET /api/replications/{name}/status`) reading `ProcessSupervisor`'s tracked
  `Process` for that task name, if any: `Id` (PID), `WorkingSet64` (memory), `TotalProcessorTime` (CPU
  time). Returns "not running" distinctly from "running" — no process tracked is a real, common state
  (a `Periodic`/cron replication between runs, or a `Continuous` one that's caught up and idle only if
  the worker process itself exits between cycles — confirm against `ProcessSupervisor`'s actual lifecycle
  before assuming "no process" only means "fully idle").
- **New Status card** in the rail, showing: running/not-running, and when running, PID + memory + CPU
  time. Written against "zero or one process," matching `ProcessSupervisor`'s current one-worker-per-
  replication design — not built to render a list, since the architecture doesn't produce one today.
- Pull, not push, consistent with `run-metrics.md`'s "pull is right for a dashboard nobody is staring at"
  call — no new SignalR channel needed for this.

## What this phase does not build

- A computed "health" rollup (connection tests + recent outcomes) — still deliberately deferred, per
  `run-metrics.md`.
- Historical process/resource usage (a chart, a trend) — this is live-only, current-state telemetry.
- Any change to `ProcessSupervisor`'s one-process-per-replication model.

## How to verify when built

- Save settings appears in the toolbar on Overview and is gone from inside the Pipeline card.
- Editing Pipeline/Endpoints/Scripts on Overview, then navigating to Runs and back to Overview without
  saving, does not lose the draft (confirms the lift preserved existing behavior — a page navigation
  within the layout route must not reset in-progress edits).
- Status and Schedule cards render identically (same data, same accent) on Overview, Table Mappings,
  Runs, and Version Control.
- Enabled appears in the replication header (not inside the Schedule card) and is visible/reachable on
  all four tabs.
- Toggling Enabled visibly moves the Schedule card's (and Status card's) accent between green and orange,
  and persists immediately — reloading the page (no Save click) still shows the new state.
- Toggling Enabled from the Runs or Version Control tab, with unsaved Pipeline/Endpoints/Scripts edits
  pending on the draft, commits only the Enabled change — the rest of the draft remains unsaved and
  editable exactly as it was.
- Triggering a run and checking the Status card while it's in flight shows a PID, and the card returns to
  "not running" once the worker process exits.
- Full suite green, including updated Playwright screenshots for all four replication tabs.

## Open questions

- Exact `ReplicationOutletContext` shape once it carries draft state and a save callback.
- Exact shape of the Enabled autosave mutation (dedicated endpoint vs. a narrow existing one) — an
  implementation detail, not a design one, now that autosave itself is settled.
- Exact placement of Enabled within the replication header (beside the name, beside the tab bar, or in
  the toolbar's `actions` area) — a layout detail for implementation, not a design question.
- Whether "no process tracked" needs to distinguish "idle between scheduled runs" from any other reason
  a worker isn't currently alive, or whether one "not running" state is enough for the first cut.
