# Phase 45 — Table mapping UX overhaul (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/table-mapping-ux-overhaul.md`

## What this covers

Related changes to how table mappings are created and edited, aimed at cutting the number of clicks and
decisions needed for the common case (map a table, accept sensible defaults) while keeping every default
overridable — plus two bug fixes surfaced while designing this:

1. Inferred mapping name, target-table autofill, and `.` allowed in names.
2. Inheritable "create target table if missing."
3. Editable target column name/type and transform, via a consistent show-value-plus-pencil interaction.
4. A mappings overview grid as the section's landing page, with single-request bulk mapping creation.
5. Fix: schema/table names combined into one string and split back apart — a real bug, not a hypothetical.
6. Fix: the auto-mapping editor silently showing the wrong source column for a row whose stored value
   isn't in the freshly loaded column list.

## 1. Name inference and target autofill

**`TableMappingForm.tsx`**

- Track whether the operator has hand-edited the name (a `nameTouched` flag, set on the name input's
  `onChange`, the same "stop inferring once touched" shape used for schema-follows-table-pick in
  `MappingSide.setTableName`).
- While untouched, derive the name from the source table as `${source.schema}.${source.table}` whenever
  either changes.
- When the source table is set (chosen or inferred) and the target table is still empty, autofill
  `target.table` to the source's table name. This only *sets* an empty field — it does not override a
  target the operator already typed.
- Confirm no client or server validation currently rejects `.` in a mapping name (grounding: the input has
  no `pattern`, and `ConfigPaths.cs` writes the name straight into a filename, which is `.`-safe) — add a
  regression test rather than a code change, since nothing appears to need fixing here.

## 2. Inheritable provisioning

- **API/config**: `ReplicationTaskConfig` gains a `provisioning` field (shape TBD — likely mirrors
  `ProvisioningConfig`); `TableMappingConfig.provisioning.createTargetTableIfMissing` becomes nullable so
  absence means "inherit the replication's value." Resolution follows the same
  replication-then-mapping-overrides order phase 16 established for endpoints.
- **`ProvisioningCard.tsx`**: the checkbox becomes an `INHERITED`-badge-plus-override-toggle control, the
  same shape `MappingSide.tsx` already renders for connection/database. When not overriding, show the
  replication's resolved value read-only; toggling on turns it into an editable checkbox starting from
  the current effective value.
- **Overview page**: `OverviewPanel.tsx` (or a new card there) needs a place to set the replication-level
  default, parallel to how `EndpointsCard` sets the replication-level endpoints.

## 3. Column mapping editor

**`ColumnMappingEditor.tsx`**

- New small shared interaction: a value rendered as text with a pencil-icon button beside it; clicking
  the pencil swaps the text for an editable control (text input for name/transform, a type picker for
  type), with a way to commit or cancel. Worth factoring as its own component
  (`EditableValue`/`PencilField`, exact name TBD) since it's used three times in this one editor.
- **Target column name**: apply the pattern, defaulting to the mapped source column's name; editing
  writes a real rename into `ColumnMapping`, which needs a new field to carry it (today `targetColumn` *is*
  the name — decide whether rename means writing a new `targetColumn` value directly, since there's
  nothing else it could mean, or whether a separate "original vs. renamed" pair is needed for provisioning
  to still find the right source-shaped column; the simpler reading is `targetColumn` already means "the
  name it will have," so a rename is just editing that field through the new UI rather than a raw one).
- **Target column type**: new read path — call the existing `IProvisioner`/`CanonicalType` source→target
  type mapping (already used to build `CREATE TABLE`) to compute and show an inferred type for every row,
  even when nothing is customized. Apply the pencil pattern to let the operator override it. `ColumnMapping`
  needs a new optional field (e.g. `targetType: string | null`) that stays `null` for the common
  accept-the-inference case and is only set when the operator actually edits it — the inferred value
  itself is never written back into config.
- **Transform**: same pencil pattern, replacing the always-open `<input>` — collapses rows that don't use
  a transform instead of showing an empty text box in every one.
- Source column dropdown is unchanged.

## 4. Mappings overview grid

**New component**, replacing `MappingsIndex`'s auto-redirect as the section's landing page (still under
`TableMappingsPanel`'s layout, or restructuring that layout if the sidebar-plus-editor shape doesn't fit
a grid landing page well — decide during implementation).

- Lists every table in the replication's resolved default source database (reuses `useTables`, the same
  hook `MappingSide` already calls).
- A filter input (by name, likely schema too).
- Per row: table name, a mapping-count badge (0 for unused; the existing `useTableMappings` /
  per-mapping source lookups can compute this, or a new endpoint if doing it client-side means N+1
  requests — check before choosing).
- A checkbox per row plus a header select-all.
- **"Create N mappings"** button, enabled once at least one row is checked, bulk-creating one
  `TableMappingConfig` per checked table: name inferred per §1's rule, source set to that table, target
  autofilled to the same table name, everything else (provisioning, scripts) left unset to inherit —
  exactly what a hand-created mapping accepting every default would produce. Needs either a bulk-create
  API endpoint or N sequential calls to the existing per-mapping upsert — a bulk endpoint is probably
  worth it once "N" is more than a handful, to avoid N round trips and partial-failure ambiguity.
- The existing sidebar-and-editor view for a single mapping stays reachable exactly as today (pick one,
  or `+` to hand-create one) — this is an additional landing page, not a replacement for the editor.

## What this phase does not build

- Any change to how a mapping actually runs, or to the provisioning DDL itself.
- A general "editable field with pencil" component library beyond what these three column-editor uses
  need — scoped to this editor first.
- Renaming/retyping an *existing* target's real database column (schema evolution) — this is about what
  gets written into the mapping/DDL before or at creation, same boundary phase 40 already drew for
  editable target tables.

## How to verify when built

- Choosing a source table with no name yet typed produces `schema.table` as the mapping name; typing into
  the name field stops further inference.
- Choosing a source table autofills an empty target table field with the same table name; a target
  already typed is left alone.
- A mapping name containing `.` saves, loads, and round-trips through the sidebar link and route
  correctly.
- A replication with `createTargetTableIfMissing` set, and a mapping that does not override it, resolves
  to the replication's value; a mapping that does override wins locally.
- Target column name and type both show a pencil, default to sensible values (source name; inferred
  type), and only appear in saved config when actually edited.
- Transform uses the same pencil pattern and behaves identically to today once edited.
- The mappings overview grid lists all source tables, filters correctly, shows accurate per-table mapping
  counts, and "Create N mappings" produces exactly N new mappings with the expected defaults.
- Full suite green, including updated Playwright screenshots for the new landing page and the changed
  column mapping editor.

## Open questions

- Whether column rename needs a distinct "renamed from" field for provisioning's benefit, or whether
  `targetColumn` being the name is already sufficient — see §3.
- Bulk-create as one API call or N sequential ones.
- Exact shape/name of the new `ReplicationTaskConfig.provisioning` field.
- Whether the mappings overview grid replaces `TableMappingsPanel`'s layout outright or sits alongside it
  as a new index route within the same sidebar shell.
