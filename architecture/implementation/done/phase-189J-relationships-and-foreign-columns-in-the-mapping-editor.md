# Phase 189J — Relationships and foreign columns in the mapping editor

**Status**: Built. See Retrospective.
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

## Retrospective

Built as designed, with the three open questions resolved and one real regression found and fixed by the
existing Playwright suite — not assumed up front.

**Open questions, resolved:**

1. **Foreign schema/table picker**: not a shared component — `MappingSide.tsx`'s own "Table" picker is
   inline JSX, not extracted. `RelationshipsCard.tsx`'s own picker mirrors its exact shape (an indexed
   `<select>` over `useTables(connectionName, database)`'s result, schema and table set together from
   the picked entry) rather than trying to share code across two screens with different surrounding
   layout — the same "same idiom, not a shared component" judgment call this codebase already makes
   elsewhere for near-identical pickers.
2. **`<optgroup>`**: not used anywhere else in this codebase, and adopted here anyway rather than a
   prefixed-label workaround — it is the native, semantically correct way to group a `<select>`'s
   options, needs no new styling to render acceptably, and a prefixed-label string would have made the
   encoded `relationship\u0000column` value (needed regardless, to disambiguate a relationship's column
   from a same-named primary one — see below) redundant with the *visible* label doing the same job twice.
3. **Reader-Kind gating**: not built. By the time this phase landed, 187J and 188J both already shipped,
   covering every reader Kind this feature initially targets (batch reload, Watermark, Change Tracking,
   CDC) — so the "what if 188J hasn't shipped yet" scenario the doc raised never materialized, and gating
   the UI on reader Kind would have been speculative complexity for a gap that closed before this phase
   started.

**What was built, concretely**: `RelationshipsCard.tsx` (new) — a list editor. Placed on the Column
Mapping tab itself, between `ColumnMappingEditor` and `CachedMetadataCard`, not a tab of its own as first
built and as this doc originally said here — moved same-session, on the judgment that a relationship has
no reason to exist except to be picked from in the column mapping right above it, so a separate tab read
as managing something unrelated rather than a step in the same task. Each relationship
row picks a foreign table from the same connection/database the primary source resolved to, and a
repeatable join-key editor (local column from the primary table's own `sourceColumns`, foreign column
fetched live for that specific relationship's table — via a new `useRelationshipColumns` hook,
`useQueries`-based like the existing `useTableMappingDetails` precedent, since the number of relationships
varies per mapping and a hook cannot be called a variable number of times). `ColumnMappingEditor.tsx`'s
single flat `<select>` gained an `<optgroup>` per relationship alongside its existing ungrouped
primary-table options, picking a grouped option now sets both `sourceColumn` and `relationship` in one
update. `TableMappingForm.tsx` carries `relationships` as ordinary draft state, included in the dirty-check
and the save payload the same way every other field is. No change to the persisted `relationshipColumns`
cache path — 186J's own `refresh-metadata` endpoint (`CachedMetadataCard`'s existing "Refresh" button)
already captures it server-side; this editor reads relationship columns live, the same way it already
reads the primary source's and target's.

**The real regression, found by the existing suite, not assumed**: the column `<select>`'s "not on the
source" fallback option (for a saved mapping whose `sourceColumn` no longer matches anything real) had its
wording changed to "not found" while adding the equivalent relationship-aware check — and
`golden-path.spec.ts`'s own step 34 asserts the literal text `'not on the source'`. Caught by running the
*existing* Playwright suite against this change, not by writing a new test for it: the fix keeps the
original wording for a primary-table mapping and adds a distinct `"<relationship> → <column> — not found"`
message only when the mapping actually names a relationship, so neither case lies about which table the
missing column was expected on.

**A second, smaller thing traced rather than assumed**: two `ColumnMapping`s can legitimately name the
same bare column string from two different sources (a relationship's foreign table sharing a name with the
primary table, or with another relationship — the exact collision 187J's own retrospective found to be the
*common* case for a lookup table's own "Id"). A plain `value={m.sourceColumn}` on the `<select>` cannot
tell those apart, so the option value is `relationship\u0000column` when relationship-sourced, decoded back
into both fields on pick — checked by writing the encode/decode as pure functions and reading them back
before wiring the `<select>`, not discovered as a live bug.

**Verified for real**: `tsc -b` and `oxlint` clean (no new warnings in any touched file), `vitest run`
86/86 (all pre-existing — no new unit tests were the right level for this feature; the round-trip claim is
what a browser-driven test can prove and a unit test can only assert about internals). A new Playwright
spec, `mapping-relationships.spec.ts`, stubbed at the network boundary like `mapping-column-add.spec.ts`'s
own precedent: declares a relationship (name, foreign table, one join key) in the card, maps a target
column through it in Column Mapping, saves, and asserts on the actual PUT body — both the relationship
declaration and the column mapping's `relationship` field survive, which is the thing "round-trips on
reopening" actually has to prove, not something to infer from what the screen shows before a save.
Confirmed by re-running the full, unfiltered `golden-path.spec.ts` (46/46, including the fixed step 34) and
the broader mapping/column-tagged subset. **Not manually checked**: the Preview SQL popup's rendering of a
real relationship JOIN inline in the SourceRead stage's statement text — the doc's own "worth a manual
check" item, left for whoever next has a live browser session against a real relationship-using mapping,
same as it was scoped.
