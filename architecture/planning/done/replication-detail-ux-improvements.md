# Replication detail: four UX changes

**Status: resolved 2026-09-04 — see Outcome at the end.** Four things asked for together on 2026-09-04. They are not one
change — one of them has a server half and one collides with a phase already planned — but they are
all about the same screen and are worth thinking about at once, because three of the four move
something *out* of shared chrome and into the thing it describes, and that is a single idea.

## The four asks

1. **Move the Runs tab into Monitoring**, with the current monitoring data shown as **Current** and the
   run history as **History**.
2. **Better filtering on the runs list** — by kind, mapping and status — and **paging back through
   older records**.
3. **Move the replication schedule out of the right-hand rail and onto the Overview tab**, as a
   single-line card between the source/target cards and the tab area below them.
4. **Move the metrics countdown and the lag countdown into the header of the card they describe**,
   rather than the page header.

## What is there today

**Five tabs** — Overview, Table Mappings, Runs, Monitoring, Version Control
(`ReplicationDetailPage.tsx:19-25`).

**A right-hand rail** (`detail-rail`) carrying `StatusCard`, `ScheduleCard` and `MetricsCard`, mounted
beside the `Outlet` rather than inside it.

**Three countdowns, all portalled into the page header.** `MetricsCard`, `MonitoringPanel` and
`RunsPanel` each wrap a `RefreshCountdown` in `<ShellActions>`, which `createPortal`s it into
`AppShell`'s action bar (phase 88).

**Runs filtering is client-side, over one page.** `RunsPanel` fetches a window and then filters what it
already has:

```js
filter === 'failed' ? r.status === 'Failed'
: filter === 'backfills' ? r.runKind === 'Backfill'
: true
```

The server endpoint is `GET replications/{name}/runs?kind=&limit=50` — so `kind` is a real server
filter, `limit` is a fixed window, and there is **no offset, cursor, mapping filter or status filter**.

## 1. Runs into Monitoring

Straightforward on the surface: `SubTabs` already exists and Overview already uses it, so Current /
History is a shape the app has.

**It collides with `phase-102-monitoring-tab-manages-intent-and-hold.md`, which is planned and not
started.** That phase puts per-mapping read intent and hold controls on `MappingLagRow`, arguing:

> Because it is already the per-mapping operational view … Putting them anywhere else would mean an
> operator who has just been told on this screen that something is wrong has to leave it to do
> anything about it.

That argument survives the restructure — those controls land on **Current** — but the two plans cannot
be written independently, and whichever is built second inherits the other's layout. This needs
sequencing before either becomes a phase.

**Two things that are easy to miss:**

- **The route moves, and routes here are a deliberate feature.** Phase 21 was "routes for every
  screen". `/runs` becoming `/monitoring/history` breaks every bookmark and every link anyone has
  saved, so a redirect is part of the work rather than a nicety.
- **`RunsCommand`.** The Run Now / Backfill buttons live in the page chrome specifically so they
  survive tab changes and still reach the Runs panel. Whether they still make sense in the chrome when
  Runs is a sub-tab two levels down, or should move next to the history list, is a real question and
  not obviously answered either way.

Also worth asking plainly: is **Current** the right word for the lag table? It answers "how far behind
is each mapping", which is current, but the Metrics card is arguably just as current and lives in the
rail. If Current/History is the frame, it is worth checking nothing else belongs under it.

## 2. Runs filtering and paging

**These two cannot be built independently, and that is the whole point of the item.** Filtering today
is applied to whatever the last 50 rows happened to be. Add paging and that behaviour becomes a lie: a
status filter that searched one page would show "no failed runs" for a replication with plenty of them,
just not in the newest fifty. **So the filters have to move server-side as part of adding paging**, or
the two features actively contradict each other.

What the server would need: `mappingName` and `status` alongside the existing `kind`, plus a way to ask
for older records.

**Offset or cursor is a genuine question.** Runs are append-heavy and the list polls, so offset paging
shifts under the reader — new runs arrive at the top and page 2 quietly re-shows rows from page 1. A
keyset cursor on `(enqueuedAtUtc, runId)` does not have that problem. Offset is simpler and the app has
one already (`useVerificationResult` pages by offset/limit), so there is a house precedent pulling the
other way.

**The companion endpoint has to keep step.** `runs/watermark-times` takes the same `kind` and `limit`
and is joined to the history list client-side. If the list gains filters and pages and that endpoint
does not, the two disagree and the watermark column goes blank for rows that have one.

**Live refresh versus paging.** The list refetches every 1.5s while a run is being watched. Somebody
reading page 4 of last month's runs should not have it move under them — paging probably has to
suspend the live poll, or the poll has to apply only to page one. Worth deciding rather than
discovering.

## 3. Schedule onto the Overview tab

The insertion point is unambiguous: `OverviewPanel.tsx` renders `EndpointsCard` (the source/target
pair) and then `SubTabs`, so "between the source/target cards and the tab area" is exactly between
those two.

**This contradicts a stated design decision, and the reason for it is now partly obsolete** — which is
the interesting part. `ReplicationDetailPage`'s own doc comment says:

> Status and Schedule live here for the same reason — a rail beside the `Outlet` rather than inside
> it, so they are the same cards showing the same thing on every tab rather than one mount of them per
> tab.

The unspoken worry there is losing edits when a card unmounts. But since phase 46 the **draft is owned
by the page, not by the card** — `ScheduleCard` takes `draft` and `onChange` as props. So moving it
into Overview costs nothing in draft survival; what it costs is that the schedule stops being visible
from the other four tabs. Whether that matters is a judgement about how often somebody reads the
schedule while doing something else, and nobody has said.

**What is the rail for afterwards?** Remove Schedule and it holds Status and Metrics. Combined with
item 4 moving the metrics countdown into its own card, it is worth asking whether the rail still earns
a column of screen width, or whether Status and Metrics also belong somewhere — which would be a bigger
change than any of the four asked for, and should not be smuggled in as a consequence.

## 4. Countdowns into their own card headers

The clearest of the four, and the current code already admits the problem. `MetricsCard`:

> This card sits in the detail rail on every tab, so its countdown is the one constant of the three —
> the other two come and go with whichever tab is open.

So the header shows **a varying set of unlabelled-by-position countdowns** depending on which tab is
open: always Metrics, plus Lag on Monitoring, plus Runs on Runs. They carry text labels, which is the
only thing making them tellable apart, and they are nowhere near the data they describe.

**One consequence to decide deliberately: this retires `ShellActions` entirely.** Those three
countdowns are its only users anywhere in the app. Phase 88 built the portal for exactly this purpose,
so item 4 is a reversal of a recent decision, and the honest options are to delete the mechanism or to
keep it unused for a future need — not to leave it half-used by accident.

**Where does the Lag countdown go?** Metrics and Runs each have one obvious card. Monitoring's
countdown describes the whole pane — the range card *and* the mapping table are both from the one
`useReplicationLag` call. If the pane keeps two cards, the countdown has to pick one or the pane needs
a header of its own. Item 1 may settle this by giving Current a sub-tab header to sit in.

---

# Outcome — resolved 2026-09-04

Every open question above was answered in conversation. **Two phases**, now written:

- **`implementation/todo/phase-103-replication-detail-information-architecture.md`** — items 1, 3, 4.
- **`implementation/todo/phase-104-run-history-filtering-and-paging.md`** — item 2.

## Phase 103 — the information architecture (items 1, 3, 4)

All client-side, all the same idea: put the thing where it belongs. Items 1 and 4 both touch the
Monitoring pane header, so splitting them apart would cost more than it saves.

- **Runs moves under Monitoring** as two sub-tabs. The labels are **"Current Status"** and **"Run
  History"** — not the shorter Current/History first proposed, because the pair reads better spelled
  out and "Run History" says what it holds without depending on its neighbour for context.
- **`/runs` gets a redirect** to the new route. Phase 21 made routes for every screen a real feature;
  breaking saved links is part of the cost of moving one, and paying it is cheap.
- **Run Now and Backfill stay in the page chrome.** Triggering a run is a replication-level action,
  not something done while reading history, and it should keep working from any tab. Only the
  `RunsCommand` plumbing that reaches the panel needs re-checking against the deeper route.
- **Schedule moves to the Overview tab**, a single-line card between `EndpointsCard` and `SubTabs`.
  The rail keeps `StatusCard` and `MetricsCard` and is not otherwise touched — dissolving it is a
  bigger change than anything asked for here and should not arrive as a side effect. The cost being
  accepted knowingly: the schedule stops being readable from the other four tabs.
- **Each countdown moves into the header of what it describes**, and **`ShellActions` is deleted** —
  the portal, its context slot, and `AppShell`'s action-bar wiring. Those three countdowns are its
  only users anywhere, so this reverses phase 88; git history keeps that reasoning if the need
  returns, and machinery kept "in case" is how a codebase accumulates things nobody dares remove.
- The Lag countdown's home was an open question and the restructure settles it: **Current Status is a
  sub-tab with its own header**, which is where it goes.

## Phase 104 — runs filtering and paging (item 2)

Its own phase because it has a server half: new endpoint parameters, a keyset query, and a companion
endpoint that has to keep step.

- **Filtering moves server-side.** This is not a preference — today's filter runs over whatever the
  last 50 rows were, and once paging exists that behaviour becomes a lie: a status filter searching one
  page would report "no failed runs" for a replication with plenty, just not in the newest fifty.
  Filters and paging cannot be built independently.
- **`mappingName` and `status` join the existing `kind`** on `GET replications/{name}/runs`.
- **Keyset cursor on `(enqueuedAtUtc, runId)`**, not offset. Runs are append-heavy and the list polls,
  so offset paging genuinely duplicates and skips rows rather than theoretically. This departs from
  `useVerificationResult`'s offset/limit precedent, and the reason it departs belongs in the phase doc
  so it does not read as an inconsistency someone should tidy up later.
- **`runs/watermark-times` takes the same filters and the same page.** It is joined to the history list
  client-side, so a list that filters and pages while its companion does not would blank the watermark
  column for rows that have one.
- **Polling continues only on the first page.** Older pages are a stable keyset window and go static
  until the reader returns to the top, so nothing moves under someone reading history and the live view
  behaves exactly as it does today.

## Sequencing

**Phase 103 first, then 104, and both before phase 102.** 102 is third in a chain behind phases 100
and 101, neither of which has started, so it is far enough off that re-targeting it costs nothing now.
Doing the restructure first means 102's intent and hold controls get designed onto **Current Status**
rather than built against a layout that then moves under them.

`phase-102-monitoring-tab-manages-intent-and-hold.md` should be edited when phase 103 lands, so its
description of the Monitoring tab matches what is actually there. Its central argument — that
Monitoring is already the per-mapping operational view and the controls belong beside the lag they
explain — survives the restructure unchanged.
