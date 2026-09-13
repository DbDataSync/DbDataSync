# SCD2 delete detection — closing a version via the `KeyReconcile` signal

**Resolved 2026-09-12.** The three open questions were settled (see *Open questions* below, now
answered inline). The design builds as one phase:

| phase | what |
| --- | --- |
| **129** — `architecture/implementation/todo/phase-129-scd2-delete-detection.md` | `KeyReconcileScd2CloseWriter` + `KeyReconcileScd2CloseStatement`, `GenericDriverKinds.KeyReconcileScd2Close`, engine registration on MsSql/Postgres, the `RunExecutor`/`PipelineResolution`/`ConfigValidation` changes that make the pairing resolvable and legal, and the SPA copy fix `ReconcileConfigCard`'s hardcoded pair-name text needs once the resolved writer can vary |

Two real gaps this session's research found in the design below, both corrected in the phase doc
rather than carried forward: the *Validation* section's proposed reuse of
`NaturalKeyDerivation.Derive` from `ConfigValidation` does not compile — `DbDataSync.Core` has no
project reference to `DbDataSync.Drivers.Generic` at all, for the same layering reason this doc's own
*Validation* section already gives for using Kind string literals instead of `GenericDriverKinds`
constants — so the phase doc derives the equivalent natural key inline over `CachedColumn`/
`ColumnMapping` instead; and the *SPA* section's claim that `ReconcileConfigCard` has "the writer
picker it already has (phase 125)" is not true — that component has no reader/writer picker at all,
only a static hint string naming the fixed pair, which is what the phase doc changes instead.

Builds directly on phase 124/125 (`architecture/planning/done/watermark-delete-detection.md`,
`architecture/implementation/done/phase-124-key-reconcile-delete-detection.md`,
`architecture/implementation/done/phase-125-reconcile-config-and-scheduling.md`) — same reader,
same config shape, same scheduling, same guard. This is not a new mechanism; it is a new *ending* for
the existing one.

---

## The problem

`Scd2Writer` explicitly opts out of the reconcile pattern:

```csharp
// src/DbDataSync.Drivers.Generic/Scd2Writer.cs:52
public bool SupportsReconciliation => false;
```

and its own doc comment says why, today:

> Deletes need a reader that reports them. Paired with one that does not, a key that disappears at
> the source stays current here forever — allowed, not refused, because a delete-blind reader with
> SCD2 is a real configuration for a source that never deletes.

That comment is correct about the *current* mechanism (`HistorizedStatement.BuildCloseChanged`
closes a version when the staging table carries a row with `operation = 'D'`
(`HistorizedStatement.cs:70`) — which only a delete-reporting reader ever produces), not about
whether SCD2 delete detection is possible at all. It isn't possible *that way* for a source with no
delete-reporting reader — which, per the compatibility matrix on the front page, is every engine
except SQL Server's Change Tracking/CDC readers. A PostgreSQL source on `WatermarkReader`, or any
source on a plain watermark/audit-table reader, can never close an SCD2 version today: the row just
stays open (`IsCurrent = true`) forever after the source deletes it.

Phase 124 built exactly the missing signal for this — cheaply, without a delete-aware reader — for
the plain-table case (`KeyReconcileReader` + `KeyReconcileDeleteWriter`). SCD2 was explicitly out of
scope then: phase 124's own "What this does not build" lists nothing about SCD2, because the writer
pairing validation (`ConfigValidation.ValidateKeyReconcilePairing`, below) blocks it outright, not
because it was considered and rejected.

## The idea

Reuse `KeyReconcileReader` completely unchanged — it already stages exactly the source's current
primary-key values, per segment, engine-neutral. Add a second writer that reads the same anti-join
`KeyReconcileDeleteWriter` already computes (a key in scope, absent from what this pass staged) and,
instead of deleting the row, closes it the way `HistorizedStatement.BuildCloseChanged` already does
for an explicit delete: `IsCurrent = 0`, `ValidTo = now`. Same signal, same segment model, same
guard — a different ending because the target has a different job.

## Why the reader already lines up with SCD2's own key

This only works cheaply because of a coincidence worth stating precisely, not assuming: `Scd2Writer`'s
natural key, when nobody has stated one, is derived by `NaturalKeyDerivation.Derive` —

```csharp
// src/DbDataSync.Drivers.Generic/NaturalKeyDerivation.cs:51
foreach (var column in sourceColumns.Where(c => c.IsPrimaryKey))
```

— and `KeyReconcileReader.KeyColumnMappings` projects exactly the same set:

```csharp
// src/DbDataSync.Drivers.Generic/KeyReconcileReader.cs:47
var keyColumns = sourceColumns.Where(c => c.IsPrimaryKey).ToList();
```

Same predicate, same source. When a mapping's Scd2 natural key is left to derive (the documented
normal case — `Scd2Writer.cs:63`: *"Left empty it is derived from the source table's primary key"*),
the staged key set and the business key this writer needs to join on are, by construction, the same
columns. **This does not hold if an operator has stated an explicit custom `naturalKey`** different
from the source's primary key — `KeyReconcileReader` has no way to know about that override; it only
ever stages `IsPrimaryKey` columns. See *Validation*, below, for how this gets caught rather than
silently joining on the wrong columns.

## Design

### A new writer: `KeyReconcileScd2Close`

`DbDataSync.Drivers.Generic`, paired with the existing `KeyReconcileReader` the same way
`KeyReconcileDeleteWriter` is — a sibling, not a replacement. Modelled directly on
`KeyReconcileDeleteWriter`'s count-then-act-then-guard shape (`KeyReconcileDeleteWriter.cs:53-88`),
with two differences: the join key is the natural key (via `TargetShape`'s value columns split the
same way `Scd2Writer.SplitColumns` already does it, `Scd2Writer.cs:165-192`), not
`shape.PrimaryKeyColumns` — the target's real primary key is the surrogate
(`HistorizedColumns.SurrogateKey`) and joining on it would match nothing; and the act is an `UPDATE`,
not a `DELETE`:

```sql
-- count: only *open* rows are eligible to close
SELECT COUNT(*) FROM <target> WHERE <IsCurrent> = <true> AND <segmentScope>;

-- close: an open row whose natural key the staged set didn't produce
UPDATE <target>
SET <ValidTo> = <now>, <IsCurrent> = <false>
WHERE <IsCurrent> = <true>
  AND <segmentScope>
  AND NOT EXISTS (
    SELECT 1 FROM <staging> s
    WHERE s.<k1> = <target>.<k1> AND ...   -- natural key columns, not the surrogate
  );
```

Same correlated `NOT EXISTS` `KeyReconcileDeleteStatement.BuildDelete` uses and for the same reason
(`KeyReconcileDeleteWriter.cs:128-131`: portable, and correct when a staged key column can be
`NULL`, unlike a tuple `NOT IN`).

`SupportsReconciliation => true` — this writer *is* the reconciling half of the pair, in the same
sense `KeyReconcileDeleteWriter` is; `Scd2Writer.SupportsReconciliation` itself stays `false`, since
the primary Scd2 writer still isn't the one doing this. The reconciling capability is scoped to the
right stage of the pipeline: the reconcile pass, not the change-processing one.

### The delete guard, reused exactly

Same `DeleteGuardOption`/`DeleteGuardEvaluator` phase 124 built, threaded through `RunExecutor.WithGuard`
(`RunExecutor.cs:1471-1478`) exactly as it already is for `KeyReconcileDeleteWriter` — that injection
point sets `DeleteGuardOption.OptionKey` regardless of which writer is on the other end, so this new
writer needs no `RunExecutor` change to receive a guard. The only difference from the delete case is
what "scope" means for the ratio: the denominator is *open* rows in the segment
(`IsCurrent = true AND <segmentScope>`), not every row in the segment, because a row already closed
by an earlier pass was never a candidate to close again.

### The natural key: reuse `Scd2Writer.NaturalKeyOption`, extend where it gets derived

The new writer reads the *same* `Scd2Writer.NaturalKeyOption` key out of its options bag — not a new
option — so a mapping's primary Scd2 writer and its reconcile-close companion can never disagree
about what identifies a row. Concretely, that means `RunExecutor.WithDerivedNaturalKeyAsync`'s gate

```csharp
// src/DbDataSync.TaskRunner/RunExecutor.cs:1434
if (writerKind != GenericDriverKinds.Scd2)
    return options;
```

needs `GenericDriverKinds.KeyReconcileScd2Close` added alongside `GenericDriverKinds.Scd2` — one
line, same derivation, same injected option key, so a reconcile pass derives the natural key exactly
the way a primary Scd2 pass already does when nothing is stated.

### `PipelineResolution.ReconcileWriterKind` — a default that depends on the mapping's real writer

Today this is an unconditional literal:

```csharp
// src/DbDataSync.Core/Config/PipelineResolution.cs:69-70
public static string ReconcileWriterKind(ReplicationTaskConfig task, TableMappingConfig? mapping) =>
    Reconcile(task, mapping).Writer?.Kind ?? "KeyReconcileDelete";
```

The default needs to depend on the mapping's own primary writer: `KeyReconcileScd2Close` when
`Writer(task, mapping).Kind == "Scd2"`, `KeyReconcileDelete` otherwise. An explicit
`ReconcileConfig.Writer` override still wins outright, unchanged — this only changes what "unset"
means, the same nullable-override shape every other stage here already has.

### Validation

`ConfigValidation.ValidateKeyReconcilePairing` (`ConfigValidation.cs:219-257`) currently hard-rejects
any writer but `KeyReconcileDelete` paired with a `KeyReconcile` reader:

```csharp
if (readerIsKeyReconcile != writerIsKeyReconcileDelete)
    throw new ConfigValidationException(...);
```

This needs to become "must be `KeyReconcileDelete` **or** `KeyReconcileScd2Close`", plus one new,
`KeyReconcileScd2Close`-specific check that does not apply to the delete case:

1. **The mapping's primary writer must actually be `Scd2`.** Closing a version on a target that
   isn't versioned is meaningless — enforce it here rather than let a misconfiguration reach a
   writer that assumes `HistorizedColumns.IsCurrent` exists.
2. **The effective natural key must equal the source's cached primary key**, exactly the set
   `KeyReconcileReader` stages. If `Scd2Writer.NaturalKeyOption` is unset, this holds by construction
   (see *Why the reader already lines up*, above) and needs no check. If it *is* stated, compare it
   against `NaturalKeyDerivation.Derive(mapping.SourceColumns, mapping.ColumnMappings)` and reject a
   mismatch by name — the alternative is a writer silently joining on columns the staging table
   doesn't have, which fails at run time with a confusing "column not found" rather than a save-time
   message that says what's wrong.

`ConfigValidation.ValidateReconcile` (`ConfigValidation.cs:268-285`) needs no change beyond calling
the updated pairing check — its cadence/after-change rules are already writer-agnostic.

### SPA

- `RECONCILE_ONLY_KINDS` (`types.ts`, phase 124) gains `KeyReconcileScd2Close` alongside
  `KeyReconcile`/`KeyReconcileDelete` — excluded from the ordinary Change Processing pickers the same
  way its sibling already is.
- `ReconcileConfigCard` needs no new field — the writer picker it already has (phase 125) just gains
  a new option, and the default-writer text can say which one a mapping will actually get.
- **Open question, not decided here**: every reconcile-facing label in the SPA says "delete" —
  the chrome button (`ReplicationDetailPage.tsx:179`, *"Reconcile deletes…"*), the form title
  (`ReconcileDeletesForm.tsx:118`), the hook and request-type names (`useReconcileDeletes`,
  `ReconcileDeletesRequest`, `types.ts:994`). For an `Scd2` mapping, this pass closes a version; it
  never deletes a row. Leaving the copy as-is keeps the *concept* honest ("reconcile what the source
  no longer has") at the cost of the literal word being wrong for one writer; renaming touches a
  request type, a hook, a form, and a button across the SPA and the controller
  (`RunsController`/`ReconcileService`) for a wording-only reason. Leaning toward **leave it**
  (the run's own log lines and `RunKindBadge` already say what actually happened), but flagging this
  explicitly rather than deciding it here.

## What this does not do

- **Does not touch `Snapshot`.** `SnapshotWriter.SupportsReconciliation => false` for a real reason
  (`SnapshotWriter.cs:20-21`): *"there is no notion of removing what is absent from a target whose
  entire job is keeping what used to be there"* — Snapshot has no `IsCurrent` concept at all, nothing
  to close. Not an oversight; a different shape of "not reconciling" than SCD2's.
- **Does not add a new `RunKind`.** Reuses `RunKind.ReconcileDeletes` — the concept ("remove/close
  what the source no longer has") is identical to the plain-table case; only the writer's ending
  differs. A new `RunKind` would mean touching `RunLanes`, the run-history `kind` filter, and
  `RunKindBadge` for a distinction the writer `Kind` already carries.
- **Does not add a new reader.** `KeyReconcileReader` is reused exactly as phase 124 built it.
- **Does not support a stated `naturalKey` that isn't the source's primary key** — see *Validation*.
  A mapping that genuinely needs a business key different from its technical primary key keeps the
  status quo (no SCD2 delete detection without a delete-reporting reader) until a future phase, if
  ever, teaches `KeyReconcileReader` to stage an arbitrary column list instead of `IsPrimaryKey`.
- **Does not change what "deletes need a reader that reports them" means for a source that *does*
  have one.** MSSQL's Change Tracking/CDC readers keep working exactly as they do today; this is
  purely a second path for sources that have no such reader.

## Open questions — resolved 2026-09-12

1. **The writer's `Kind` name.** → **resolved**: `KeyReconcileScd2Close`, exactly the working name
   used throughout this doc — it matches the `KeyReconcile<Ending>` shape `KeyReconcileDelete` already
   set, and is unambiguous about which writer family's version it closes. No reason found during
   phase-doc research to prefer an alternative.
2. **SPA copy** — see the *SPA* section above. → **resolved**: leave "Reconcile deletes" as the label,
   the hook name (`useReconcileDeletes`), and the request type (`ReconcileDeletesRequest`) everywhere —
   the concept stays honest even where the literal word "delete" is inexact for an `Scd2` mapping, and
   renaming touches too much SPA/controller surface for a wording-only reason; the run's own log lines
   and `RunKindBadge` already say what actually happened. One narrower thing does need to change,
   found only by reading `ReconcileConfigCard.tsx` and `ReconcileDeletesForm.tsx` in full rather than
   assuming the plan's own description of them: `ReconcileConfigCard.tsx` has no writer picker at all
   (this doc's *SPA* section above was wrong about that) — it has a static hint sentence naming the
   fixed `KeyReconcile`/`KeyReconcileDelete` pair, which becomes factually wrong once the resolved
   writer can be `KeyReconcileScd2Close`, and `ReconcileDeletesForm.tsx`'s hint sentence ("removes
   target rows... never inserts or updates") is also factually wrong for an `Scd2`-resolved mapping.
   Both need to become conditional on the resolved writer — a copy-accuracy fix, not the renaming this
   question was actually asking about. See phase 129's doc, §8, for the precise wording.
3. **Where the mismatch check lives.** → **resolved**: inside `ValidateKeyReconcilePairing` itself, as
   an added branch — not a new `ValidateScd2ReconcilePairing` sibling. The function is already called
   from two places (`ConfigRepository.SaveTableMapping` directly, and again inside `ValidateReconcile`)
   and both already compute the mapping's primary writer for another check
   (`ValidateHistorizedTarget`) at the same call site, so both can supply it to the widened signature
   with no new lookup — a separate sibling would need its own call added at both sites, the same
   "who remembers to call the second validator" risk `ValidateReconcile`'s own doc comment already
   flags as the reason it reuses `ValidateKeyReconcilePairing` "verbatim" rather than re-implementing
   the check. See phase 129's doc, §7, including a real gap this reasoning surfaced: the mismatch
   check itself cannot be built exactly as originally worded here (see the note under "Resolved
   2026-09-12" above) because `ConfigValidation` cannot reference `NaturalKeyDerivation` across the
   Core/driver-layer boundary.

## References

- `architecture/planning/done/watermark-delete-detection.md` — the original design this extends.
- `architecture/implementation/done/phase-124-key-reconcile-delete-detection.md`,
  `phase-125-reconcile-config-and-scheduling.md` — what already exists and is being reused wholesale.
- `src/DbDataSync.Drivers.Generic/KeyReconcileDeleteWriter.cs`,
  `src/DbDataSync.Drivers.Generic/Scd2Writer.cs`, `HistorizedStatement.cs`,
  `NaturalKeyDerivation.cs` — what the new writer sits between.
- `src/DbDataSync.Core/Config/PipelineResolution.cs`, `ConfigValidation.cs` — the two files that
  actually need to change to make the pairing legal and resolvable.
- `src/DbDataSync.TaskRunner/RunExecutor.cs` — the one-line natural-key-derivation gate that needs
  to recognize the new writer `Kind`.
