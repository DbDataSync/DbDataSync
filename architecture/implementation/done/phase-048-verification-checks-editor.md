# Phase 48 — A Checks editor for the Verify page

**Status**: Done
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

---

# Retrospective

Phase 43 built everything a check does and left it settable only by API call, so the screen that shows
results could not produce one. That is closed: the Verify page now adds, edits and removes checks, and
Playwright 34 configures one through the form, runs it, and reads the result — which is the assertion
phase 43's own test could not make, because it configured through the API.

## Chips, not a multi-select

The plan left the column picker's shape open. It is a row of toggle chips, because what is selected has
to be readable without opening anything: these choices decide what a result's rows *mean*, and a
`<select multiple>` shows a scrolling box where two of eleven items happen to be highlighted — the least
legible way to say "these two". A column the mapping no longer has is kept and marked rather than
dropped, the same rule the column mapping editor follows: it is what the check says, and losing it on
the next save would be a change nobody made.

## The threshold is entered as a percentage

Stored as a fraction, entered and displayed as a percent, because the results card already says
"differences under 60.00% are not flagged". Two spellings of the same number on one screen is worse than
a conversion in one place.

## No Test button, and why

The plan's other open question. A Test button for a `Sql` check would need a new endpoint that runs one
*unsaved* check against both databases — a second path into the verification executor, with its own
answer about what an unsaved, unnamed check is. The Run checks button already runs the saved checks and
shows the result, which is the same information one save away, and saving a check is not a
consequential act. Not built, and not because it was hard.

## The screen's own save, not a new endpoint

A check lives on the mapping, so the ordinary mapping upsert is what saves it — the whole config goes
back, so an edit here cannot quietly drop a field this screen does not render. The plan called this and
it held.

## A real bug found on the way out

The first full run of the new suite failed in test 25 with `Unexpected end of JSON input`. That was not
a flake: `ConfigRepository` wrote with `File.WriteAllText`, which truncates and then writes, while the
API serves reads straight off disk. A GET landing inside that window gets a fragment — and two operators
on one replication hit the same race, not just a test. Fixed by writing to a sibling temp file and
renaming, in its own commit, with a test that fails in under 200ms without it.

## Verification

- Playwright 34 — adding a check through the form, the fields following the kind (Sum asks for measures,
  Row count does not), grouping picked as chips, the saved config matching what was entered, the check
  actually running and producing a result, editing pre-filling from what was saved, the threshold
  round-tripping through its percentage, and removal persisting.
- Test 24's comment updated: it configures through the API deliberately, and now says which half of the
  pair it is.
- Full .NET suite green: 712 tests. Playwright: 36 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions

- ~~**A Test button for `Sql` checks.**~~ Not built, for the reason above.
- ~~**The column-multi-picker's UI.**~~ Toggle chips.
