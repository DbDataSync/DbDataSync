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
- **Target column name**: apply the pattern. `targetColumn` stays the current/desired name, but a rename
  now also appends to a new `renames: RenameStep[]` on the `ColumnMapping` — **an array, not a single
  "renamed from" value**, because the operator can rename a column more than once before provisioning
  ever applies anything, and provisioning needs the full history, not just the latest edit. Each step is
  `{ from: string; to: string; applied: bool }`. `applied` is what keeps a **swap** (A renamed to B and B
  renamed to A at the same time) from corrupting data — provisioning's `ALTER TABLE` planning has to
  reason about which steps already landed before deciding what to run next, rather than blindly applying
  every unapplied-looking step by current name. **The exact sequencing algorithm for a rename cycle is
  real design work, not settled here** — see Open questions.
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

**New component**, reached via a special, **bolded "Overview" entry at the top of the existing sidebar**
in `TableMappingsPanel` — a sibling of the per-mapping list, not a replacement for it. The
sidebar-plus-pane shell stays; only the pane's content is new for this route.

- Lists every table in the replication's resolved default source database (reuses `useTables`, the same
  hook `MappingSide` already calls).
- A filter input (by name, likely schema too).
- Per row: table name, a mapping-count badge (0 for unused; the existing `useTableMappings` /
  per-mapping source lookups can compute this, or a new endpoint if doing it client-side means N+1
  requests — check before choosing).
- A checkbox per row plus a header select-all.
- **"Create N mappings"** button, enabled once at least one row is checked. **One API call**, not N —
  the server creates every selected mapping within a single request (name inferred per §1's rule, source
  set to that table, target autofilled to the same table name, everything else left unset to inherit).
  Because this can run long enough to want feedback, the endpoint reports progress as it goes — reusing
  the existing SignalR live-run infrastructure (`run-metrics.md` notes it's already there) rather than
  inventing a second push/poll mechanism just for this. The UI shows "created N of M" while the request
  is in flight, not a single opaque spinner.
- The existing sidebar-and-editor view for a single mapping stays reachable exactly as today (pick one,
  or `+` to hand-create one) — this is an additional landing page, not a replacement for the editor.

## 5. Fix: schema/table combined into one string, then split apart

Combining schema and table into one string is fine for a *label*; it is a bug the moment that string
becomes *data* someone parses back apart, because a schema or table name containing a literal `.`
(quoted identifiers allow this) breaks the split. Two confirmed instances:

- **`MappingSide.tsx`** — the non-`allowNewTable` (source) table `<select>` builds
  `${schema}.${table}` as each `<option>`'s `key`/`value` and the selection's current key, then recovers
  `schema`/`table` via `e.target.value.split('.')` in `onChange`. Fix: carry the selection as a real pair
  (e.g. key the `<option>` by index or by an opaque id, and look up `{schema, table}` from the selected
  table's own object) instead of a string that gets torn back apart.
- **`HookRenderer.QuoteMaybeQualified`** (`DataSync.Drivers.Generic/HookRenderer.cs`) — splits an
  operator-supplied hook-parameter value on `.` to tell a bare name from a schema-qualified one
  (`parts.Length == 2`). Breaks for a schema or table name that itself contains a `.`. This one is a
  harder fix: the value genuinely arrives as operator-typed free text (a hook parameter), so there's no
  structured pair to carry instead — needs its own look at whether the input can become structured
  (a schema field and a table field, rather than one string) or whether quoting/escaping rules need
  stating explicitly for the free-text case.
- **Audit the rest of the codebase** for the same shape before calling this done — these two were found
  by a direct search for `.split('.')`-style code; a combine-without-an-obvious-split (e.g. a dictionary
  keyed by a combined string, compared later) would not show up in that search and needs a separate look.

## 6. Fix: auto-mapping editor shows the wrong source column for stale rows

**Root cause**, in `ColumnMappingEditor.tsx`: each row's source-column `<select>` is
`value={m.sourceColumn}`. When the stored `sourceColumn` name isn't present in the just-loaded
`sourceColumns` list, the browser's native `<select>` silently renders the **first** `<option>` instead —
the component's actual state doesn't change, only the display does. This matches the reported symptom
exactly (rows "jump in" once metadata loads, all appearing to show the first source column) and explains
why **Auto-map "fixes" it**: it overwrites every row with a freshly computed valid value, masking the
display bug rather than encountering it.

Fix: stop relying on the browser's silent fallback. When a row's stored `sourceColumn` (or
`targetColumn`) doesn't match the currently loaded metadata, show that explicitly — an empty/placeholder
selection or a visible "unknown column" marker — rather than letting the select render as if the first
item were chosen. Left as-is, an operator can resave a mapping without ever noticing it silently changed.

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
  counts, and "Create N mappings" produces exactly N new mappings with the expected defaults, via a
  single request that reports progress while it runs.
- The bolded Overview sidebar entry navigates to the grid and sits above the per-mapping list.
- Renaming a target column twice before saving/applying produces two ordered, unapplied `renames` steps;
  after Apply runs the ALTER, the corresponding step(s) are marked `applied`.
- `MappingSide`'s source table picker correctly selects a table whose schema or table name contains a
  literal `.` — regression test specifically for the bug this phase fixes.
- A mapping's row whose stored `sourceColumn` doesn't exist in freshly loaded source metadata renders as
  an explicit "unknown"/unselected state, never silently as the first column in the list.
- Full suite green, including updated Playwright screenshots for the new landing page and the changed
  column mapping editor.

## Open questions

- **The rename-cycle (swap) sequencing algorithm.** How provisioning safely applies a set of `renames`
  steps when two columns swap names, without double-applying or clobbering — needs real design attention,
  not just the `applied` flag's bookkeeping.
- Exact shape/name of the new `ReplicationTaskConfig.provisioning` field.
- Whether reusing the SignalR live-run hub for bulk-create progress is a new hub or a channel on the
  existing one.
- Whether `HookRenderer.QuoteMaybeQualified`'s free-text schema-qualified value can become a structured
  input instead of operator-typed text that gets split — or whether it stays text with clearer quoting
  rules documented instead.
- Whether other combine-then-split instances exist beyond the two found by direct search — needs a
  broader audit pass, not just the two confirmed spots.
