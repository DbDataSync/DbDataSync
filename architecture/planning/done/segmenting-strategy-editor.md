# A UI editor for segmenting strategies

**Status: resolved 2026-08-31 — a named, confirmed gap from phase 58's own retrospective, not a new
discovery.**

## The gap

Phase 58 (done) built segmenting strategies end to end — DuckDB/source-SQL/target-SQL/C# authoring, the
Backfill checklist, mapping-level defaults — but its own retrospective says plainly:

> No UI yet for authoring the strategies themselves. They are defined on the replication config and the
> mapping/backfill forms select from them, but the list is edited through the replication's settings
> JSON rather than a dedicated editor. A real strategy editor — with a Test button, in the spirit of
> phase 48's Checks editor — is the obvious follow-on and is not built here.

Confirmed in code: `ReplicationTaskConfig.SegmentingStrategies: List<SegmentingStrategyConfig>` (Name,
Kind, `Sql`, `ScriptName`, `Column`, `Parameters`) exists and is fully consumed — `BackfillForm.tsx`
already has a strategy picker (`backfill-strategy-select`) and previews a chosen strategy's candidates —
but nothing writes to `SegmentingStrategies` from the SPA. An operator hand-edits the replication's config
file to add or change one.

## Design: phase 48's Checks editor, same shape

`SegmentingStrategyConfig` was deliberately modeled on `VerificationCheckConfig` — the retrospective says
so directly ("an operator who has written a verification check should recognise every field here"). The
editor should be the same recognition: a list-with-inline-editor card (Name, Kind badge, edit/remove,
"Add strategy"), living on the Overview page next to where replication-level things already live (this is
replication-scoped config, not mapping-scoped — `EndpointsCard`/`ScriptBindingsCard`'s neighborhood, not
`TableMappingForm`'s).

Fields, by `Kind`:

- **DuckDb / SourceSql / TargetSql**: a `CodeEditor` (Monaco, `language="sql"`) for `Sql`, and a `Column`
  input. The `RunsAgainstAConnection` hint (`.hint.warn`, already styled per phase 58) carries over
  unchanged — this editor is exactly where an operator sets up a strategy that will "run unattended, on
  the replication's own schedule," so the warning belongs here even more than at the picker.
- **Script**: a `ScriptName` select (scripts implementing `ISegmentingStrategy`) plus `ParameterForm`
  bound to that script's declared parameters — phase 42 reuse, same as phase 48's `Script`-kind checks.

**A Test button**, per the retrospective's own suggestion, in phase 41's spirit: run the strategy (through
the same preview path `BackfillForm` already calls) and show its candidates right there, before saving —
so an operator finds out a DuckDB query is malformed while writing it, not the next time a scheduled
reload silently does nothing.

## What this does not change

- `SegmentingStrategyConfig`'s shape, `ISegmentingStrategy`, or the runner that executes strategies —
  phase 58's engine is unchanged; this is authoring UI only.
- `BackfillForm.tsx`'s existing picker/preview/checklist flow.

---

# Outcome

Agreed, as `implementation/todo/phase-061-segmenting-strategy-editor.md`.
