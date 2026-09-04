# Phase 103 — replication detail: Runs under Monitoring, Schedule on Overview, countdowns in their cards

**Status**: Done.
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

---

## Outcome

Both open questions above were answered during implementation — see Judgement calls below. Everything
in items 1–3 of the summary at the top of this file landed as designed; nothing was descoped.

### What was built

**Routing (`src/DbDataSync.Web/src/App.tsx`, `pages/ReplicationDetailPage.tsx`,
`pages/replication-detail/tabs.tsx`):**

- `ReplicationDetailPage`'s `TABS` array drops `runs`; four top-level tabs now (Overview, Table
  Mappings, Monitoring, Version Control).
- `<Route path="runs" element={<Navigate to="../monitoring/history" replace />} />`, exactly the
  pattern the doc specified.
- `monitoring` becomes a layout route: `<Route index element={<MonitoringCurrentStatusTab />} />` and
  `<Route path="history" element={<MonitoringRunHistoryTab />} />`, both new. `tabs.tsx`'s
  `MonitoringTab` (the thin outlet-context adapter App.tsx mounts at `path="monitoring"`) now renders
  `MonitoringSection`, a new layout component living in `MonitoringPanel.tsx` beside the panel it
  always held, on the same "thin router adapter + real layout component" split `OverviewTab`/
  `OverviewPanel` already use.
- `RunsCommand`'s target changed from `/runs` to `/monitoring/history` in `ReplicationDetailPage`'s
  `send()` — see Judgement calls for why this was changed rather than left to ride the redirect.
- `ReplicationOutletContext` gained an `enabled: boolean` field (the **saved** enabled state), threaded
  from `ReplicationDetailPage` through `OverviewTab` into `OverviewPanel`, so `ScheduleCard` — now
  mounted inside `OverviewPanel` rather than beside the outlet — can still read it without a second
  fetch of its own.

**Monitoring restructure (`pages/replication-detail/MonitoringPanel.tsx`, new content in an existing
file rather than a new one, to keep the layout and the panel it wraps next to each other):**

- `MonitoringSection` — the layout: `SubTabs` (Current Status / Run History, `path: null` on the
  first) over an `<Outlet>`, mirroring `OverviewPanel`'s own shape exactly.
- `MonitoringCurrentStatusTab` / `MonitoringRunHistoryTab` — thin outlet-context adapters, rendering
  the existing `MonitoringPanel` and `RunsPanel` respectively.
- `MonitoringPanel` itself (the lag table) lost its own `.pane` wrapper (the new `MonitoringSection`
  owns that now) and gained a `.pane-head` — new markup, new CSS class — holding the "Current status"
  title and the Lag countdown, since this pane genuinely had no header of its own before there were
  sub-tabs to need one.

**Run history (`pages/replication-detail/RunsPanel.tsx`):** lost its own `.pane` wrapper for the same
reason; the Runs countdown moved from the deleted `ShellActions` portal into the existing "Run history"
card's own `card-head`, beside the filter chips, keeping its `intervalMs={interval}` override verbatim.

**Metrics (`pages/replication-detail/MetricsCard.tsx`):** the countdown moved from the portal into the
card's own existing `card-head`, beside the window-selector buttons — no new markup needed, since this
card already had a header to put it in.

**Schedule (`pages/replication-detail/ScheduleCard.tsx`, rewritten; `OverviewPanel.tsx`):** the card is
now a single `card-head` with no `card-body` — mode select, then the mode-specific fields, inline —
mounted in `OverviewPanel` between `EndpointsCard` and `SubTabs`. Removed from `ReplicationDetailPage`'s
`detail-rail`, which now holds only `StatusCard` and `MetricsCard`. `ReplicationDetailPage`'s own doc
comment, which justified the rail on "avoids losing an edit when a card unmounts," is corrected to say
plainly that the worry was already stale (draft ownership moved to the page in phase 46) and that
Schedule moved on the strength of that correction.

**`ShellActions` deleted**, confirmed by grep to have exactly the three call sites the doc named before
touching anything: `ShellActions.tsx`, `shellActionsSlot.ts`, and `AppShell`'s `actionSlot` state,
`ShellActionsSlot.Provider` wrapper, and `.shell-actions` span all removed. `index.css` lost the
`.shell-actions` rule and gained `.pane-head`. `AppShell`'s own doc comment gained a paragraph saying
plainly that phase 103 reverses phase 88, per the phase doc's own instruction not to drop this quietly.

**Tests:** `tests/DbDataSync.Web.Tests/tests/monitoring-restructure.spec.ts` (new) covers all five E2E
bullets from "How it will be verified" directly, stubbed at the network boundary in the style of
`lag-monitoring.spec.ts`. `golden-path.spec.ts` and `runs-watermarks-refresh.spec.ts` — both written
against the pre-103 five-tab layout — needed real updates, not just renames, to keep asserting what
they always asserted rather than something that happened to still pass; see Judgement calls.

### How it was verified

**`tsc --noEmit` and `npm run build`** (`src/DbDataSync.Web`): both clean.

**E2E.** This sandbox has no `docker` at all (`where docker` and `Get-Command docker` both come back
empty), and `global-setup.ts` shells out to `docker exec` unconditionally for every spec in the suite —
not only `golden-path.spec.ts`, which is the only file that actually touches SQL Server. That is a
harder blocker than the "known environment note"'s webServer flakiness, and it means the real
`playwright.config.ts` cannot run *any* spec in this sandbox, `monitoring-restructure.spec.ts` included.

Worked around for verification purposes only, never committed: built the API once
(`dotnet build src/DbDataSync.Api`, already done), started it by hand with the same env vars
`playwright.config.ts` uses, started `vite` by hand, and pointed a scratch Playwright config with no
`globalSetup`/`globalTeardown`/`webServer` at the two already-running servers. Every spec that stubs
its replication data at the network boundary needs neither Docker nor golden-path's SQL Server fixture,
so this reaches everything except `golden-path.spec.ts` itself:

- `monitoring-restructure.spec.ts` (new, 5 tests) — **all pass.**
- `lag-monitoring.spec.ts` (5), `run-details-dialog.spec.ts` (4), `runs-watermarks-refresh.spec.ts` (4),
  `mapping-metadata-cache.spec.ts` (4), `mapping-column-add.spec.ts` (5), `duckdb-query-source.spec.ts`
  (4) — **all pass**, unchanged behaviour confirmed after the restructure (only
  `runs-watermarks-refresh.spec.ts` needed an edit — a `tab-runs` click became
  `monitoring-tab-history`).
- `golden-path.spec.ts` (real SQL Server, real end-to-end runs) — **not run**, no SQL Server reachable
  in this sandbox. Every reference to the old five-tab layout in it was found by grep and updated by
  reasoning about the new routes and DOM shape (see Judgement calls for the ones that needed more than
  a rename) — `tsc`/the manual server confirm the app itself is correct, but this file's own assertions
  against real data are unverified.

**Screenshots.** Regenerated via the manual run above, for exactly the spec files phase 103 changed the
rendered output of: `60-monitoring-tab.png`, `62-runs-pid-and-watermarks.png`,
`63-refresh-countdowns.png`, plus four new ones from `monitoring-restructure.spec.ts`
(`80`–`83`) and three from `run-details-dialog.spec.ts` that had never been committed before at all
(`70`–`72` — a pre-existing gap unrelated to this phase, filled incidentally because that spec's own
route now renders the new layout). Screenshots from specs this phase did not touch
(`mapping-metadata-cache`, `duckdb-query-source`, `mapping-column-add`, the replications list) came out
byte-different on this run too — almost certainly relative-time text rendering against today's system
clock rather than anything this phase changed — and were reverted rather than churned for no reason
tied to this phase. `golden-path.spec.ts`'s own screenshots that phase 103 affects
(`09-run-history.png`, `13-run-history-with-backfill.png`, `28-overview-endpoints.png`,
`33-replication-chrome-disabled.png`, `43-detail-layout.png`, and the two live-run ones) could not be
regenerated at all, for the same missing-SQL-Server reason `golden-path.spec.ts` itself could not run —
recorded here rather than silently left stale.

### Judgement calls

- **`MonitoringSection` as a new component name, `MonitoringPanel` kept for the lag-table content.**
  The doc's table names the index route's element `MonitoringPanel` — that stays literally true; the
  *new* layout wrapper needed its own name since `MonitoringPanel` was already taken, and
  `MonitoringSection` mirrors how `OverviewPanel` is the layout and `OverviewNotesTab` etc. are its
  thin sub-tab adapters, just with the names swapped (here the pre-existing name was the content, not
  the layout).
- **`.pane-head` is new, but only Lag uses it.** Metrics' and Runs' countdowns both had an existing
  `card-head` to land in (`MetricsCard`'s own, and the "Run history" card's) — matching the pattern
  the doc's item 3 states for them ("MetricsCard's own card-head", "the Run History header" read as
  that card's header). Lag is the one with no card that speaks for the whole pane — the range card and
  the mapping table are two separate cards under one `useReplicationLag` call — which is exactly what
  the doc's item 3 already anticipated needing a new pane-level header for.
- **`RunsCommand`'s navigate target changed from `/runs` to `/monitoring/history`, not left to ride the
  new redirect.** Leaving it as `/runs` would still work — the redirect gets it to the same place — but
  it means every Run Now/Backfill click pays for an extra client-side redirect hop it does not need to,
  for no benefit; the chrome already knows exactly where the panel it is commanding lives.
- **The single-line Schedule card wraps rather than truncates or scrolls, resolving the second open
  question.** `card-head`'s flex row is given `flexWrap: 'wrap'`; a short schedule (the common case)
  reads as one line, and a schedule whose fields plus the trailing explanatory text do not fit wraps to
  a second line rather than silently cutting off a value somebody actually typed in. Screenshotted with
  an ordinary Continuous schedule (`82-overview-schedule.png`) — a genuinely long cron expression was
  not tested against real rendering, so the wrap behaviour is verified as *present* rather than as
  *sufficient* for every string.
- **`MetricsCard` was not moved onto Current Status, resolving the first open question by leaving it
  where it was.** The doc marks this rail-scope and deliberately deferred, and out of scope says
  dissolving the rail further is a separate decision — moving one card off it while leaving Status
  would be exactly that decision, arrived at as a side effect rather than on its own.
- **`golden-path.spec.ts` needed more than a `tab-runs` → `tab-monitoring` rename in three places.**
  Test 14 ("every screen has its own URL") asserted `/runs` stayed `/runs` through Back/Forward
  navigation — now false, since the redirect uses `replace` and leaves no history entry of its own —
  so it was rewritten to navigate to `/monitoring/history` directly and to assert the redirect
  separately. Test 25 asserted the Schedule card was visible on every tab; rewritten so only Status
  is checked across all four tabs and Schedule is checked once, on Overview, matching what the phase
  intentionally gave up. Test 35 asserted `.pane > .card`'s top matched `.detail-rail > .card`'s — a
  selector that stopped matching anything real anywhere in the app after this phase, since Monitoring
  now has a sub-tab bar above its first card the same way Overview already did (and `EndpointsCard`'s
  own outer element was never a `.card` to begin with, so Overview was never actually a candidate
  either). Rewritten to compare `.detail-main > .pane`'s own padded content edge against
  `.detail-rail`'s directly — the invariant the original defect was actually about — rather than
  depending on a DOM shape that no longer exists on any route.
- **The E2E harness's real blocker in this sandbox is Docker's absence, not (only) the webServer
  flakiness the task described.** Found and fixed the webServer issue anyway, since it was reachable
  once servers were started by hand: `npm run dev -- --port 5173 --strictPort` binds vite to `[::1]`
  only on this machine, so the config's own IPv4 readiness probe (`http://127.0.0.1:5173`) never
  connects and the entry times out at 30s even though vite itself is ready in under one second — this
  is almost certainly the exact flakiness the task's environment note described. Fixed in
  `playwright.config.ts` by adding `--host 127.0.0.1` to that command, verified by reproducing the
  failure and then the fix manually. This does not touch product code and directly serves the phase's
  own verification bar for whoever runs this suite next somewhere Docker exists.
