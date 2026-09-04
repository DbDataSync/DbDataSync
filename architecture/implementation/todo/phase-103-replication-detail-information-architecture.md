# Phase 103 — replication detail: Runs under Monitoring, Schedule on Overview, countdowns in their cards

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/replication-detail-ux-improvements.md`, resolved
2026-09-04. Items 1, 3 and 4 of four; item 2 is phase 104.

Three moves, one idea: **put the thing where it belongs.** Each takes something out of shared chrome
and returns it to the thing it describes. They are one phase because two of them meet at the same
place — the Monitoring pane's header — and separating them would mean touching it twice.

**This phase must land before phase 102**, which puts intent and hold controls on the Monitoring tab.
102 is third in a chain behind 100 and 101, neither started, so re-targeting it now is free; building
it first would mean designing controls against a layout that then moves under them.

## 1. Runs becomes a sub-tab of Monitoring

Five top-level tabs become four. `Monitoring` becomes a layout route with two sub-tabs, using the
`SubTabs` convention Overview and the mapping editor already follow:

| sub-tab | route | element |
| --- | --- | --- |
| **Current Status** | index (`path: null`) | `MonitoringPanel` |
| **Run History** | `history` | `RunsPanel` |

`path: null` for the first is not incidental — it is the documented `SubTabs` convention, and it means
`/replications/{name}/monitoring` opens what the section is *for* rather than requiring a segment.

The labels are spelled out rather than the shorter Current/History first proposed: "Run History" says
what it holds without depending on its neighbour for context, which matters in a browser tab title and
in a link someone pastes.

### `/runs` must redirect, not 404

Phase 21 made routes for every screen a deliberate feature, so every saved bookmark and pasted link to
`/replications/{name}/runs` is real. It becomes
`<Route path="runs" element={<Navigate to="../monitoring/history" replace />} />` — the same
`Navigate replace` pattern `App.tsx` already uses for its index routes.

Dropping it instead would break links silently: a 404 on a URL that worked yesterday, with nothing
saying where the screen went.

### `RunsCommand` needs re-checking, not moving

Run Now and Backfill stay in the page chrome. Triggering a run is a replication-level action, not
something done while reading history, and it should keep working from Overview and Table Mappings
without navigating first.

What changes is only that the panel they reach is now two levels down a routed outlet rather than one.
The chrome already survives tab changes — that is why the buttons live there — so the plumbing should
hold; it needs verifying rather than redesigning.

## 2. Schedule moves to the Overview tab

`ScheduleCard` leaves `detail-rail` and lands in `OverviewPanel`, between `EndpointsCard` and
`SubTabs`, as a **single-line** card rather than the full-height one it is today.

**The stated reason it was in the rail no longer applies.** `ReplicationDetailPage`'s doc comment says:

> Status and Schedule live here … so they are the same cards showing the same thing on every tab
> rather than one mount of them per tab.

The unspoken worry is losing an edit when a card unmounts — but since phase 46 the **draft is owned by
the page**, and `ScheduleCard` already takes `draft` and `onChange` as props. `OverviewPanel` reads the
same draft from the outlet context. So the card can unmount and remount freely with nothing lost, and
that comment should be corrected in this phase rather than left describing a rule that no longer holds.

**What it does cost, accepted knowingly:** the schedule stops being readable from the other four tabs.

**The rail keeps `StatusCard` and `MetricsCard` and is not otherwise touched.** Dissolving it is a
bigger change than anything asked for here, and arriving at it as a side effect of moving one card
would be the wrong way to decide it.

## 3. Each countdown moves into the header of what it describes

Three `RefreshCountdown`s are portalled into the page header through `<ShellActions>`. Which ones are
up there depends on the open tab, so the header shows a varying set of countdowns sitting nowhere near
the data they describe. `MetricsCard`'s own comment already concedes the shape of the problem:

> This card sits in the detail rail on every tab, so its countdown is the one constant of the three —
> the other two come and go with whichever tab is open.

Each moves into the header of its own card or pane:

- **Metrics** → `MetricsCard`'s `card-head`, beside the window selector.
- **Lag** → the Current Status sub-tab's header. This settles a question the planning doc left open —
  the countdown describes the whole pane, both the range card and the mapping table, and before this
  restructure there was no pane-level header to put it in.
- **Runs** → the Run History header, keeping its `intervalMs={interval}` override, which reports the
  interval actually in force rather than the constant.

### `ShellActions` is deleted

Those three are its only users anywhere in the app. Removed: `ShellActions.tsx`, `shellActionsSlot.ts`,
and `AppShell`'s action-slot state and action-bar wiring.

This reverses phase 88, which built the portal for exactly this purpose, and the phase doc should say
so plainly rather than quietly dropping a file. Git history keeps that reasoning if the need returns.
Machinery kept "in case" is how a codebase accumulates things nobody dares remove.

## How it will be verified

**E2E** (`tests/DbDataSync.Web.Tests`) — this phase is entirely presentational, so this is where it is
actually proven:

- `/replications/{name}/runs` lands on Run History, with the URL rewritten. The redirect is the piece
  most likely to be dropped as an afterthought and the one an operator notices first.
- both sub-tabs render their own content, and `/monitoring` with no segment opens Current Status
- Run Now still triggers from the Overview tab — the chrome plumbing, from a tab that is not Monitoring
- the Schedule card renders on Overview between the endpoints and the sub-tab bar, and an edit made
  there survives switching to another top-level tab and back (the draft-ownership claim above, asserted
  rather than assumed)
- each countdown renders inside its own card, and **the shell's action bar contains none of them** —
  both halves, since asserting only the first would pass with the portal still populated

**Screenshots.** `tests/DbDataSync.Web.Tests/screenshots/` carries a shot of each screen and several
now show the old layout. Refreshing them is part of this phase, not a follow-up.

## Decisions

- **Spelled-out sub-tab labels**, "Current Status" and "Run History".
- **A redirect, not a removal**, for `/runs`.
- **Run controls stay in the chrome** — a replication-level action does not belong inside one sub-tab.
- **The rail keeps Status and Metrics.** Dissolving it is a separate decision.
- **`ShellActions` is deleted rather than kept unused.**

## Out of scope

- **Runs filtering and paging** — phase 104. This phase moves the panel; it does not change what it
  shows or how much of it.
- **Dissolving the detail rail.**
- **Phase 102's intent and hold controls.** They land on Current Status *after* this; 102's doc should
  be edited when this lands so its description of the tab matches what is there. Its central argument —
  that Monitoring is already the per-mapping operational view — survives unchanged.

## Open questions to resolve during implementation

- **Does `MetricsCard` belong on Current Status rather than the rail?** It is arguably as "current" as
  the lag table, and the sub-tab's name invites the question. Not moving it here — that is rail-scope,
  deliberately deferred — but the answer will look obvious once both are on screen, and it is worth
  looking at then rather than pretending the question was not raised.
- **What the single-line Schedule card shows when the schedule is complex.** A cron expression with a
  window and a paused state may not fit one line; whether it truncates, wraps, or keeps a compact
  two-line form is a judgement best made against the real strings.
