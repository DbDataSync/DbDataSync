# Phase 21 — A Route for Every Screen

**Status**: Built
**Plan reference**: none — asked for directly after phase 20 ("all of the pages need to have separate
routes instead of a single route").

## The problem

The replication detail screen was one route, `/replications/:name`, with its four tabs held in
`useState`. The mapping the editor had open was likewise component state inside
`TableMappingsPanel`. Three consequences, all of them the same consequence:

- Reloading dropped you back on Overview with the first mapping selected, wherever you had been.
- There was no link to send anyone. "Look at the runs for `orders-sync`" could not be a URL.
- The browser's Back button left the replication entirely rather than stepping back a tab, because as
  far as the browser was concerned nothing had happened.

## What was built

Nested routes under two layout routes:

```
/replications/:name                     ReplicationDetailPage (chrome: crumbs, tabs, run controls)
  index                                 → overview
  overview | runs | history             the three simple tabs
  mappings                              TableMappingsPanel (chrome: the mappings sidebar)
    index                               → the first mapping, or an empty state
    new                                 the create form
    :mappingName                        the editor
```

`ReplicationDetailPage` became a layout that renders an `Outlet` and passes the replication name down
through outlet context. `TableMappingsPanel` is a layout in its own right for the same reason: the
sidebar is chrome for the mapping routes, and keeping it above the `Outlet` means it does not
re-render, re-fetch or lose its scroll position when the mapping beside it changes.

The panels themselves — `OverviewPanel`, `RunsPanel`, `HistoryPanel`, `TableMappingsPanel` — take
plain props and know nothing about routing. Four thin adapters in `replication-detail/tabs.tsx` read
the outlet context and hand it over. That keeps the panels ordinary components rather than things that
only work at one URL.

## Tabs and rail items are links now

They were `<button onClick={navigate}>`. A destination with a URL should be an `<a>`: middle-click
opens a tab, right-click offers "copy link address", and the browser paints visited state and handles
Back itself. All of that was silently unavailable before.

Two CSS selectors were element-scoped (`.tabbar button.tab`) and had to become element-agnostic, plus
`text-decoration: none` on `.tab`, `.rail-item`, `.sidebar-item` and `.btn-link`. The rendered result
is pixel-identical — the Playwright screenshots are the check.

`SectionTabs` lost its `active` prop entirely. Which section is lit is a fact about the URL, and
`NavLink` already knows it; three call sites were passing an answer the router could give.

## Decisions worth recording

**The run commands stay component state.** The chrome's *Backfill…* and *Run Now* buttons reach the
Runs panel through a nonce-keyed command object, which is still `useState` on the layout — deliberately
not `location.state`. React Router preserves location state across history navigation, so an action
encoded there would re-fire when someone pressed Back onto that entry, or reloaded. Triggering a
replication run twice because the user hit refresh is not a tradeoff worth making for tidiness. The
layout does not unmount between tabs, so the state survives exactly as long as it should.

**Index routes redirect with `replace`.** `/replications/:name` → `overview` and `/mappings` → the
first mapping. Without `replace` those redirects become history entries, and Back bounces off them
instead of leaving.

**`/mappings` opens the first mapping** rather than showing an empty pane beside a populated sidebar,
which preserves the behaviour the tab already had. With no mappings at all it stays put and says so.

**A save navigates to what was saved, not to what was open.** Creating names something that had no
route a moment ago, and the form allows a rename, so `TableMappingForm`'s `onDone` became
`onSaved(mappingName)` / `onRemoved()`. One callback that meant both "saved" and "deleted" could not
have told the caller where to go.

**`/mappings/new` reserves the name "new".** A mapping literally called `new` is unreachable. This is
the same sentinel the screen already used when the selection was component state, so nothing regressed;
it is recorded here rather than fixed because no one has hit it.

**A catch-all redirects to `/replications`.** A stale or mistyped URL used to render chrome around an
empty frame.

## A bug the change surfaced

The "new mapping" placeholder in the sidebar was written as "show it when there is no mapping param",
which is also true of the index route — mid-redirect to the first mapping. It now asks the router
directly (`useMatch(.../new)`). It happened not to flicker in practice, because React Router's
`useParams` in this version surfaces the child param to the parent layout, but relying on that was the
wrong shape regardless: the condition should say what it means.

## Verification

- Playwright test 14, new: every tab click changes the URL; `/history` and `/mappings/:name` open
  cold from a deep link with the right tab lit and the right sidebar row active; a reload stays put;
  `/mappings` redirects to the first mapping and Back leaves the tab rather than bouncing; Back and
  Forward step across tabs; and a nonsense URL lands on `/replications`.
- Existing tests tightened rather than merely repaired: creating a replication now asserts it lands on
  `/overview`, the new-mapping button asserts `/mappings/new` and the placeholder row, and saving
  asserts the URL follows the saved name.
- 14 Playwright tests green. `tsc -b` clean; `oxlint` unchanged at its three pre-existing warnings
  after `tabClass` moved into its own module (a component file that also exports a helper breaks fast
  refresh).
- **No .NET change** — this phase touched no API, contract or type.

## One assertion that was wrong, not the code

The first version of test 14 checked `mapping-name-input` after a deep link. That input only exists
while *creating* a mapping — an existing one is identified by its heading — so the test was asserting
something the screen has never shown. The deep link itself worked from the first run.
