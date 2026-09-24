# Phase 189J — Relationships and foreign columns in the mapping editor

**Status**: Planned, not started. Depends on `phase-186J-relationship-config-and-shared-reader-plumbing.md`
(needs `RelationshipConfig`, `ColumnMapping.Relationship`, and `RelationshipColumns` metadata to exist and
be reachable through the API before there's anything real for this UI to read or write). Does not depend on
187J/188J — the editor can be built and reviewed against saved config alone; the *result* of running a
mapping that uses a relationship isn't needed to build the screen that declares one.
**Plan reference**: `phase-185J-declared-relationships-and-foreign-column-lookups.md` (superseded — its
Open Question 2 named this as untraced; this doc is that tracing).

## What exists today, traced for this doc

- `TableMappingForm.tsx` (`src/DbDataSync.Web/src/pages/replication-detail/`) is the mapping editor's top
  level. It holds `source: SourceTableSpec` state (`useState<SourceTableSpec>`) and already composes
  several sibling cards as independent pieces of the same form — `SourceFilterCard.tsx`,
  `DefaultSegmentingCard.tsx`, `ProvisioningCard.tsx` are the existing precedent for "a self-contained card
  that reads/writes one slice of the mapping's config."
- `ColumnMappingEditor.tsx` is the column-mapping tab. It takes a flat `sourceColumns: ColumnMetadata[]`
  prop and renders one `<select>` per row (`sourceColumns.map((c) => <option key={c.name} value={c.name}>
  {c.name}</option>)`) — an operator picks one source column name per target column, from a single flat
  list.
- `QuerySourcePanel.tsx`/`QueryEditorDialog` is the precedent for a self-contained popup editor with its own
  "run and see real columns" flow, from the recent custom-source-query feature — **not** the shape this
  phase needs, per 186J's own resolution: a relationship's foreign side is a real, catalog-introspectable
  table, so there's no "write SQL, run it, capture what came back" step the way a query source needs.

## What this phase builds

### `RelationshipsCard.tsx` (new) — attached in `TableMappingForm.tsx` alongside the existing sibling cards

A list editor: each relationship has a name, a schema/table picker for the foreign side (reusing whatever
component already lets an operator pick a source table elsewhere in this same form — likely the same
picker `TableMappingForm.tsx` itself uses for the mapping's own primary `source`, since 186J's "same
connection and database" constraint means this picker is browsing the identical connection/database the
primary source already resolved to, not a fresh one), and a repeatable join-key-pair editor (local column /
foreign column, both pickable from already-cached columns — the primary table's `sourceColumns` for
"local", the relationship's own newly-cached `RelationshipColumns[name]` for "foreign," once a table is
chosen).

Self-joins (185J/186J: allowed) need no special UI — picking the mapping's own primary schema/table as a
relationship's foreign side is just a normal pick from the same table picker, nothing to disable or warn
about.

### `ColumnMappingEditor.tsx` — extended, not replaced

The single flat `<select>` needs relationship columns folded in, grouped/distinguishable from the primary
table's own columns — e.g. an `<optgroup>` per relationship (label "Customer", options "Region", "Name", ...
sourced from `RelationshipColumns["Customer"]`) alongside the existing ungrouped primary-table options, or
an equivalent visual grouping if this codebase's existing `<select>` styling doesn't use `<optgroup>`
elsewhere (not checked this session). Picking a relationship-grouped option needs to set both
`ColumnMapping.SourceColumn` (the foreign column name) **and** `ColumnMapping.Relationship` (the
relationship name) — today's `<select>` only ever writes one field per pick, so this is a real (if small)
change to the row's own update handler, not just a bigger options list.

The existing "not currently in `sourceColumns`" fallback branch (`{!sourceColumns.some((c) => c.name ===
m.sourceColumn) && (...)}` — shown for a mapping whose saved `SourceColumn` no longer matches anything, e.g.
after a schema change) needs the equivalent check extended to also look in the relevant relationship's own
cached columns before concluding a saved mapping now points at nothing.

### Preview / SQL visibility

Per 186J/187J's resolved "inline in the existing `SourceRead` stage" decision, no new preview UI surface is
needed — whatever already renders a mapping's `PreviewStatement`s (the existing Preview SQL popup this
session's own earlier work touched — see recent commits "Collapse the generated-column-expression list into
a popup") already shows the full statement text, joins included, with zero changes required here. Worth a
manual check once 187J/188J land that the rendered JOIN reads clearly in that popup, but no new component.

## What this phase does not build

- No live "browse and preview" flow for a relationship's foreign table — picking it from the schema/table
  picker and refreshing metadata (186J's `RelationshipColumns` capture) is sufficient; there is no
  equivalent to `QuerySourcePanel`'s "run it and see what came back" step because there's nothing to run.
- No change to `TableMappingForm.tsx`'s primary-source picker itself — only a new sibling card reusing
  whatever that picker already is.
- No UI for reader-Kind-specific relationship availability (e.g. graying out relationships for a mapping
  whose reader is Change Tracking vs. CDC vs. a reload) — 188J's scope covers CT/CDC/Watermark alongside
  187J's batch readers, so by the time this phase ships, every reader kind this feature initially targets
  should already support relationships; if a gap remains, that's this phase's own open question below, not
  assumed away.

## Open questions

1. **Exact foreign schema/table picker component to reuse** — named above as "likely the same picker
   `TableMappingForm.tsx` itself uses for the mapping's own primary source," but the actual component
   (inline JSX vs. a named sub-component) wasn't identified by name this session.
2. **Whether this codebase's `<select>` styling already uses `<optgroup>` anywhere**, or whether relationship
   grouping needs a different visual treatment (e.g. a prefixed label string like "Customer → Region" as a
   single flat option, avoiding an `<optgroup>` dependency entirely) — not checked this session.
3. **Whether every reader Kind 187J/188J cover is actually shipped by the time this phase starts.** If 188J
   ships after this phase, the editor would let an operator declare a relationship and map columns through
   it for a reader that doesn't yet honor it — worth deciding whether the UI should gate on reader Kind at
   all, or simply let a not-yet-supported combination surface as a run-time error the way an unrelated
   misconfiguration would, matching this codebase's general preference for real errors over UI-side
   prediction of backend capability.

## How to verify when built

- `tsc -b` / `oxlint` clean, `vitest run` green for the new/extended components, matching this repo's
  standard frontend verification bar.
- A Playwright spec: declare a relationship in the new card, map a target column through it in
  `ColumnMappingEditor`, save, reload the mapping, and confirm both the relationship and the
  relationship-sourced column mapping round-trip correctly through the saved config — the same "round-trips
  on reopening" bar this repo's other config-editing specs already hold themselves to (see, e.g., phase
  184M's own retrospective for the shape of that kind of assertion).
- Manual check of the Preview SQL popup against a real mapping using a relationship, confirming the rendered
  `JOIN` is legible inline in the `SourceRead` stage's statement text.
