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

---

# Retrospective

Built as planned. The layout move was mechanical; what it exposed was not.

## Disabling a replication has never worked

Writing the test for the Enabled endpoint found it. The YAML serializer omits defaults and compares
against `default(T)`; `false` is `default(bool)`, so it was never written, and
`ReplicationTaskConfig.Enabled`'s own `= true` initializer set it straight back on load. The Overview's
Enabled toggle has been decorative since it existed. `ScriptConfig.Enabled` had the same bug — a
disabled script reloaded enabled.

Nothing caught it because every test that had ever written one of these left it on. `[DefaultValue(true)]`
is the fix, and it is load-bearing rather than documentation: it makes the comparison against `true`,
so `false` is written and `true` is omitted, which is the right way round and keeps a committed config
from spelling out defaults nobody changed.

`Schema = "dbo"` and `Parallelism = 1` have the same *shape* and are not bugs: `default(string)` is
null and `default(int)` is 0, and neither is a value anybody sets. It is specifically a `bool` whose
initializer is `true` that cannot express its other state.

## The draft was being lost by looking at something else

`OverviewPanel` owned the draft, and it unmounts the moment somebody clicks Runs — so a half-finished
pipeline edit was discarded by a tab change, silently. Lifting it into the layout route fixes that
because that component does not unmount until the replication does.

The seeding effect deliberately does **not** re-seed when `task` changes. It cannot: the Enabled
toggle invalidates that query on every click, and re-seeding there would throw away whatever was being
edited — which is the bug this move exists to fix, reintroduced from the other end.

## Enabled saves alone, and lives somewhere else because of it

Its own endpoint, not a mode of the upsert. Routing it through the full save would mean toggling
Enabled from the Runs tab quietly committed whatever unfinished edit was sitting in the Overview's
draft — a change nobody asked for, made by a control that says nothing about it. The Playwright test
toggles it with exactly that edit pending and asserts only the toggle landed.

And it sits in the header rather than in the Schedule card for the same reason stated visually: two
controls that save differently should not sit next to each other looking alike. `enabled` is read from
the **saved** task everywhere, never from the draft, so the accent cannot flicker on save.

## Not running is the answer, not an error

A worker drains its queue and exits, so a replication that is caught up has no process between passes
— which is most of the time for most of them. The card says that in words. Left unexplained, this
would be a status card that taught operators to worry about a healthy idle replication.

Polled at five seconds rather than the metrics card's thirty, because a worker's whole life is
measured in seconds: a card refreshing twice a minute would mostly show a process that had already
exited.

## Verification

- `ReplicationChromeTests` (6) — not running reported as a state rather than an error, a missing
  replication as a 404, Enabled committing on its own in both directions, every other field left
  exactly as saved, and a missing replication refused.
- `DisablingRoundTripTests` (5) — a disabled replication and a disabled script reloading disabled, the
  other direction so the fix cannot be "always write false", disable-then-enable, and the default
  still omitted from the file.
- Playwright 25 — Save in the toolbar and gone from the pane; Status, Schedule and Enabled present on
  all four tabs; the "not running" explanation; a draft surviving a trip to Runs and back; Enabled
  toggled from Version Control with that draft still unsaved, committing only itself; both accents
  following without a Save; and the new state surviving a reload — which is the assertion that would
  have caught the serializer bug years earlier.
- Playwright 04b updated: it selected `form .card`, and the Overview is no longer a form.
- Full .NET suite green: 641 tests. Playwright: 27 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions, all four answered

- ~~**`ReplicationOutletContext`'s shape.**~~ `{ replicationName, command, draft, setDraft, saving, save }`.
- ~~**The Enabled mutation's shape.**~~ A dedicated `PUT /api/replications/{name}/enabled`, for the
  reason above — a narrow existing one would have been the upsert, which is precisely what it must not
  be.
- ~~**Where Enabled sits.**~~ In the toolbar, left of the run controls: reachable from all four tabs
  and visibly not part of the Schedule card's batched fields.
- ~~**Whether "no process" needs to distinguish idle from anything else.**~~ One state is enough for
  now, and the card explains it. `ProcessSupervisor` tracks a handle per replication and nothing else,
  so any finer answer would be inferred rather than observed — and an inferred status is the kind of
  thing that is wrong exactly when it matters.
