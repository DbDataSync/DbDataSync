# Phase 86 — Lag monitoring: a bulk endpoint, a Monitoring tab, and list-level stats

**Status**: Not started.
**Plan reference**: `architecture/planning/done/lag-monitoring-ui.md`

## The gap

Phase 85 built `ReaderLagService` and a single-mapping endpoint, but nothing in the SPA consumes it —
no hook, no component. Surfacing it needs three things: a way to fetch every mapping's lag in one call
(nothing like this exists today, and the existing list page already fetches N-per-row for other data, so
one bulk call per replication fits that pattern), a new tab to show it in detail, and a summary on the
top-level list.

## What to build

### Bulk endpoint

`GET api/replications/{replicationName}/lag` (or whatever matches this codebase's existing
replication-scoped-aggregate route convention — check `metrics`'s route before picking). Returns every
mapping's `MappingLag` (`types.ts:881-889`) keyed by mapping name, plus a server-computed
`lowestLagMs`/`highestLagMs` (naming your call) across mappings with an actual number. "Effective lag"
per mapping for ranking = `exactLagMs ?? estimatedLagMs`; a mapping with `supported: false` or both lag
fields null is excluded from the range entirely, not treated as zero.

### `useMappingLag`-equivalent hook

A `useReplicationLag(replicationName)` hook (`hooks.ts`, alongside `useRunMetrics` — same
`refetchInterval: 30_000` unless there's a reason to differ) wrapping the new endpoint.

### Monitoring tab

- `ReplicationDetailPage.tsx`'s `TABS` array gains an entry; a new route; a new panel component reading
  `replicationName` from `useOutletContext<ReplicationOutletContext>()` exactly like `MetricsCard.tsx`
  does (`MetricsCard.tsx:23-25`).
- Top of the panel: the replication's lowest/highest range, in `MetricsCard.tsx:71-77`'s `Figure`-string
  style (`lowest X · highest Y`) — same visual language, not a new one.
- One row per mapping: source (`{connectionName} · {database}` / `{schema}.{table}`,
  `MappingsOverview.tsx:19,109-111`'s existing convention) and target the same way, then lag. **Four
  visually distinct states, never collapsed**: a real number (labeled exact or estimated —
  `MappingLag`'s two fields are already mutually exclusive, so which one is populated tells you which
  label to show), "not applicable" (`supported: false`), and "no data yet" (`supported: true`, both lag
  fields null).

### Replications list

`ReplicationRow` (`ReplicationsPage.tsx:22-38`) gains a lag summary via one call to the same bulk
endpoint per row — the range, or just the highest value if the range is too much at list density
(implementation's call once it's laid out and visible).

## What this phase should not do

- A second aggregate-only endpoint — one bulk endpoint serves both the tab and the list.
- Extending lag to any reader kind phase 85 didn't cover (Watermark, TriggerAudit, BatchReload) — this
  phase surfaces what already exists, not new computation.
- A lag history/trend view — only current values were ever asked for.

## How to verify

- A test asserting the bulk endpoint's range calculation excludes unsupported/dataless mappings rather
  than averaging them in as zero.
- A test asserting the range correctly ranks a mix of exact (CDC) and estimated (Change Tracking past
  its DMV window) lag values against each other via the `exactLagMs ?? estimatedLagMs` rule.
- A test/story (whatever this repo's existing SPA component-testing convention is — check before
  assuming Playwright vs. something else) confirming all four lag states render distinctly.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

---

## Outcome

**Shipped.** One endpoint, two surfaces, and four states that a reader can tell apart from across
the room.

### What was built

`GET /api/replications/{name}/lag` returns every mapping's `MappingLag` keyed by name, plus
`lowestLagMs`/`highestLagMs` and `rangeIncludesEstimates`. `ReplicationLagController` is its own
controller on that route, mirroring `MetricsController` rather than hanging off
`TableMappingsController`: the figure is per-mapping but the question is about the replication, and
phase 85's per-mapping endpoint stays exactly where it is.

`useReplicationLag` polls it every thirty seconds, the same cadence `useRunMetrics` uses and for the
same reason — this is a figure somebody reads when they go looking, and the case where somebody is
watching a replication move second by second is a live run, which the run hub already covers. The
Monitoring tab and every row of the replications list share one query key, so a list of ten
replications is ten requests rather than ten times however many mappings each has.

`MonitoringPanel` is the new tab: the range at the top in `MetricsCard`'s `Figure`-string style, one
row per mapping below with source and target in `MappingsOverview`'s `{connectionName} · {database}`
over `{schema}.{table}` form, resolved through the same `resolveSide` inheritance the server
applies. `ReplicationsPage` gains a Max lag column.

### The range's flag, which the doc did not ask for

`rangeIncludesEstimates` is an addition. Ranking needs one comparable number per mapping and
`exactLagMs ?? estimatedLagMs` is that number — but that coalesce is precisely the thing phase 85
spent a response shape preventing, and its own retrospective says a consumer that coalesces them
"has said, in writing, that it does not mind." Saying so in writing is exactly what this field is.
A range that any estimate went into carries that estimate's error, which is this system's polling
interval rather than anything about the replication; the tab spells that out under the figure and
the list prefixes the number with `~`. Without it the two screens would be reporting a poll-interval
approximation in the same typeface as a fact the engine stated, which is the one thing phase 85's
whole shape exists to prevent.

### Deviations from the doc

**The panel takes `replicationName` as a prop; a thin `MonitoringTab` in `tabs.tsx` reads the outlet
context.** The doc says to read `useOutletContext` in the panel "exactly like `MetricsCard.tsx`
does" — but `MetricsCard` does not do that, it takes props (`MetricsCard.tsx:23`), and the actual
convention in this folder is the one `tabs.tsx` documents: panels stay routing-unaware and a thin
adapter per tab supplies the name. Following the doc literally would have made this the only panel
that cannot be mounted anywhere but under its own route.

**The list shows only the highest, not the range** — the call the plan doc explicitly left until it
was laid out and visible. At list density the range is two numbers in a column narrow enough that
neither is legible, and the question a list answers is "which of these needs looking at". That is
the highest; the low end never changes the answer, and the full range is one click away on the row
it belongs to. The screenshot is `61-replications-lag-column.png`.

### Judgment calls

- **`lowestLagMs`/`highestLagMs`, and milliseconds throughout**, matching `MappingLag`'s
  `exactLagMs`/`estimatedLagMs` and, behind those, `TaskRuns`' own `ReaderLifetimeMs`. A client can
  do arithmetic on a number without parsing `"00:04:13.5"` first.
- **The controller walks its mappings sequentially, not in parallel.** Most answer out of the
  polling history the gate already wrote, but the ones that do not open a connection to the source,
  and a forty-mapping replication fanning those out at once would make a status screen a load spike
  on the database it is reporting about.
- **A `data-lag-state` attribute on each row**, carrying the same four-way distinction the styling
  carries. The requirement is that the four states be *distinguishable*, and an attribute makes that
  assertable rather than only visible — and re-states it for a reader who is not distinguishing the
  badges by colour.
- **`formatLag` is not `MetricsCard`'s `formatMs`.** That one tops out at minutes, because a pass
  taking longer than an hour is the exception there; a lag of hours is the case this whole screen
  exists for.

### The bug the UI test found

`formatLag`'s first cut copied `formatAgo`'s 90-minute and 48-hour overhangs — which exist to stop
"an hour and a half ago" reading as a round number it isn't — and so rendered an hour of lag as
`60m`. Three of the four Playwright assertions failed on it. That is exactly the "a number somebody
has to divide before they know whether to care" problem the function's own doc comment claims to
solve, shipped in the function making the claim. The boundaries are the units' own now.

### How SPA components are tested here, and what that meant

There is no component-test runner in this repo — no vitest, no testing-library — and
`src/DataSync.Web/package.json` has no `test` script. The convention is `tests/DataSync.Web.Tests`, a
single serial Playwright golden path against a real API, a real git repo and a real SQL Server.

`lag-monitoring.spec.ts` (4 tests) follows that convention but stubs every request the screen makes
at the network boundary, including the replication itself, so it depends on nothing the golden path
leaves behind and runs in any order beside it. That is deliberate rather than lazy: the four states
are properties of the *payload* — a mapping is unsupported because of its reader kind, has no data
because it has never run, and is estimated rather than exact because its version aged out of
`sys.dm_tran_commit_table`. Producing all four against real databases means four replications, a
reader that cannot report lag, a mapping deliberately left unrun, and a DMV window nothing can make
time pass in: hours of fixture to assert something about rendering. `ReplicationLagTests` covers the
server's half against the real service and real config; this covers the client's half against the
shape the server sends; the two meet at `ReplicationLag`.

The four-state assertion is that the four cells' text differs *from each other*, not that each
contains some word — "all four render distinctly" is a claim about a set, and a per-cell assertion
would pass on four cells that all read the same.

### How it was verified

- `ReplicationLagTests` (new, 9): six over `ReplicationLag.From` directly — the exclusion of
  unsupported and dataless mappings from the range (asserting five minutes, not zero), the
  exact-vs-estimated ranking in **both** directions because a comparison that only ever sees the
  exact figure at one end would pass a one-way test by accident, the estimate flag including the
  case where an estimate exists but did not reach the range, and the null-not-zero range. Three over
  the endpoint, on a replication whose two mappings reach two different states through the real
  service — one inheriting Change Tracking and estimated from real polling history, one overriding
  to a reader with no database-wide counter — proving the same function is the one wired to the
  route.
- `lag-monitoring.spec.ts` (new, 4, Playwright): the four states, the range and its estimate
  caveat, the list column, and the replication where nothing can report lag saying so rather than
  showing zero. Screenshots `60-monitoring-tab.png` and `61-replications-lag-column.png`.
- Full Playwright suite **49 passed** — the golden path's 45 unaffected by the new tab and the new
  list column.
- Full suite: `Category!=Integration` **1105 passed, 18 failed**; `Category=Integration` **213
  passed, 0 failed**. `tsc -b`, the SPA build and `oxlint` clean on every file this phase touched.

### Pre-existing failures, confirmed as such

All 18 were confirmed by stashing this phase's work and re-running: the identical 18, with 1096
passed instead of 1105. None is in a file this phase touched.

- The 9 phase 85 recorded, unchanged: `InviteCommandTests.Invite_AgainstAnMsSqlConfiguredRepo_
  Succeeds`, the two `AdminConfigControllerTests` file-source tests, five Windows-only tests in
  `DataSync.Certificates.Tests`, and `CertificateExpiryServiceWindowsTests`.
- 9 more that arrived with phase 83 after phase 85's count was taken: the whole of
  `AdminCertificateServiceWindowsTests`, Windows-only and unable to pass on Linux for the same
  reason the rest of that arc's tests cannot.

### What this phase did not do

No second aggregate-only endpoint, no lag history or trend view, and no lag for any reader kind
phase 85 did not cover — Watermark, TriggerAudit and BatchReload still render "not applicable"
through the same shape, which is what the fast-follow that gives them a figure will fill in without
touching either screen.
