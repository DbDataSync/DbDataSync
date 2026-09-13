# Phase 129 — SCD2 delete detection via `KeyReconcileScd2Close`

**Status**: Done.
**Plan reference**: `architecture/planning/done/scd2-delete-detection.md`, itself building on
`architecture/planning/done/watermark-delete-detection.md` and phases 124/125. Built largely as
specified, in 6 commits (`e0602e7`..`ec7e4dc`) plus this retrospective; one real gap the spec didn't
name turned up while proving the feature end to end (see "Decisions made").

## What this built

### `KeyReconcileScd2CloseWriter` / `KeyReconcileScd2CloseStatement` — `src/DbDataSync.Drivers.Generic/`

Modelled directly on `KeyReconcileDeleteWriter`: the same count-then-act-then-guard shape, one
transaction, two separate `SegmentScope` instances (a `DbParameter` belongs to only one
`DbCommand.Parameters` collection at a time). Two differences from its sibling: the anti-join key is
the **natural key** (read via `Scd2Writer.NaturalKeyOption`), not `shape.PrimaryKeyColumns` — an SCD2
target's real primary key is the generated surrogate (`DS_VersionKey`), which would match nothing in
staging — and the act is an `UPDATE` that sets `DS_ValidTo`/`DS_IsCurrent`, never a `DELETE`.

`Scd2Writer.SplitColumns` changed from `private` to `internal` so the new writer reuses its exact
required/unmapped-key checks rather than duplicating them — a mapping's primary `Scd2` writer and its
reconcile-close companion can never disagree about what identifies a row. No behavior change to
`Scd2Writer` itself; the method is still invisible outside `DbDataSync.Drivers.Generic`.

New `GenericDriverKinds.KeyReconcileScd2Close` constant. Registered on `MsSqlDriver` and
`PostgresDriver`, immediately after `KeyReconcileDeleteWriter`, the same place phase 124 added that
one. No new reader — `KeyReconcileReader` is reused exactly as phase 124 built it.

### `RunExecutor` and `PipelineResolution` — the two places "unset" needed a new meaning

`RunExecutor.WithDerivedNaturalKeyAsync`'s gate widened from `writerKind != GenericDriverKinds.Scd2`
to also recognize `GenericDriverKinds.KeyReconcileScd2Close` — same derivation, same injected
`Scd2Writer.NaturalKeyOption`, so a reconcile-close pass derives the natural key exactly the way a
primary `Scd2` pass already does when nothing is stated.

`PipelineResolution.ReconcileWriterKind`'s unset default stopped being an unconditional
`"KeyReconcileDelete"` literal and now branches on the mapping's own primary writer:
`"KeyReconcileScd2Close"` when it's `"Scd2"`, `"KeyReconcileDelete"` otherwise. Plain string literals,
not `GenericDriverKinds` constants — `DbDataSync.Core` has no project reference to
`DbDataSync.Drivers.Generic` (confirmed empty `<ItemGroup>` of `ProjectReference` entries in its
`.csproj`), the same reason `ConfigValidation`'s own pairing check already uses literals. An explicit
`ReconcileConfig.Writer` override still wins outright — this only changed what "unset" resolves to.

### Validation — `ConfigValidation.ValidateKeyReconcilePairing`, widened in place

The writer check widened from an equality test (must be `KeyReconcileDelete`) to a membership test
(must be `KeyReconcileDelete` or `KeyReconcileScd2Close`), plus two checks that apply only to the
`Scd2` ending and need a value the check never needed before — the mapping's own primary writer, Kind
and Options:

1. The mapping's own writer must actually be `Scd2`. Closing a version on a target that isn't
   versioned is meaningless.
2. A stated Scd2 natural key must name exactly the source's primary key (translated through the
   column mappings) — the only columns `KeyReconcileReader` actually stages. Anything else would have
   the new writer join on columns the staging table doesn't have.

The stated-key check duplicates the tiny `c.IsPrimaryKey` predicate inline rather than calling
`Drivers.Generic`'s `NaturalKeyDerivation` — `Core` cannot reference the driver layer (confirmed via
the empty `ProjectReference` list, same as above), and `Drivers.Generic` already references `Core`, so
the reverse reference the plan doc's original wording implied would have been circular and would not
compile.

Both new checks live inside `ValidateKeyReconcilePairing` itself, not a new sibling function — it's
called from two places (`ConfigRepository.SaveTableMapping` directly, and again inside
`ValidateReconcile`), and both already compute `PipelineResolution.Writer(task, mapping)` for
`ValidateHistorizedTarget` at the same call site, so both supply the two new parameters with no new
lookup. `ValidateReconcile`'s own signature widened the same way, forwarding to
`ValidateKeyReconcilePairing`. `ConfigRepository.SaveTableMapping` computes `primaryWriter` once and
reuses it at all three call sites.

### SPA — a real correction to what the plan doc assumed existed

`RECONCILE_ONLY_KINDS` gained `'KeyReconcileScd2Close'`, excluded from the ordinary Change Processing
pickers the same way its siblings already are — as planned.

The plan doc claimed `ReconcileConfigCard` "needs no new field — the writer picker it already has just
gains a new option." Reading the component in full during implementation showed **there is no writer
picker in it at all** — it renders a fixed, static hint string ("Always the
`KeyReconcile`/`KeyReconcileDelete` pair…"). That sentence becomes factually wrong the moment the
resolved writer can vary. Fixed by threading `OverviewPanel`'s already-in-scope
`draft.changeProcessing.writer.kind` down as a new `writerKind` prop, and rendering
`KeyReconcileScd2Close` in the hint when it's `'Scd2'`, `KeyReconcileDelete` otherwise — mirroring
`PipelineResolution.ReconcileWriterKind`'s own default logic client-side. Small and real, but not the
writer-picker change the plan described.

`ReconcileDeletesForm.tsx`'s own hint ("removes target rows whose key is no longer there… Never
inserts or updates") is also factually wrong for a mapping that resolves to `KeyReconcileScd2Close`,
which closes a version rather than removing a row. Made conditional via a new
`resolvedReconcileWriterKind` helper mirroring `PipelineResolution.ReconcileWriterKind`'s full
precedence (an explicit override wins outright; otherwise the mapping's own primary writer decides).

Everything else the plan's open question named — the "Reconcile deletes" button label,
`useReconcileDeletes`, `ReconcileDeletesRequest` — was left exactly as decided: the concept stays
honest even though "delete" is inexact for an `Scd2` mapping, and the run's own log lines and
`RunKindBadge` already say what actually happened.

### A real gap the plan doc never named — `ReconcileService` itself

Every change above teaches something what the *default* writer Kind should be. Proving the feature
end to end (see below) found that nothing had yet taught the one caller that actually matters for a
real request to *ask* for it: `ReconcileService` (`POST
/api/replications/{r}/mappings/{m}/reconcile-deletes`, and the scheduler's own
`EnqueueScheduledAsync`) had its writer Kind hardcoded to a private `const string WriterKind =
"KeyReconcileDelete"`, set at phase 124 and never revisited. Neither the plan doc nor this phase's own
first five checkpoints named this file. Fixed by resolving `PipelineResolution.ReconcileWriterKind(task,
mapping)` fresh in both of `ReconcileService`'s enqueue methods — the same per-call resolution
`ConfigRepository.SaveTableMapping` already uses for validation. Without this fix, an `Scd2` mapping's
on-demand or scheduled reconcile sweep would have enqueued a `KeyReconcileDelete` work item against a
target with no such primary key, failing at run time regardless of what the resolved default said —
exactly the class of gap phase 124's own retrospective found in its plan ("the plan's own claim glossed
over a real wrinkle"), found here the same way: by actually driving the feature through its real,
external entry point rather than only at the unit level.

## How it was verified

- `dotnet build` clean (0 errors, 0 warnings) at every checkpoint.
- **`DbDataSync.Drivers.Generic.Tests`**: 172 passed — the new `KeyReconcileScd2CloseStatementTests`
  (count, close-statement shape, natural-key-not-surrogate join, composite key, both dialects).
- **`DbDataSync.Core.Tests`**: 247 passed (up from 238) — `KeyReconcilePairingValidationTests` and
  `ReconcileValidationTests`, existing cases updated for the widened signature plus new cases for the
  `Scd2` pairing, the wrong-primary-writer rejection, and the natural-key mismatch rejection (both
  accept and reject paths).
- **`DbDataSync.TaskRunner.Tests --filter Category=Integration`** (real SQL Server, 14330/14331): 32
  passed (up from 28) — `Scd2NaturalKeyIntegrationTests`' new region: a deleted key's version closes,
  an updated-not-deleted key's version stays open with its stale value (proof the sweep never compares
  values, only key presence), a segmented sweep only closes within its own range, the default guard
  refuses and rolls back an over-ratio close, an override guard closes everything. Also confirmed
  `Scd2Writer.SplitColumns`'s visibility change broke none of this file's existing cases.
- **`DbDataSync.Api.Tests --filter Category=Integration`**: 66 passed, 0 failed (18m43s) —
  `ReconcileDeletesScd2IntegrationTests`, real HTTP + a real spawned `DbDataSync.TaskRunner` worker +
  real SQL Server: seeds three open versions through an actual Primary pass, deletes one source row,
  `POST`s `reconcile-deletes`, and confirms the deleted key's version closes while an untouched key's
  version stays open and the row itself is never removed. Also the test that found the
  `ReconcileService` gap above.
- **SPA**: `npm run build` clean (0 errors); `npm run lint` exit 0, no new warnings.

## Decisions made

- **The writer's `Kind` name, `KeyReconcileScd2Close`** — matches the `KeyReconcile<Ending>` shape
  `KeyReconcileDelete` already set. No better alternative surfaced during implementation.
- **SPA copy stays "Reconcile deletes" everywhere except the two hint sentences that were factually
  wrong**, not merely imprecise — see "SPA," above. Renaming the request type, hook, form, or button
  was judged not worth the cross-cutting churn for a wording-only reason, per the plan's own reasoning.
- **The natural-key-mismatch check lives inside `ValidateKeyReconcilePairing`**, not a new sibling
  function — both its call sites already compute the primary writer for an unrelated check, so
  threading two more parameters through was free; a sibling would need its own call added at both
  sites.
- **`ReconcileService`'s hardcoded writer Kind was a real gap, not a design question** — see above.
  Resolved by making it call the same resolver everything else in this phase already resolves through,
  rather than inventing a second source of truth for the same default.
- **No new `RunKind`.** Reuses `RunKind.ReconcileDeletes` as planned — the concept is identical; only
  the writer's ending differs, and the writer `Kind` on the run/log already carries that distinction.
- **No new reader.** `KeyReconcileReader` is reused exactly as phase 124 built it, registered nowhere
  new.

## What this does not build

- **`Snapshot` support.** `SnapshotWriter.SupportsReconciliation => false` for a real reason — no
  `IsCurrent` concept at all, nothing to close.
- **Support for a stated `naturalKey` that isn't the source's primary key.** A mapping that genuinely
  needs a business key different from its technical primary key keeps today's status quo (no SCD2
  delete detection without a delete-reporting reader) until a future phase, if ever, teaches
  `KeyReconcileReader` to stage an arbitrary column list instead of `IsPrimaryKey`.
- **Any change to what "deletes need a reader that reports them" means for a source that already has
  one.** MSSQL's Change Tracking/CDC readers keep working exactly as before; this is purely a second
  path for sources with no such reader.
- **Any SPA copy/request-type/hook/controller rename** beyond the two factual-accuracy fixes in "SPA,"
  above.

## Open questions — resolved

All three the planning doc raised were resolved during this phase, as the plan doc itself already
records (`architecture/planning/done/scd2-delete-detection.md`'s own "Open questions — resolved 2026-
09-12" section): the `Kind` name, the SPA copy question, and where the validation branch lives. Nothing
new was left open by this phase's own implementation — the one real surprise (`ReconcileService`) was
found and fixed within the phase, not deferred.
