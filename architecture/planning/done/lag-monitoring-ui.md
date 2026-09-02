# Surfacing reader lag: a Monitoring tab, list-level stats, and a per-replication range

**Status: resolved — ready for an implementation phase doc.**

## What's there today, confirmed by reading the code

Phase 85 built the computation and a single-mapping API
(`GET api/replications/{replicationName}/table-mappings/{mappingName}/lag`,
`TableMappingsController.cs:47-65`) but nothing in the SPA calls it — no `useMappingLag` hook, no
component renders `MappingLag`. There is **no bulk endpoint**: every existing per-mapping figure in this
codebase (reader kind, schedule, mapping count) is already fetched N-per-row even on the list page
(`ReplicationsPage.tsx`'s `ReplicationRow` calls `useReplication`/`useTableMappings` per row — not a
single list-with-detail endpoint), but lag specifically has no endpoint at all yet, bulk or otherwise.

`MetricsCard.tsx`'s p50/p95/max pattern (phase 36) is the right precedent for the "lowest and highest"
ask: one server-computed aggregate object per replication, not the client reducing over N individual
fetches — the server already has (or, here, computes) the full mapping list, so let it do the
arithmetic once.

`MappingsOverview.tsx` already has the display convention for "a mapping's source and target" —
`{connectionName} · {database}` for the endpoint, `{schema}.{table}` for the table identity
(`MappingsOverview.tsx:19,109-111`) — reuse it rather than inventing new formatting for the Monitoring
tab.

`MappingLag`'s shape (`types.ts:881-889`): `{ readerKind, supported, exactLagMs, versionsBehind,
estimatedLagMs }`, with `exactLagMs`/`estimatedLagMs` mutually exclusive by phase 85's own design — a
mapping can also be `supported: false` (no lag capability at all, e.g. BatchReload/Watermark/TriggerAudit
today) or `supported: true` with both lag fields null (no data yet — first pass, or no polling history).
Three distinct states, and the UI must keep them distinct: an unsupported mapping reads "not applicable,"
a supported-but-dataless one reads "no data yet," and only a populated one shows a number.

## Design

### One new bulk endpoint, reused by both surfaces

`GET api/replications/{replicationName}/lag` (exact route is implementation's call — mirror whatever
convention `metrics`/other replication-scoped aggregate endpoints already use), returning every mapping's
`MappingLag` (keyed by mapping name) **plus a server-computed replication-level range**: lowest and
highest lag across mappings that actually have a number. "Effective lag" for ranking purposes is
`exactLagMs ?? estimatedLagMs` — the two are already mutually exclusive per mapping, so this never loses
information, it just gives one comparable figure to rank by. Mappings with no number (unsupported, or
supported-but-dataless) are excluded from the range, not treated as zero — the same "not applicable
isn't a dash that reads like zero" discipline `run-lag.md` established from the start.

One endpoint, one computation, consumed by both the Monitoring tab (full per-mapping breakdown) and the
replications list (just the range) — avoids two endpoints computing the same aggregate slightly
differently, and matches the list page's existing N-per-row fetch style (one bulk call per replication
row, not per-mapping).

### Monitoring tab

New entry in `ReplicationDetailPage.tsx`'s `TABS` array, a new route, a new panel component taking
`replicationName` from `useOutletContext` exactly like `MetricsCard` does. Shows:

- The replication-level lowest/highest range at the top, in the same `Figure`-string style
  `MetricsCard.tsx:71-77` uses for its percentiles (`lowest X · highest Y`), not a new visual language.
- One row per mapping: source (`{connectionName} · {database}`, `{schema}.{table}`) and target the same
  way, and its lag — exact/estimated/not-applicable/no-data-yet, all four states visibly distinct, not
  collapsed into a single "—" that could mean any of the last three.

Poll on the same cadence `MetricsCard` already uses (`refetchInterval: 30_000`) unless a reason to differ
turns up during implementation.

### Replications list

Add a lag summary to `ReplicationRow` (`ReplicationsPage.tsx:22-38`) — the range (or just the highest
value, if the range reads as clutter at list density; implementation's call once it's actually laid out)
via one call to the new bulk endpoint per row, consistent with how the row already fetches its other
per-replication data.

## What this phase should not do

- A second, separate aggregate-only endpoint — the bulk endpoint serves both surfaces.
- Extending lag to Watermark mode or any other still-unsupported reader — this phase surfaces what phase
  85 already computes (CDC, Change Tracking), nothing new on the computation side.
- Historical lag trends/graphs — phase 15's mockups and `run-lag.md` both only ever asked for current
  values, not a time series.

## How to verify

- A test asserting the bulk endpoint's range excludes unsupported/dataless mappings rather than
  averaging them in as zero.
- A test asserting the range picks `exactLagMs ?? estimatedLagMs` consistently — a replication mixing a
  CDC mapping (exact) and a Change Tracking mapping past its DMV window (estimated) ranks both
  correctly against each other.
- A component/UI-level check (however this repo tests SPA components — check existing precedent) that
  all four lag states (exact, estimated, not-applicable, no-data-yet) render visibly differently from
  each other.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-086-lag-monitoring-ui.md`.
