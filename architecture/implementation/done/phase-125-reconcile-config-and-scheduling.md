# Phase 125 — delete reconciliation: `ReconcileConfig`, a scheduled cadence, and an after-change strategy

**Status**: Done.
**Plan reference**: `architecture/planning/done/watermark-delete-detection.md`. Phase 2 of that design —
built on phase 124 (the `KeyReconcile` reader/writer, `DeleteGuard`, `RunKind.ReconcileDeletes`,
`ReconcileService`), confirmed already in `done/` before starting.

## What this built

### `ReconcileConfig` + `AfterChangeStrategy` — `src/DbDataSync.Core/Config/`

Exactly the shape the plan specified: `ReconcileConfig` (`Enabled`, `Every: SchedulingConfig?`,
`AfterChange`, `DeleteGuard`, and nullable `Reader`/`Cache`/`Writer` overrides), `AfterChangeStrategy`
(`None`/`AfterAny`, a `[JsonPolymorphic]` hierarchy like `DeleteGuard`'s own), `AfterChangeEvaluator.
ShouldReconcile`. Added to `ReplicationTaskConfig.Reconcile` (non-nullable, defaulted — the same shape
`ProvisioningConfig` already has, not the C# `required` keyword, which would have forced every existing
call site across the test suite to set it) and `TableMappingConfig.ReconcileOverride` (nullable, inherits).
`PipelineResolution` grew `Reconcile`/`ReconcileReaderKind`/`ReconcileCacheKind`/`ReconcileWriterKind`/
`LevelOfReconcile`, mirroring its existing `Reader`/`Cache`/`Writer` methods — using plain string literals
for the default Kinds (`"KeyReconcile"` etc.), the same `Core`-cannot-reference-`Drivers` constraint
`ConfigValidation` already works under.

**A gap the phase 124 retrospective explicitly deferred to this phase, now closed**: `DeleteGuardYamlConverter`
and a new `AfterChangeStrategyYamlConverter`, both mirroring `BatchReloadSegmentYamlConverter` exactly —
YamlDotNet cannot construct an abstract type or discriminate a derived one without a hand-written
`IYamlTypeConverter`, and `ReconcileConfig` is the first thing that actually persists `DeleteGuard`/
`AfterChangeStrategy` to YAML (phase 124 only ever needed their JSON form, for the ephemeral work-item
options channel). Registered in `YamlConfigSerializer` alongside `BatchReloadSegmentYamlConverter`.

### `ConfigValidation.ValidateReconcile`

Reuses `ValidateKeyReconcilePairing` verbatim when `Enabled` (a scheduled sweep needs the identical
cached, fully-mapped primary key an on-demand one does), reuses `ValidateScheduling` for `Every` when
set, and resolves the plan's own open question — "the after-change floor when Every is unset" — as
**require `Every` whenever `AfterChange` is not `NoAfterChangeStrategy`**, exactly the plan's stated
leaning. Hooked into `ConfigRepository.SaveTableMapping` beside phase 124's own `ValidateKeyReconcilePairing`
call.

### `TaskRunStore` + `WorkQueueStore` — three new queries

- `GetLastReconcileEnqueueByMapping` — a direct sibling of `GetLastPrimaryEnqueueByMapping`, filtered to
  `RunKind.ReconcileDeletes`.
- `GetRowsReadSincePerMapping(taskName, sinceByMapping)` — the after-change trigger's own input. Resolves
  the plan's other open question ("one correlated statement per task, or a per-candidate `SumRowsRead`")
  as neither exactly: one bounded query (floored at the *earliest* cutoff among the mappings asked
  about, since each mapping's own cutoff — its last sweep — differs), summed per mapping in memory
  against its own cutoff. One round trip per tick, the same batching reasoning
  `GetLastPrimaryEnqueueByMapping`'s own doc comment already gives.
- `WorkQueueStore.HasPendingReconcile(taskName, mappingName)` — resolves the plan's "`HasPendingReconcile`
  vs. reusing `HasOutstandingWork`" question in favor of a dedicated, mapping-scoped method:
  `HasOutstandingWork` only scopes by lane, which can't tell "this mapping's own sweep is in flight"
  from "some other mapping's backfill is."

### `ReconcileService.EnqueueScheduledAsync`

The scheduler's own entry point, alongside phase 124's operator-facing `EnqueueAsync`: segments come from
the mapping's `DefaultSegmenting` (empty means `Full`, the same convention a standalone reload replication
already uses) rather than a request body, and the guard is always the resolved `ReconcileConfig.DeleteGuard`
— explicitly serialized onto every enqueued work item now, never left to the writer's own hardcoded
fallback (which only existed for a work item nothing here had written yet). The on-demand endpoint from
phase 124 was updated to match: it now resolves the mapping's own configured guard when `overrideGuard`
is false, rather than silently trusting the writer's 50%-default regardless of what an operator configured.

### `WorkQueue.DeleteGuardJson` — a real gap in phase 124's own plan, closed here

The phase 124 plan said the guard "rides the writer options via a `WithGuard(...)` helper mirroring
`RunExecutor.WithSegment`" — but no channel existed for a work item to carry a guard at all; `WorkQueue`
only ever had `ReaderKind`/`CacheKind`/`WriterKind` and a segment. Added a new nullable
`WorkQueue.DeleteGuardJson` column (additive migration, same precedent those three Kind columns
themselves set), threaded through `WorkItem`, `WorkQueueStore.Enqueue`, and a new `RunExecutor.WithGuard`
that injects it into the writer's options exactly like `WithSegment` injects a segment — null (no
override) for every item before this phase and every `Primary`/`Backfill`/`Verification` item after.

### `SchedulerService.TickReconcileAsync`

Runs every tick, for every replication, independent of the replication's own `Scheduling.Mode` (a sweep
has its own `SchedulingConfig`). For each mapping with `Reconcile.Enabled`, computes cadence-due and
after-change-due, dedups against `HasPendingReconcile`, and calls `ReconcileService.EnqueueScheduledAsync`.

**A real logical bug found and fixed while writing the first scheduler test**: the plan's own "cadence
due: `Every is not null && IsDue(...)`" and "after-change due: `ShouldReconcile(...)`, floored by the
cadence" — implemented literally, both checks share the identical `IsDue(Every, lastEnqueue, now)` gate,
which means `afterChangeDue` can only ever be true when `cadenceDue` is *already* true — so
`cadenceDue || afterChangeDue` collapses to just `cadenceDue`, and `AfterChangeStrategy` would have had
**zero observable effect** in any configuration (it's required to carry a non-null `Every`, so this
collapse was unconditional). Fixed by splitting the two: `NoAfterChangeStrategy` is the *only* mode where
`Every` is an unconditional "sweep no matter what" trigger; any other strategy makes `Every` stop being
unconditional and become purely the after-change floor — a sweep then needs *both* enough time elapsed
*and* something to react to. This is what actually gives `AfterAnyChangeStrategy` a real, distinguishable
effect from `NoAfterChangeStrategy`.

### `RunExecutor` — the guard injection only

No `RunKind`-branching change (the plan's own claim held, same as phase 124's); `WithGuard` is the one
addition, described above.

### SPA

`ReconcileConfigCard` (new) — cadence (inline, not a reuse of `ScheduleCard`, which is written directly
against `ReplicationTaskConfig.scheduling` and not worth generalizing for one second consumer), an
after-change picker, and a guard picker with a `MaxRatio` percentage field — on the replication's Pipeline
tab, beside the Change Processing editor it complements. `types.ts` gained `ReconcileConfig`/
`AfterChangeStrategy`/`DeleteGuard` and the two new config fields; the new-replication default in
`ReplicationsPage.tsx` carries a disabled one (`reconcile` is non-optional on the wire type, so this was
a required fix, not a nice-to-have). `RECONCILE_ONLY_KINDS`'s exclusion (phase 124) turned out to be
missing from one more picker discovered while wiring this up — `MappingPipelineCard`'s own per-mapping
reader/writer override selects — fixed alongside.

## How it was verified

- `dotnet build DbDataSync.slnx` clean; `npm run build`/`lint` clean (same pre-existing
  `react-hooks/exhaustive-deps` class of warning, none new).
- **`AfterChangeEvaluatorTests`** (4 tests): `None` never reconciles regardless of rows read; `Any`
  reconciles only when rows read `> 0`; JSON round-trip for both.
- **`ReconcileConfigYamlRoundTripTests`** (4 tests, real git-backed `ConfigRepository`): a `NoneDeleteGuard`
  + `NoAfterChangeStrategy` round-trips; a `RatioDeleteGuard`'s `MaxRatio` survives; an
  `AfterAnyChangeStrategy` with an `Every` cadence round-trips; a replication that never touches
  `Reconcile` loads a disabled config with the documented defaults (`RatioDeleteGuard`, `NoAfterChangeStrategy`,
  no `Every`) — the "this is what a fresh replication actually gets" case the plan's own default values
  promise.
- **`ReconcileValidationTests`** (8 tests): disabled skips every check regardless of how invalid the rest
  is; the `KeyReconcile` pair and a keyless source are both rejected/accepted exactly as phase 124's own
  pairing tests are; an after-change strategy with no `Every` is rejected naming "cadence"; the same with
  an `Every` set passes; no-after-change-and-no-`Every` (a purely on-demand configuration) passes; an
  invalid `Every` (`FrequencySeconds: 0`) is caught by the existing `ValidateScheduling` rules reused
  here.
- **`SchedulerServiceReconcileTests`** (5 tests) — the doc's own "fake clock, in-memory state" ask,
  realized the same way the existing `SchedulerServiceHoldTests` already does it: `SchedulerService.TickAsync`
  invoked directly by reflection against real local config/state (a real git-backed `ConfigRepository`,
  real SQLite `TaskRunStore`/`WorkQueueStore`) but an ungated `Watermark` reader Kind, so nothing here
  ever touches a real database — no `Category=Integration` tag needed, matching the existing file's own
  precedent. A cadence-due mapping enqueues one sweep per configured segment; `NoAfterChangeStrategy` with
  no `Every` never enqueues even after a fabricated Primary pass reads 500 rows; `AfterAnyChangeStrategy`
  enqueues once after a fabricated Primary pass reads rows and not again until more arrive; the cadence
  floor caps after-change frequency even when more rows arrive between ticks; an already-in-flight sweep
  is never duplicated. **This is the suite that caught the cadence/after-change collapse bug** — the very
  first version of `AfterAnyChangeStrategy_EnqueuesOnceAfterRowsAreRead_...` failed by enqueuing on the
  first tick before any rows had been read at all, which is what led to tracing the logic back to the
  `IsDue`-sharing bug above.
- Full non-integration suites green: `DbDataSync.Core.Tests` (238, up from 221 before this phase).
  A full `DbDataSync.Api.Tests` run (495 tests) caught two pre-existing contract tests that phase 124's
  new `KeyReconcile`/`KeyReconcileDelete` registration made stale, neither a phase 125 defect:
  `ChangeReaderFirstPassContractTests.EveryChangeReader_IsDeclaringOrExempt` didn't yet know
  `KeyReconcileReader` was exempt from declaring read intents (same reasoning as `BatchReloadReader` —
  it always reads the whole scope), and `ConnectionsControllerTests.Capabilities_ReportsWhatTheRegisteredDriverActuallySupports`
  hardcoded the MsSql driver's segmenting-reader and reconciling-writer kind lists, which now also include
  `KeyReconcile` and `KeyReconcileDelete`. Both updated; full suite green afterward (495 passed).

## Decisions made

- **`Reconcile` is a plain defaulted property, not the C# `required` keyword** — matching
  `ProvisioningConfig`'s own precedent on `ReplicationTaskConfig`, and deliberately not `required`, which
  would have forced every one of the dozens of existing `new ReplicationTaskConfig { ... }` call sites
  across the test suite (and the SPA's own "create a replication" object literal) to set it explicitly.
- **The cadence/after-change interaction was redesigned, not just implemented as literally stated** — the
  plan's own phrasing ("floored by the cadence") turns out to admit two readings, and the literal one
  makes `AfterChangeStrategy` a no-op. Resolved in favor of the reading that gives the feature real
  meaning: `Every` is unconditional only under `NoAfterChangeStrategy`; any other strategy repurposes it
  as a pure floor.
- **`WorkQueue.DeleteGuardJson` is a new column, not a repurposing of the existing segment channel** —
  keeps the guard and the segment as two independent, individually-nullable per-item values, exactly
  parallel to how `ReaderKind`/`CacheKind`/`WriterKind` already sit beside `SegmentJson` rather than being
  folded into it.
- **No mapping-level `ReconcileOverride` editor in the SPA** — the backend fully supports it
  (`PipelineResolution.Reconcile` already resolves `mapping.ReconcileOverride ?? task.Reconcile`), but the
  UI only exposes the replication-level card given the scope already covered this session. A real, scoped
  gap — see below.
- **No new Playwright coverage for `ReconcileConfigCard`/`ReconcileDeletesForm`**, despite
  `BackfillForm`'s own equivalent living in `golden-path.spec.ts` — that file is a very large, carefully
  sequenced single spec, and inserting into it without a dedicated pass to understand its full
  fixture/ordering dependencies risked destabilizing an existing, CI-enforced suite for marginal
  additional confidence beyond what real HTTP+worker integration tests (this phase's and phase 124's)
  and a clean TypeScript build already provide. Flagged rather than silently skipped.

## What this does not build

- New `AfterChangeStrategy` variants beyond `None`/`AfterAny` — the hierarchy is the extension point, as
  planned.
- New `DeleteGuard` variants beyond phase 124's `None`/`Ratio`.
- Moving segmenting off the mapping.
- A worker-side after-change hook — the scheduler owns the decision, as planned.
- `ReconcileBatches` (the batch rollup) — the plan left this an explicit "decide during implementation"
  question; decided here as **not built**, since the plain per-`ReconcileDeletes`-run history (already
  filterable by kind since phase 124) was judged to read clearly enough for v1, and the scope already
  covered this session did not leave room to build and verify a second rollup store/UI on top of it.
- A mapping-level `ReconcileOverride` editor in the SPA (see "Decisions made" above) — the config model
  and every backend consumer already support it; only the UI affordance is missing.

## Open questions — resolved

1. **`rowsReadSinceLastSweep` query shape** — resolved: one bounded batch query (floored at the earliest
   per-mapping cutoff), not N separate calls — see `TaskRunStore.GetRowsReadSincePerMapping` above.
2. **The after-change floor when `Every` is unset** — resolved per the plan's own leaning: `Every` is
   required whenever `AfterChange` is not `None`, enforced by `ConfigValidation.ValidateReconcile`.
3. **Whether the batch rollup is in scope** — resolved: not this phase (see "What this does not build").
4. **`HasPendingReconcile` vs. reusing `HasOutstandingWork`** — resolved: a dedicated,
   mapping-scoped method, since `HasOutstandingWork` cannot express "this specific mapping" at all.
