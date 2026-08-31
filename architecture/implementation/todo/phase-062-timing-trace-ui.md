# Phase 62 — Surfacing the timing trace in the UI (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/timing-trace-ui.md`

## What this covers

The frontend half of phase 59's opt-in timing trace: a toggle for `TraceTiming` on the mapping editor, and
an expandable per-run detail view in run history showing the seven timing columns phase 59 already writes.

## 1. `TraceTiming` toggle

`TableMappingForm.tsx` gains a plain on/off control for `TraceTiming`, alongside the mapping's other
settings (provisioning, default segmenting). No `INHERITED` badge — confirmed mapping-level only, no
replication layer, per phase 59.

## 2. `TaskRunRecord` type and run history detail

- `api/types.ts`'s `TaskRunRecord` gains the seven optional fields (`readerKind`, `readerTimeToFirstRowMs`,
  `readerLifetimeMs`, `stagingKind`, `stagingDurationMs`, `writerKind`, `writerDurationMs`) — confirm
  against the actual `RunsController` response shape (likely already serializing them; this may be a
  types-only change with no backend edit needed).
- `RunsPanel.tsx`: a run whose record carries timing data gets a small indicator; clicking/expanding that
  row reveals Reader/Staging/Writer Kind and duration inline. A run with no timing data (the common,
  untraced case) shows nothing extra — no empty columns added to the base table, avoiding phase 47's
  row-alignment problem for the common case.

## What this phase does not build

- Any backend change to phase 59's decorator, columns, or opt-in semantics.
- An aggregate/percentile dashboard — still out of scope, this is single-run detail only.
- A replication-level default/override layer for `TraceTiming`.

## How to verify when built

- Toggling `TraceTiming` on for a mapping, running it, and opening that run in history shows all seven
  values; an untraced run shows the plain row with no expansion affordance.
- `ReaderTimeToFirstRowMs <= ReaderLifetimeMs` is visibly true in the rendered detail (sanity-checking the
  display against phase 59's own invariant, not just the raw data).
- Toggling `TraceTiming` off stops new runs from carrying timing data, and old traced runs still display
  correctly (nothing retroactively breaks).
- Full suite green, including a Playwright flow: enable tracing, run, expand the row, see the numbers.

## Open questions

- None — scope is fully determined by what phase 59 already shipped.
