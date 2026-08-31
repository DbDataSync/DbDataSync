# Surfacing the reader/staging/writer timing trace in the UI

**Status: resolved 2026-08-31 — a confirmed gap, in two parts.**

## The gap

Phase 59 (done) built the opt-in timing trace end to end on the backend: `TableMappingConfig.TraceTiming`
(off by default), a generic stream decorator for reader time-to-first-row and lifetime, wrapped-call
stopwatches for staging and writer duration, and seven new nullable `TaskRuns` columns
(`ReaderKind`/`ReaderTimeToFirstRowMs`/`ReaderLifetimeMs`/`StagingKind`/`StagingDurationMs`/`WriterKind`/
`WriterDurationMs`). Its retrospective names the gap directly: no dashboard was built, by design — but
confirmed by inspection, **nothing in the SPA references any of it at all**:

- `TableMappingConfig`'s `TraceTiming` flag has no toggle anywhere — it can only be turned on by
  hand-editing config, the same gap phase 61 closes for segmenting strategies.
- The TypeScript `TaskRunRecord` type (`api/types.ts`) doesn't declare the seven new fields, so even
  though the API almost certainly already serializes them (they're on the same record `RunsController`
  returns), the SPA can't read them — `RunsPanel.tsx` shows none of it.

This is smaller than phase 61: mostly frontend, since the data is already flowing over HTTP.

## Design

**1. The opt-in toggle.** A small control on `TableMappingForm.tsx` — near the other mapping-level
settings (provisioning, default segmenting) — for `TraceTiming`. A plain on/off, no `INHERITED` badge
(phase 59 confirmed this is mapping-level only, no replication layer).

**2. Surfacing the data in run history.** `RunsPanel.tsx`'s run history table is already dense
(`COLUMNS` currently has eight fields); adding seven more as columns would repeat phase 47's row-alignment
problem and clutter every mapping whether traced or not. Better fit: an expandable detail — clicking a
traced run's row (or a small indicator icon shown only when that run has timing data) reveals
Reader/Staging/Writer Kind and duration inline, collapsed by default. An untraced run (the common case,
since this is opt-in) shows nothing extra at all — no empty columns, no visual noise for mappings that
never turned this on.

## What this does not change

- Any backend behavior from phase 59 — the decorator, the columns, the opt-in semantics are unchanged.
- No aggregate/percentile view — still explicitly out of scope, per phase 59's own retrospective; this is
  a single-run detail view, not a dashboard.

---

# Outcome

Agreed, as `implementation/todo/phase-062-timing-trace-ui.md`.
