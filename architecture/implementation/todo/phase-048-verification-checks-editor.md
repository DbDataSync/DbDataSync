# Phase 48 — A Checks editor for the Verify page (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/verification-checks-editor.md`

## The gap

Phase 43 built running, storing, and reading verification results, and its own retrospective names what
it didn't build: "A check is a handful of typed fields over phase 42's parameter system... but the form
is not built." `TableMappingConfig.verification` can currently only be set via a direct API call —
`VerificationPanel.tsx` has nothing to add, edit, or remove a check.

## The fix

### A Checks card on `VerificationPanel.tsx`

New card above the existing results list, listing `mapping.verification` (name, kind, group/measure
summary) with edit and remove actions, and an "Add check" affordance — list-with-inline-editor, the same
shape `ColumnMappingEditor.tsx` uses for its rows, not `ScriptBindingsCard`'s single-slot toggle (a
mapping has a *list* of checks, not one).

Saves go through the existing mapping upsert (`useUpsertTableMapping`) with the modified
`verification` array — no new CRUD endpoint needed.

### The add/edit form, by kind

- **`RowCount`**: one column-multi-picker for `groupBy`, resolved against the mapping's column mappings
  (mapped-pair identity, same reasoning as phase 43's column pickers generally — an aliased column is one
  selection).
- **`Sum`**: `groupBy` and `measures`, both column-multi-pickers over the mapping's columns.
- **`Sql`**: `sourceSql` and `targetSql` as `CodeEditor` (Monaco, `language="sql"`, matching the
  Setup card's phase-45 Monaco fix and the mapping's own source-filter editor) — plus `groupBy`/
  `measures` as the same column-multi-pickers, entered by hand since a raw SQL check's result shape isn't
  inferrable.
- **`Script`**: a `scriptName` select (scripts whose kind is the verification-query-generator slot from
  phase 43) and a `ParameterForm` bound to that script's `ParameterConfig.parameters` — direct reuse of
  phase 42's component and data, the same way `ScriptBindingsCard` already consumes it.
- **Common**: `filter` (`CodeEditor`, SQL predicate) and `differenceThreshold` (number input, 0 meaning
  "any difference").

### The column-multi-picker

New, small component: a multi-select of the mapping's column mappings (target names, since checks are
described in target terms per phase 43), used for `groupBy`/`measures` wherever the kind needs them. Not
routed through `ParameterForm`/`ParameterDescriptor` — `groupBy`/`measures` are plain `string[]` fields on
`VerificationCheckConfig` itself, fixed by the type, not declared per-script.

## What this phase does not build

- A Test button for `Sql`-kind checks — flagged by phase 43 as worth revisiting, but a separate decision
  (see Open questions).
- Any change to how a check runs, or to result computation/display.
- A new API endpoint for check CRUD — the existing mapping upsert already covers it.

## How to verify when built

- Adding a `RowCount` check with a `groupBy` selection, saving, and running it produces a result matching
  what the API already computes for that config (exercised via the API today, per phase 43's tests).
- Adding a `Sum` check with both `groupBy` and `measures` set.
- Adding a `Sql` check with generic SQL, and a second with per-dialect source/target SQL, both round-trip
  correctly and produce the expected result shape when run.
- Adding a `Script` check renders that script's declared parameters via `ParameterForm` and the saved
  `parameters` map matches what was entered.
- Editing an existing check pre-fills the form correctly for every kind; removing one updates the list and
  persists.
- `differenceThreshold` and `filter` save and are respected by an existing check's next run (already true
  server-side per phase 43 — this just confirms the new UI writes them correctly).
- Full suite green, including a new Playwright flow: configure a check through the UI, run it, see a
  result — closing the gap the phase 43 retrospective named (its own Playwright test configured through
  the API).

## Open questions

- Whether to also add the Test button for `Sql`-kind checks phase 43's retrospective flagged, or leave it
  for later — not required to close the "no editor" gap, but adjacent enough to reconsider while building
  this.
- Exact UI for the column-multi-picker (a dropdown with checkboxes, a tag-style multi-select, etc.) — an
  implementation detail.
