# Phase 61 — A UI editor for segmenting strategies (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/segmenting-strategy-editor.md`

## What this covers

A replication-level editor for `SegmentingStrategyConfig` entries — today only creatable/editable by
hand-editing the replication's config file, per phase 58's own named gap.

## 1. `SegmentingStrategiesCard` on the Overview page

New card, list-with-inline-editor (`ColumnMappingEditor`/phase 48's Checks card shape): each strategy's
Name and Kind, edit/remove, "Add strategy." Saves through the existing replication upsert
(`useUpsertReplication`) with the modified `SegmentingStrategies` array — no new CRUD endpoint, same
pattern phase 48 established for checks.

## 2. The add/edit form, by `Kind`

- **DuckDb / SourceSql / TargetSql**: `CodeEditor` (Monaco, `language="sql"`) for `Sql`, a `Column`
  input. Show the existing `.hint.warn` connection notice (reused verbatim from `BackfillForm`'s
  `runsAgainstAConnection` check) for the two connection-bound kinds.
- **Script**: a `ScriptName` select scoped to scripts implementing `ISegmentingStrategy`, plus
  `ParameterForm` bound to that script's declared `ParameterDescriptor[]`.

## 3. Test button

Runs the strategy through the same preview path `BackfillForm.tsx` already calls (reuse the hook/query,
don't fork a second implementation), rendering candidates (label, range, selected) inline in the editor
before the strategy is saved.

## What this phase does not build

- Any change to `SegmentingStrategyConfig`, `ISegmentingStrategy`, or strategy execution — authoring UI
  only.
- A replication-level default/override layer beyond what phase 58 already scoped (mapping-level
  defaults, replication-level strategy definitions — unchanged).

## How to verify when built

- Adding a DuckDb strategy through the editor, saving, and selecting it in `BackfillForm` produces the
  same candidates the editor's own Test button showed.
- Adding a Script-kind strategy renders that script's declared parameters via `ParameterForm` and persists
  the entered values.
- Editing an existing strategy (defined previously by hand in config) round-trips correctly through the
  new editor without altering fields the operator didn't touch.
- The connection-warning hint appears for SourceSql/TargetSql/Script-that-touches-a-connection strategies
  and not for DuckDb ones, matching `BackfillForm`'s existing behavior.
- Full suite green, including a new Playwright flow: author a strategy through the UI, test it, use it in
  a backfill — closing the gap phase 58's retrospective named.

## Open questions

- None — scope and shape are settled by phase 58's own retrospective and existing `VerificationCheckConfig`
  precedent.
