# Phase 68 — Per-mapping pipeline settings, and an auto-derived SCD2 natural key (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/per-mapping-pipeline-settings.md`

## Why

The SCD2 writer's `naturalKey` option — which mapped columns identify a row across its versions — is a
`WriterConfig.Options` entry, and `WriterConfig` lives only on `ReplicationTaskConfig`
(`ReplicationTaskConfig.cs:26`), applied uniformly to every table mapping the replication has. That's
only correct if every table in the replication shares the same natural-key columns, which is true
approximately never — a replication syncing `Orders`, `Customers` and `Products` needs three different
natural keys and can currently express one. This phase makes reader/cache/writer Kind *and options*
overridable per table mapping, and uses that to let the natural key default itself from the source's
primary key instead of being a required, replication-wide, hand-typed value.

## Config model

`TableMappingConfig` gains three independently-nullable fields:

```csharp
public ReaderConfig? ReaderOverride { get; set; }
public CacheConfig? CacheOverride { get; set; }
public WriterConfig? WriterOverride { get; set; }
```

Null means inherit the replication's stage entirely — Kind and Options together, no merge, the same
atomic-override rule the rest of this config model already uses for scripts, hooks and provisioning.
Non-null replaces that stage's Kind and Options wholesale. Each of the three is independent: a mapping
can override just the writer and still inherit reader and cache.

**Not reusing `HierarchicalBinding`.** That helper resolves a dictionary keyed by slot across three
levels (connection → replication → mapping). Pipeline config is two levels — there is no
connection-level pipeline config today — and exactly three fixed keys, each independently overridable.
A `??` per field is simpler than adapting a three-level dictionary walk to a two-level fixed triple.
Confirmed while writing this phase doc, not assumed going in.

## Resolution, in RunExecutor

`RunMappingAsync` resolves Kind today as `item.Kinds.ReaderKind ?? processing.Reader.Kind` (and the
same for cache/writer) at `RunExecutor.cs:482-484`, then always reads `processing.Reader.Options` /
`.Cache.Options` / `.Writer.Options` at `RunExecutor.cs:580-582` regardless of any Kind override.
Becomes two steps — resolve the effective per-stage config first, then resolve Kind and Options from
it:

```csharp
var effectiveReader = mapping.ReaderOverride ?? processing.Reader;
var effectiveCache  = mapping.CacheOverride  ?? processing.Cache;
var effectiveWriter = mapping.WriterOverride ?? processing.Writer;

var readerKind = item.Kinds.ReaderKind ?? effectiveReader.Kind;
// ...and Options come from effectiveReader.Options / effectiveCache.Options / effectiveWriter.Options.
```

`item.Kinds` — the transient per-work-item override a backfill already uses — stays the most specific,
applied on top of `effective*.Kind` exactly as it's applied on top of `processing.*.Kind` today.
Nothing about that mechanism changes.

## The SCD2 natural key

### Locked to auto-derivation at the replication level

The replication's own Pipeline tab stops offering a free-text `naturalKey` field at all — for the SCD2
writer it shows only "auto-derived from each mapping's primary key." There is nowhere above the mapping
to type a value in. A table mapping's own Pipeline tab is the only place `naturalKey` can be entered
explicitly, as an override.

### Auto-derivation

- New `NaturalKeyDerivation.Derive(IReadOnlyList<ColumnMetadata> sourceColumns, IReadOnlyList<ColumnMapping> columnMappings) -> IReadOnlyList<string>`
  — likely in `DataSync.Drivers.Generic`, alongside `ProvisioningColumnBuilder`, which already does the
  analogous "translate source metadata through a mapping's `ColumnMapping` list" job for a different
  question (phase 25). Filters `sourceColumns` to `IsPrimaryKey`, maps each through `columnMappings` to
  its target column name.
- In `RunExecutor`, before calling `writer.ApplyAsync`: when the effective writer Kind is `Scd2` and its
  effective Options has no `naturalKey`, fetch the source's columns (the same
  `sourceDriver.ListColumnsAsync` call `EnsureTargetTableProvisionedAsync` already makes) and inject the
  derived value into a per-call copy of the writer options — the same pattern `WithSegment` already uses
  to inject a per-iteration option without mutating the configured dictionary.
- No primary key and no override: nothing gets injected, and `Scd2Writer.ApplyAsync`'s own existing
  required-option check (`Scd2Writer.cs:165-167`) fires exactly as it does today. No new failure path to
  build for that case.

### `Required` flips to `false`

`Scd2Writer.Parameters`'s `naturalKey` descriptor (`Scd2Writer.cs:52-63`) is `Required = true` today,
enforced server-side by `ParameterValidation.Validate` (`ParameterValidation.cs:36-40`) via
`ParameterCheck.ThrowIfInvalid(ReplicationTaskConfig)`. Locking the replication level to auto-derivation
means a replication using the SCD2 writer will, going forward, never have a `naturalKey` in its own
`WriterConfig.Options` at all — so this becomes `Required = false`, uniformly. The same declaration is
what a mapping-level override is checked against too (see Validation, below), and `false` is correct at
both levels: absent means "auto-derive," present means "use this," neither level should be forced to
state one.

### API for the SPA's "auto-derived" display

The mapping editor's Pipeline tab has to show what *would* be derived, not just have it happen
invisibly at run time. Add a method alongside `ProvisioningService.GetInferredTargetTypesAsync`
(`ProvisioningService.cs:110-131`, which already does the equivalent "read source `ColumnMetadata`,
answer a per-mapping question" job for column types) — e.g. `GetInferredNaturalKeyAsync(replicationName, mappingName)`,
returning the derived target column list or a reason it's empty (no source primary key), built on the
same `NaturalKeyDerivation` helper `RunExecutor` uses, so the preview and the runtime behavior can never
quietly disagree.

### Consider switching `naturalKey`'s declared type to `ColumnPicker`

`ParameterType.ColumnPicker` with `ParameterCardinality.Any` (`ParameterDescriptor.cs:28-33`) already
exists for exactly this shape — a multi-select over the mapping's own column mappings, for a composite
key, instead of the free-text "comma-separated for a composite one" field it is today. Worth taking
while this is being touched anyway, rather than carrying the current plain-text field forward unchanged.
Not required for the phase to be complete if it turns out to be more than a small addition.

## Validation

Mirror `ParameterCheck.ThrowIfInvalid(ReplicationTaskConfig)` (`ParameterCheck.cs:40-61`) for a
mapping's overrides: when `ReaderOverride` / `CacheOverride` / `WriterOverride` is non-null, check its
Kind exists and its Options validate, the same way the replication-level check already does. Call this
from wherever `TableMappingConfig` is saved, alongside the existing hook validation
(`ConfigRepository.SaveTableMapping` already calls `ValidateHooks` at this point — phase 26).

**One thing to get right that the existing check doesn't have to.** `ThrowIfInvalid(ReplicationTaskConfig)`
resolves capabilities from the **source** connection's driver for all three stages
(`ParameterCheck.cs:34-39`) — harmless today because reader/staging/writer Kinds mostly exist
identically on both registered drivers, but not actually correct. A mapping's `CacheOverride` /
`WriterOverride` should be checked against the **target** driver's capabilities and `ReaderOverride`
against the **source**'s — the way `RunExecutor` itself already resolves them, with separate
`sourceDriver`/`targetDriver`. Get this right here rather than copying the existing shortcut forward;
see "Open questions" for whether the replication-level check should also be fixed while this is being
touched.

## SPA

- New tab on the mapping editor: `{ path: 'pipeline', label: 'Pipeline', testId: 'mapping-tab-pipeline' }`
  in `mappingTabs.ts`, alongside the rest.
- New component, structurally close to `OverviewPanel.tsx`'s `PipelineTab` (the three-stage
  Reader → Staging → Writer picker, `ParameterForm` for the declared options): reader/cache/writer each
  get an inherit/override toggle. Overriding reveals the same Kind picker and `ParameterForm` the
  replication's own Pipeline tab uses, seeded from the replication's current values as a starting point
  rather than a blank form.
- Writer-Kind-specific handling for `naturalKey`, on both Pipeline tabs (mirroring the SCD2-delete-blind
  check `PipelineTab` already special-cases by Kind — `OverviewPanel.tsx:190-196`):
  - **Replication's Pipeline tab**: when Writer Kind is `Scd2`, `naturalKey` is not rendered as an
    editable field — replaced with static text ("Auto-derived from each mapping's primary key").
  - **Mapping's Pipeline tab**: when the effective Writer Kind is `Scd2`, show the auto-derived columns
    (from the new API above) as the default, read-only, with an explicit toggle to override and enter
    columns by hand.

## Out of scope

- **Unique-constraint-based auto-derivation.** Only `IsPrimaryKey` exists on `ColumnMetadata` today; a
  unique index/constraint isn't introspected anywhere in the codebase, on either driver. Primary-key-only
  for this phase — see the planning doc.
- **Migrating an existing replication's `naturalKey` value onto its mappings.** Not needed: SCD2 has not
  been used in production, so there's nothing to preserve. If a dev/test config has a replication-level
  value set, it simply stops being read once this ships.
- **Per-mapping overrides for anything other than reader/cache/writer Kind+Options.** Segmenting
  strategies, provisioning, hooks and scripts already have their own per-mapping mechanisms, untouched by
  this phase.

## How to verify when built

- Unit: `NaturalKeyDerivation.Derive` — a source with a single-column primary key, a composite one, no
  primary key at all, and a primary-key column that isn't present in the mapping's `ColumnMappings`.
- Unit: `RunExecutor`'s stage resolution — a mapping with no overrides runs the replication's Kind and
  Options for all three stages; a mapping overriding just the writer still inherits reader and cache;
  `item.Kinds` still wins over a mapping override, the same way it already wins over the replication's
  own configured Kind.
- `Category=Integration`: two table mappings under one SCD2-writer replication, different tables,
  different primary keys, both auto-deriving correctly and neither run touching the other's natural key.
- `Category=Integration`: a mapping overriding `naturalKey` explicitly gets that value even though its
  source also has a primary key that would have derived to something else.
- `Category=Integration`: save-time validation rejects a mapping override naming a Kind its (source or
  target, as appropriate) driver doesn't support, and rejects an invalid option value the same way the
  replication-level check already does.
- Playwright: the replication's Pipeline tab shows "Auto-derived from each mapping's primary key" with
  no field to type a natural key into; a mapping's Pipeline tab shows the derived columns and can
  override them; a run using the override applies against the overridden key rather than the derived
  one.

## Open questions

- Whether `ParameterCheck`'s existing source-driver-for-every-stage shortcut (see "Validation," above)
  is worth fixing for the **replication**-level check too while this phase is already touching that
  file, or left as pre-existing behavior outside this phase's scope.
