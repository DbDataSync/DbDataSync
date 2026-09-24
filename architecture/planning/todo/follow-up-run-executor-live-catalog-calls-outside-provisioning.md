# Follow-up — `RunExecutor`'s remaining live-catalog calls outside provisioning

Found by a deliberate codebase-wide sweep for the "live metadata call during a run" anti-pattern, prompted
by fixing exactly this in `ApplyScriptedTransformsAsync` (see
`architecture/implementation/todo/phase-194S-sql-column-expression-cache-only-metadata.md`) and in
`MsSqlBatchReloadReader` (see
`architecture/implementation/todo/phase-191S-shared-from-join-builder-and-retiring-query-readers.md`). The
stated policy: an active run operates entirely on cached metadata
(`mapping.SourceColumns`/`RelationshipColumns`/`TargetColumns`), never issuing a live catalog call mid-run,
except for the explicit metadata-refresh action or an auto-provisioning flow. A schema change is always
possible and a cache can always go stale — that's the operator's own responsibility, not something a run
should compensate for.

## Confirmed violation

**`RunExecutor.WithDerivedNaturalKeyAsync`** (`src/DbDataSync.TaskRunner/RunExecutor.cs:1565`) calls
`sourceDriver.ListColumnsAsync(...)` live, on every pass whose writer is `Scd2`/`KeyReconcileScd2Close` and
that has no explicit `naturalKey` option set, to derive the SCD2 natural key from the source's live primary
key. Unlike every other live call the sweep found, this one has no defending doc comment and no stated
exemption — it's simply the same anti-pattern already fixed elsewhere in this same arc, not yet fixed here.

**Proposed fix, matching the same shape as 194S**: derive the natural key from `mapping.SourceColumns`'
cached primary-key flags instead of a live call, the same cache `ApplyScriptedTransformsAsync` is moving to.

## Two exemption categories the sweep surfaced that the original two-item policy didn't name

The user's stated exceptions were "the explicit metadata-refresh action" and "auto-provisioning." The sweep
found two live calls that fit neither cleanly, each already defended by its own doc comment as a deliberate
design choice — these aren't necessarily bugs, but they weren't accounted for when the policy was stated,
and deserve an explicit decision rather than being left ambiguous:

1. **`RunExecutor.BuildLifecycleHookContextAsync`** (`:1422-1439`) calls `ListColumnsAsync` on both source
   and target, on every call, with the comment: *"deliberately not cached across a pass: a schema-evolution
   hook's whole point is to react to the source having gained a column, and caching this lookup would be
   exactly the thing that silently breaks it later."* This is a coherent argument for a mechanism whose
   entire purpose is live reactivity to schema drift — but it's neither the refresh action nor
   auto-provisioning. Is "a lifecycle hook may read live, because reacting to live schema state is its job"
   a third accepted exception, or should this call also move to cache with a documented way for an operator
   to force a refresh when they know the schema changed?
2. **`RunExecutor.RunVerificationAsync`** (`:545-553`) fetches both sides' columns live, but only for a
   `Script`-kind verification check, defended similarly ("a built-in derives everything it asks from the
   mapping and should not pay a catalog round trip"). Verification is arguably a distinct pass type from
   ordinary read/write replication — narrower in scope and less frequent — so whether it falls under the
   same policy at all is a judgment call rather than a clear violation.

## What this doc does not resolve

Whether either exemption category above should be formally added to the policy, reworded, or closed off —
that's a decision for whoever picks this up, not assumed here. The `WithDerivedNaturalKeyAsync` case is not
ambiguous in the same way; it has no defense at all and should simply be fixed.
