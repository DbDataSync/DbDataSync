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
