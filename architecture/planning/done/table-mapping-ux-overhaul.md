# Table mapping UX overhaul: inferred naming, inheritable provisioning, editable column mapping, and a mappings overview grid

**Status: resolved 2026-08-28 — arrived fully specified.**

## The note as given

- A table mapping's name should be inferred from its source and target tables, and the target table name
  should autofill to match the source table name.
- "Create target table if missing" should be an inheritable setting from the replication, rather than
  always configured per mapping.
- In column mappings: the source column stays a dropdown. The target column gets a rename option behind
  a pencil icon. The target type is shown (even when inferred, not stored) with its own pencil icon to
  customize it. The same show-value-plus-pencil pattern applies to the transform field too.
- A mapping name should allow `.`, since it is the common schema/table separator, and the default
  inferred name should include the schema.
- The mappings section's initial page should become an overview: a filterable grid of every source table
  in the default source database, each showing how many mappings it's already used in, with checkboxes,
  a select-all, and a "Create N mappings" button that bulk-creates one mapping per selected table with a
  default target and everything else inherited.

## Grounding in the current code

- **Naming.** `TableMappingForm.tsx`'s name `<input>` is `required` with no pattern — `.` is not actually
  blocked today, and neither is any other printing character. The name is used directly as a filename
  (`ConfigPaths.cs`: `Path.Combine(TableMappingsDir(...), $"{mappingName}.yaml")`), which is fine for `.`
  on every filesystem this runs on. There is currently **no inference at all** — an operator types a name
  from nothing, and the target table field starts empty (`emptySpec`) rather than following the source.
- **Provisioning.** `ProvisioningConfig.createTargetTableIfMissing` lives only on `TableMappingConfig`
  today; `ReplicationTaskConfig` has no provisioning field to inherit from. This is the same shape phase
  16 already solved for endpoints and phase 23 solved for scripts — inherit-unless-overridden — just not
  yet applied here.
- **Column mapping editor.** `ColumnMappingEditor.tsx` already knows the source column's native type
  (`typeOf`) and shows it beside the source dropdown, but the target side shows only the column name (plus
  a PK badge) — no type, and no way to rename or retype it. `IProvisioner`/`CanonicalType.cs` already do
  source-native → canonical → target-native type mapping to build `CREATE TABLE`; showing an inferred
  target type here means calling that same mapping, not inventing a second one. Transform is a plain
  always-editable `<input>` today.
- **Mappings landing page.** `TableMappingsPanel.tsx`'s `MappingsIndex` currently redirects straight to
  the first mapping (or shows "No mappings yet — add one."). There is no grid of source tables, no
  per-table mapping-count, and no bulk-create path — every mapping is created one at a time via `+`.

## Design decisions

### Name inference

Default the mapping name to `<sourceSchema>.<sourceTable>` (schema included, per the note), computed
whenever the source table changes and the operator hasn't hand-edited the name. The usual
"autofill until touched" rule applies — once the operator types into the name field directly, inference
stops overriding it, the same convention `setTableName`'s schema-follows-name-pick already uses one level
down.

### Target table autofill

Choosing (or inferring) a source table also fills the target table field with the same table name (not
necessarily the same schema — the target's own schema selection stays independent), as a starting point
the operator can still change. This only *sets* the field; it doesn't re-lock it — same override-friendly
spirit as the name.

### Provisioning inheritance

`ReplicationTaskConfig` gains `provisioning?: ProvisioningConfig` (or equivalent), and
`TableMappingConfig.provisioning.createTargetTableIfMissing` becomes nullable/optional so "unset" means
inherit. `ProvisioningCard.tsx` gets the same `INHERITED` badge + override toggle shape `MappingSide`
already uses for connection/database — visual consistency with a pattern the operator has already learned
elsewhere in this same form.

### Column mapping editor: show-value-plus-pencil, applied three places

A small reusable interaction — a value shown as text, with a pencil icon that turns it into an editable
field — applied to:

- **Target column name** (rename), defaulting to the source column's name.
- **Target column type**, defaulting to the *inferred* type (source native type → canonical → target
  native type, via the existing `IProvisioner`/`CanonicalType` mapping) — shown even when nothing has been
  customized, and **not written into the stored mapping unless the operator edits it**. Only a real
  customization becomes stored state; the common case (accept the inference) stores nothing extra.
- **Transform**, for consistency of interaction even though it's a different kind of value (a SQL
  expression, not a name or a type) — the same show/pencil affordance rather than an always-open input
  box sitting in every row whether it's used or not.

Source column stays exactly as it is — a dropdown, no pencil, because picking which source column feeds
a row is a selection, not an edit.

### The mappings overview grid

Becomes the initial page of the mappings section (replacing the auto-redirect-to-first-mapping), listing
every table in the replication's *default source* database (its resolved source endpoint, unless a
mapping overrides it — the default is the replication's, per the "default" in the note) as a filterable
grid: table name, a mapping-count indicator (0 when unused, N when one table backs N mappings — the
one-source-to-many-mappings case already exists via source filters), a checkbox per row, and select-all.
"Create N mappings" bulk-creates one mapping per checked table, each with: the inferred name, the source
set to that table, the target autofilled to the same table name, and everything else — provisioning,
scripts, schedule-relevant settings — left unset so it inherits, exactly as a single hand-created mapping
would if the operator accepted every default.

This becomes the landing page; the existing per-mapping sidebar-and-editor view (`TableMappingsPanel`)
stays reachable the way it is today (picking a mapping, or `+` for one at a time) for anyone who wants to
configure a single mapping by hand rather than bulk-creating.

## Refinements (2026-08-28)

**Navigation to the grid**: a special, bolded **Overview** entry at the top of the existing mappings
sidebar (`TableMappingsPanel`'s `sidebar-list`), above the per-mapping list. The grid is a sibling of the
editor within the same sidebar-plus-pane shell, not a replacement for it — resolves the open question of
how the grid fits into the existing layout.

**Bulk create is one API call**, not N. The server creates all selected mappings within a single request.
Because this can take long enough to want feedback, and because SignalR already exists for live-run
progress (`run-metrics.md`), the bulk-create endpoint should report progress the same way — a
request that streams or pushes "created N of M" as it goes, rather than a bare request/response the UI
can only show as one opaque spinner for.

**Column rename needs to be an array, not a single value — settled, not left open.** A single
`renamedFrom` isn't enough: the operator can rename a target column more than once before provisioning
ever applies anything, and provisioning needs the full history to know what to actually run. So each
`ColumnMapping` gets a list of rename steps (from-name, to-name), and **each step tracks whether it has
been applied** to the real database yet. That last part is not decoration — it's what keeps a **swap**
(renaming column A to B and B to A at the same time) from corrupting data: applying renames blindly by
current name, without knowing which ones already landed, can double-apply a step or apply a stale one
against a column that already changed identity. The exact algorithm for safely sequencing a swap (or any
rename cycle) is real design work, not settled here — flagged as an open question for the implementation
phase, because it affects how provisioning's `ALTER TABLE` planning has to reason about the history, not
just how the UI stores it.

**New: audit for schema/table names combined into one string and later split back apart.** The concern:
combining is fine for a *label* an operator reads, but is a real bug the moment the combined string
becomes the *data* — a schema or table name that itself contains a literal `.` (quoted identifiers allow
this on every engine here supports) breaks any later `split('.')`, either misplacing the schema/table
boundary or silently producing garbage. Confirmed as a real, existing bug class, in two places:

- **`MappingSide.tsx`** (the non-`allowNewTable` table `<select>`, used for the source side): builds
  `${schema}.${table}` as the option `value`/`key` and the current selection's key, then does
  `e.target.value.split('.')` on change to recover `schema`/`table`. A schema or table name containing a
  `.` breaks this — likely exactly the bug already encountered.
- **`HookRenderer.QuoteMaybeQualified`** (`DataSync.Drivers.Generic/HookRenderer.cs`): splits an
  operator-supplied hook parameter value on `.` to decide whether it's a bare or schema-qualified name,
  documented in its own comment as the tradeoff that makes both spellings work. Same failure mode —
  `parts.Length == 2` assumes exactly one `.`, wrong for a qualified name whose schema or table itself
  contains one.

Both need fixing as part of this phase, not just flagging: `MappingSide`'s combine-then-split should
carry schema and table as a real pair (an object, or two-part key) rather than one string, matching the
`schema` + `table` fields already used everywhere else in this same component. `HookRenderer` needs a
scan for other instances of the same class before calling the audit complete — this file had one, but
nothing here checked every combine-point in the codebase, only the two `split('.')`-shaped ones a direct
search caught.

**New: the auto-mapping "columns appear late with the wrong source" bug — root cause identified.** In
`ColumnMappingEditor.tsx`, each row's source-column `<select>` is `value={m.sourceColumn}`. If the stored
`sourceColumn` name is not present among the just-loaded `sourceColumns` — which can genuinely happen for
a moment, since the whole editor is gated on `sourceColumns`/`targetColumns` both being loaded and then
renders every row from data that was already sitting in `mappings` — the browser's native `<select>`
silently displays the **first** `<option>` in the list, because no option matches the given `value`. The
component's own state (`m.sourceColumn`) hasn't actually changed; only the display has, which is exactly
the reported symptom (rows "jump in" once columns load, all showing the first source column) and exactly
why **Auto-map fixes it** — it overwrites every row's `sourceColumn` with a freshly computed, valid value,
masking rather than encountering the display bug. The fix is to stop relying on the browser's silent
fallback: when a row's stored `sourceColumn` (or `targetColumn`) doesn't match the currently loaded
metadata, say so explicitly (an empty/placeholder selection, or a visible "unknown column" marker) instead
of letting the select render as if the first item were chosen — which, left alone, an operator could
resave without ever noticing the mapping actually changed.

---

# Outcome

Agreed, as `implementation/todo/phase-045-table-mapping-ux-overhaul.md`.
