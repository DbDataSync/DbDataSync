# Cached source/target metadata, captured on creation, refreshed only on request

**Status: resolved — ready for an implementation phase doc. Resolves the audit in
`architecture/planning/todo/metadata-queries-design-time-only.md`.**

## What the audit found

Every run-time introspection call site traced (see the audit doc, and the conversation that produced
it) falls into two real categories, not one:

- **`BatchReloadReader.ExpandAutoSegmentsAsync`** (auto-segment discovery) — inherently live. Sampling a
  column's actual value range/distribution has no stored substitute; the answer changes as data grows.
  **Resolved: leave as-is, no change.**
- **Everything else** — `WatermarkReader`/`BatchReloadReader`/`TriggerAuditReader`/`ScriptedQueryReader`
  querying source columns during a read, and every writer's `TargetShape.LoadAsync` plus
  `BatchInsertStagingProvider.StageAsync` querying target columns during a write — exists because
  `ColumnMapping` deliberately stores no type/key/nullability info (`TargetType`'s own doc comment: "the
  type is inferred... writing that inference into config would freeze today's answer against a source
  whose column later changes"). A real, deliberate anti-staleness decision, not an oversight — caching
  a table's actual shape risks a writer building a `MERGE` against a primary key that no longer exists,
  silently, well after the schema changed underneath it.

## The resolution

Cache it anyway, but make staleness a decision an operator makes deliberately, not something that
happens silently either way:

- **Capture source and target column metadata (`ColumnMetadata` — the existing
  `Name`/`NativeType`/`IsNullable`/`IsPrimaryKey`/`IsIdentity` shape, already used for introspection
  results elsewhere) when a mapping is created**, stored on `TableMappingConfig` itself.
- **A "Refresh metadata" action, and only that action, updates it.** Ordinary operation — every run,
  every pass — never re-queries the source/target catalog to update this cache and never silently picks
  up a schema change. "The standard operation of the tool shouldn't change behaviour without someone
  being involved in that decision."
- **This phase builds the cache and the UI only.** It deliberately does **not** yet change what
  `WatermarkReader`/`BatchReloadReader`/`TriggerAuditReader`/`ScriptedQueryReader`/every writer/staging
  provider actually do at read/write time — they keep live-querying exactly as today. Switching each of
  them to read the cache instead is a real behavior change (a run that used to always see the current
  schema now sees whatever was last captured or refreshed) and belongs in its own later phase, reviewed
  on its own, once the cache reliably exists to switch to.

## Design

- `TableMappingConfig` gains `SourceColumns`/`TargetColumns` (`IReadOnlyList<ColumnMetadata>`, naming
  implementation's call), empty until first captured.
- Capture reuses the introspection the mapping editor's column-mapping tab already performs at design
  time (a live `GetColumnsAsync`/`ListColumnsAsync` call, already fine per the audit) — persist what that
  fetch already returns into the new fields at save time, rather than adding a second, purpose-built
  query solely to populate a cache. Confirm exactly where that fetch happens today
  (`MappingSide.tsx`/`ColumnMappingEditor.tsx` and whatever API call backs it) before deciding the exact
  wire-up; don't assume the mechanism without checking current code.
- A new endpoint (e.g. `POST api/replications/{name}/table-mappings/{mappingName}/refresh-metadata`,
  alongside the existing `Upsert`/`Get` on `TableMappingsController`) re-runs the same introspection
  live and overwrites the stored cache, returning the updated mapping.
- UI: a "Refresh metadata" button on the mapping editor, visible for both source and target sides (or
  one button covering both — implementation's call once it's laid out), calling the new endpoint and
  showing the operator what changed, not just silently updating.

## What this phase should not do

- Change `ExpandAutoSegmentsAsync` or any auto-segmenting behavior — confirmed to stay live.
- Wire any reader, writer, or staging provider to actually *read* the new cache instead of
  live-querying — the cache exists and is refreshable this phase; nothing consumes it yet. That's the
  deliberate next phase, not this one.
- Backfill existing mappings' caches automatically — an existing mapping has empty
  `SourceColumns`/`TargetColumns` until an operator either edits it (triggering capture the same way
  creation does) or explicitly hits Refresh. No migration silently populates it behind anyone's back.

## How to verify

- A test asserting a newly created mapping's `SourceColumns`/`TargetColumns` are populated from the same
  introspection the column-mapping tab already performed, without a second live query.
- A test asserting Refresh, and only Refresh, updates the stored cache — an ordinary mapping save that
  doesn't touch source/target config leaves the cache untouched.
- A test asserting an existing (pre-this-phase) mapping's empty cache doesn't break anything reading the
  mapping — this phase adds fields nothing yet depends on.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-090-mapping-metadata-cache.md`.
