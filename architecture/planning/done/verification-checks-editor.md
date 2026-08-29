# A Checks editor for the Verify page

**Status: resolved 2026-08-28 — a confirmed, known gap, not a discovery.**

## The question as asked

"How would someone configure Checks for the Verify page of a table mapping? I don't see anywhere it can
be configured."

## Answer: there is nowhere, yet — this was left open on purpose

Phase 43 (`sync-verification-queries`, done) built the whole run/read/compare path — `TableMappingConfig`
already has a `verification: VerificationCheckConfig[]` field, `VerificationPanel.tsx` runs checks and
renders results with source/target/delta and the read-time gap — but its own retrospective says plainly:

> There is no editor for checks yet. They are configured through the API, and the screen runs and reads
> them. A check is a handful of typed fields over phase 42's parameter system, which is why that phase
> came first — but the form is not built, and the Playwright test configures through the API and says so.

So this is a known, named gap phase 43 deliberately deferred once phase 42 (the parameter system it needs)
was confirmed to exist. This doc is that follow-on.

## Grounding in the current code

- `VerificationController` has run/list/get, and nothing to create, edit, or delete a check —
  configuring `TableMappingConfig.Verification` goes through the same mapping upsert every other part of
  a mapping already uses (`PUT /api/replications/{r}/mappings/{m}`), no new CRUD endpoint needed for the
  list itself.
- `VerificationCheckConfig` (`types.ts`): `name`, `kind` (`RowCount | Sum | Sql | Script`), `groupBy:
  string[]`, `measures: string[]`, `sourceSql`/`targetSql`, `scriptName`, `parameters: Record<string,
  string>`, `filter`, `differenceThreshold`. `groupBy`/`measures` are plain string arrays, not
  `ParameterDescriptor`-shaped — they're the columns a check compares, not a script's own settings.
- **`ParameterForm` (phase 42) exists and is exactly the right tool for the `Script`-kind case**:
  `ScriptConfig.parameters: ParameterDescriptor[]` is already fetched via `useScripts()`, and
  `ParameterForm` already renders `ColumnPicker`/typed/vararg parameters generically — the same component
  `ScriptBindingsCard` already uses for script parameters elsewhere.
- **`groupBy`/`measures` are not naturally `ParameterForm` territory**, because their shape (`string[]`)
  is fixed by `VerificationCheckConfig` itself, not declared per-script. They need a column-multi-picker —
  the same "resolves against the mapping, not raw source/target columns" idea phase 43 established for
  column pickers generally, built as its own small control rather than routed through
  `ParameterDescriptor`.

## Design

A **Checks** card on `VerificationPanel.tsx` (the Verify page), above the results list — configuration
and results share the tab an operator is already looking at, rather than living somewhere else on the
mapping.

- **List**: each configured check, its kind, and an edit/remove action — same list-with-inline-editor
  shape `ColumnMappingEditor` already uses for column mappings, not `ScriptBindingsCard`'s single-slot
  inherit/none/bound shape (verification checks are a repeatable list per mapping, not one slot).
- **Add/edit form**, fields conditional on `kind`:
  - **RowCount**: a `groupBy` column-multi-picker (against the mapping's column mappings).
  - **Sum**: `groupBy` and `measures` column-multi-pickers.
  - **Sql**: `sourceSql`/`targetSql` as `CodeEditor` (Monaco, `language="sql"`) fields — generic-or-per-
    dialect per phase 43's design — plus `groupBy`/`measures` declared by hand, since a hand-written
    query's result shape isn't something DataSync can infer.
  - **Script**: a `scriptName` picker (scripts of the verification-query-generator kind) and
    `parameters` rendered through `ParameterForm`, driven by that script's own declared
    `ParameterDescriptor[]` — reusing phase 42 directly rather than building a second parameter renderer.
  - **Common to all kinds**: `filter` (a `CodeEditor` SQL predicate, same pattern as the mapping's source
    filter) and `differenceThreshold` (a number input).

## What this does not cover

- A **Test** button for a `Sql`-kind check. Phase 43's retrospective flagged this as worth revisiting —
  phase 41's Test treatment lands on the script path, where there's a script to run against generated
  input; a hand-written SQL check's nearest equivalent is just running it (the existing Run checks
  button). Related, but a separate decision from building the editor itself.
- Any change to how a check *runs* or how results are computed/displayed — this is authoring only.

---

# Outcome

Agreed, as `implementation/todo/phase-048-verification-checks-editor.md`.
