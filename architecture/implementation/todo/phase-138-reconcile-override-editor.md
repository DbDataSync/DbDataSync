# Phase 138 — a mapping-level Delete Reconciliation override, in the SPA

**Status**: Planned, not started.
**Plan reference**: none — found during a 2026-09-14 audit of recently-shipped backend work with no
matching SPA surface (see `architecture/implementation/README.md`'s 2026-09-14 update). Closes a gap
phase 125's own retrospective named directly: *"No mapping-level `ReconcileOverride` editor in the
SPA — the backend fully supports it... only the UI affordance is missing."*

## Why

`TableMappingConfig.ReconcileOverride` (`ReconcileConfig?`) has existed since phase 125
(`src/DbDataSync.Core/Config/TableMappingConfig.cs:184`), `PipelineResolution.Reconcile` already
resolves `mapping?.ReconcileOverride ?? task.Reconcile` (`PipelineResolution.cs:58`), and the SPA's own
`TableMappingConfig` type has carried `reconcileOverride?: ReconcileConfig | null` since the same
phase, with a doc comment that already says exactly what this phase builds: *"This mapping's own
delete-reconciliation settings, in place of the replication's. Null inherits
`ReplicationTaskConfig.reconcile` entirely."* Nothing has read or written that field from the SPA since
it was added — confirmed directly: no reference to `reconcileOverride` anywhere in
`TableMappingForm.tsx`.

This is a pure SPA phase. No backend change of any kind.

## What this phase will build

### 1. `reconcileOverride` joins `TableMappingForm`'s local state

Alongside the existing `pipeline` (`PipelineOverrides`) state, a sibling `useState<ReconcileConfig |
null>(existing?.reconcileOverride ?? null)`. Added to the save payload the same way `pipeline` already
is (`TableMappingForm.tsx`'s `save` spreads `...existing` first, so this is additive — no other field's
save behavior changes).

### 2. A toggle-wrapped `ReconcileConfigCard`, reused as-is

`ReconcileConfigCard` (`src/DbDataSync.Web/src/pages/replication-detail/ReconcileConfigCard.tsx`) is
already exactly the right editor — it takes a `ReconcileConfig` and an `onChange`, nothing about it is
replication-specific. Wrap it in the same whole-object inherit/override toggle
`MappingPipelineCard.toggleOverride` already establishes for a per-stage override (INHERITED badge,
"Override here" toggle button, seeded from the currently-effective value when turned on, back to `null`
when turned off) — not `InheritableToggle`, which is boolean-only.

```tsx
const effectiveReconcile = reconcileOverride ?? task?.reconcile
const overridingReconcile = reconcileOverride !== null

const toggleReconcileOverride = () =>
  setReconcileOverride(overridingReconcile ? null : structuredClone(effectiveReconcile))
```

Placed on the mapping editor's **Pipeline** tab (`TableMappingForm.tsx`, where `MappingPipelineCard`
already renders), directly below it — the replication-level equivalent sits the same way, directly
below the Change Processing card, on `OverviewPanel.tsx`'s Pipeline area. Same tab, same relative
position, one level down.

### 2a. Scope: match the replication-level card exactly

**Decided, not left open**: the override edits `Enabled`, `Every` (cadence), `AfterChange`, and
`DeleteGuard` — exactly what `ReconcileConfigCard` already exposes today. `ReconcileConfig.Reader`/
`Cache`/`Writer` stay unexposed at both levels; their own doc comment calls them "present mainly for
the same structural reason `ChangeProcessingConfig.Reader` carries `Options`, not because a different
Kind is meaningful here" — always `KeyReconcile`/`StagingTable`/`KeyReconcileDelete` (or
`KeyReconcileScd2Close`, phase 129) in practice. Building an editor for a case that has never come up
is scope this phase does not need.

### 3. `writerKind` for the reused card

`ReconcileConfigCard` needs the mapping's *effective* Change Processing writer Kind (to show which
reconcile writer — `KeyReconcileDelete` vs `KeyReconcileScd2Close` — a sweep will actually resolve to,
per phase 129). At the mapping level this is `kindOf('writer')` — already computed in
`MappingPipelineCard` from `overrides.writerOverride ?? inherited.writer`, not from the replication's
own `task.changeProcessing.writer` directly, since a mapping's writer may itself be overridden.
`TableMappingForm` will need to derive this the same way (or `MappingPipelineCard` could export the
helper) rather than duplicating a subtly different calculation.

## How it will be verified

- `npm run build` / `npm run lint` clean — the only kind of "test" most of this repo's pure-SPA phases
  get (see phase 087, phase 101, phase 109j's own SPA sections for precedent); no new backend tests
  possible since nothing backend changes.
- Manual: toggle the override on a mapping, change cadence/after-change/guard, save, reload the page,
  confirm the override round-trips (proves the save-payload wiring, not just the component rendering).
  Toggle it back off, save, confirm the mapping goes back to showing the replication's own settings —
  proves `null` (not an empty/default object) is what "off" writes.
- No new Playwright coverage, for the same reason phase 125 gave for `ReconcileConfigCard` itself:
  `golden-path.spec.ts` is a large, carefully sequenced single spec, and inserting into it without a
  dedicated pass on its fixture/ordering risked more than the marginal confidence gained, on top of a
  clean TypeScript build. Flagged, not silently skipped.

## Decisions

- **Scope matches the replication-level card exactly** (enabled/cadence/after-change/guard) — see
  "2a" above. Confirmed with the user 2026-09-14 rather than left as an open question.
- **Reuse `ReconcileConfigCard` directly**, not a parallel mapping-specific component — it already takes
  exactly the props needed (`ReconcileConfig`, `writerKind`, `onChange`), and nothing about its rendering
  assumes replication scope.
- **The toggle pattern mirrors `MappingPipelineCard.toggleOverride`** (a whole-object override, seeded
  from the current effective value), not `InheritableToggle` (boolean-only) or a from-scratch design.

## What this does not build

- Reader/Cache/Writer sub-overrides within the Reconcile override (see "2a").
- Any backend change — the config model, validation, resolution, and the scheduler have supported this
  since phase 125.
- A `LevelOfReconcile`/"where does this actually come from" indicator beyond the existing INHERITED
  badge convention — `PipelineResolution.LevelOfReconcile` exists server-side but nothing else in this
  UI surfaces a server-computed binding level either; the client-side `reconcileOverride !== null` check
  is sufficient and consistent with every other override toggle in this file.

## Open questions to resolve during implementation

- **Whether `MappingPipelineCard` should export its `kindOf`-style writer-resolution helper**, or
  whether `TableMappingForm` recomputes the equivalent locally. Small either way; worth a look at how
  much `MappingPipelineCard`'s internals can be reused without exporting more than intended.
