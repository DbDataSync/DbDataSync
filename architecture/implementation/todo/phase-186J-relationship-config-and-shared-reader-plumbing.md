# Phase 186J — Relationship config shape, validation, and shared reader plumbing

**Status**: Planned, not started.
**Plan reference**: `phase-185J-declared-relationships-and-foreign-column-lookups.md` (superseded by this
doc and its three siblings — see that doc's own Status line). This doc covers the foundation every other
split depends on: the config shape, its validation, catalog metadata for the foreign side, and the
`IChangeReader` plumbing both the batch-reader phase (187J) and the change-reader phase (188J) build on.

## What this phase builds

### Config — `src/DbDataSync.Core/Config/TableMappingConfig.cs`

```csharp
public sealed class RelationshipJoinKey
{
    public required string LocalColumn { get; init; }    // column on the mapping's own primary source table
    public required string ForeignColumn { get; init; }  // column on the foreign table
}

public sealed class RelationshipConfig
{
    public required string Name { get; init; }               // referenced by ColumnMapping.Relationship
    public string Schema { get; init; } = "dbo";
    public required string Table { get; init; }               // same ConnectionName+Database as Sources[0]
    public required List<RelationshipJoinKey> JoinKeys { get; init; }  // ANDed; at least one
}
```

- `TableMappingConfig` gains `List<RelationshipConfig> Relationships { get; init; } = [];`.
- `ColumnMapping` gains `string? Relationship { get; init; }` — `null` (the default, and every existing
  saved mapping) means `SourceColumn` is read from the primary source table, unchanged from today. Non-null
  names a `RelationshipConfig.Name`, and `SourceColumn` then names a column on *that* relationship's foreign
  table instead.
- **Self-joins are allowed** — a `RelationshipConfig` whose `Schema`/`Table` equal the mapping's own primary
  source is valid (decided in conversation: no reason to special-case it; the join-rendering mechanism in
  187J/188J doesn't care whether the foreign table equals the primary one, and self-referencing hierarchies
  — an employee row pointing at its own manager row in the same table — are a real, common case).

### Validation

A save-time check on `TableMappingConfig` needs to reject:

- `ColumnMapping.Relationship` naming a value not present in that same mapping's `Relationships[].Name`.
- A `RelationshipConfig` with zero `JoinKeys`.
- A `RelationshipConfig.Name` that collides with another relationship's name in the same mapping (ordinary
  case-insensitive uniqueness, matching how `ColumnMapping.TargetColumn` uniqueness or similar existing
  checks in this file are already enforced — exact precedent to mirror not traced this session, but the
  shape is standard).

No other constraint on `LocalColumn`/`ForeignColumn` beyond "non-empty" — whether they resolve to real
columns is a save-time metadata-refresh concern (next section), not a shape-validation one, matching how
`ColumnMapping.SourceColumn`/`TargetColumn` are validated today (presence, not existence against a live
catalog).

### Catalog metadata for the foreign side

`TableMappingConfig.SourceColumns` (phase 91's per-mapping cached-column snapshot) is scoped to the primary
source table only. A relationship's foreign table needs the same treatment, so that:

- The mapping editor's relationship-column picker (189J) can show real column names/types without a live
  catalog round-trip on every render.
- `BatchReloadReader`/`MsSqlBatchReloadReader`/the change readers (187J/188J) can resolve relationship
  columns the same cache-only way they already resolve the primary table's columns for segmenting-adjacent
  concerns.

Concretely: `MappingMetadataService.RefreshAsync` → `MappingColumnReader.ReadAsync` (the phase-91 call chain
that captures `TableMappingConfig.SourceColumns`/`TargetColumns` today) needs to also capture each declared
relationship's foreign-table columns, keyed by relationship name — e.g. a new
`Dictionary<string, List<CachedColumn>> RelationshipColumns` on `TableMappingConfig`, refreshed in the same
pass and by the same `POST .../refresh-metadata` action an operator already triggers after editing a
mapping. Exact storage shape (a dictionary keyed by relationship name vs. a list of
`(RelationshipName, List<CachedColumn>)` records) not decided here — whichever serializes cleanly through
this file's existing YAML/JSON round-trip is fine; there's no behavioral difference.

Because the foreign table is a real table (not a query source), this is an ordinary live catalog call
(`ITableCatalog.GetColumnsAsync`, the same mechanism `MappingColumnReader` already uses for the primary
table) — **no** need for the "run the query and read a live `DbDataReader`'s schema" mechanism the recent
custom-source-query feature needed (`ScriptTestService.TryReadColumnMetadata`); that mechanism exists
specifically because a query source has no catalog entry to introspect. A relationship's foreign side always
does.

### Shared reader plumbing — `IChangeReader.ReadChangesAsync` / `IStatementPreview.DescribeAsync`

A new explicit parameter, `IReadOnlyList<RelationshipConfig> relationships`, added to both signatures
alongside the already-present `columnMappings`/`sourceColumns`/`mappingName` — a mapping-level concept, not
a reader Kind's own free-form setting, so it belongs with those explicit parameters rather than smuggled
through the `options` string bag (`options` stays reserved for what it is today: a reader Kind's own
author-declared settings, e.g. `RawQueryReader`'s `query`). `PreviewRequest` (the record `DescribeAsync`
takes) gains the equivalent field.

Every existing `IChangeReader`/`IStatementPreview` implementation across every driver project picks up the
new parameter and ignores it, matching how most readers already ignore several of the interface's existing
parameters (e.g. `RawQueryReader` ignores `columnMappings`/`sourceColumns` today). This phase makes the
signature change and updates every call site to compile and pass `mapping.Relationships` through — it does
not make any reader *use* the parameter. That's 187J (batch readers) and 188J (change readers).

Also gains a relationship-aware foreign-column resolution helper, likely alongside
`CachedColumn`'s existing `RequireColumn`/`RequireAll` extension methods (`src/DbDataSync.Drivers.Generic/`
— exact file not identified this session), so 187J/188J's readers can resolve
`(RelationshipName, ColumnName) → ColumnMetadata` the same disciplined, cache-only way `RequireColumn`
already resolves a primary-table column.

## What this phase does not build

- No SQL generation, no `JOIN` rendering, no reader actually consuming `relationships` to change its
  statement — every reader still queries exactly as it does today; only the parameter exists and is
  threaded. See 187J/188J.
- No frontend — see 189J.
- No support for a relationship whose foreign side is a query source, or whose connection/database differs
  from the mapping's primary source — both remain out of scope, per 185J's own confirmed decisions.
- No cross-mapping relationship reuse (a per-connection catalog) — per-mapping only, per 185J.

## Open questions

1. **Exact uniqueness-check precedent to mirror** for `RelationshipConfig.Name` collisions — not identified
   this session; whoever implements should find and match the existing pattern for
   `ColumnMapping.TargetColumn` (or wherever else this file already enforces a same-mapping name
   uniqueness) rather than inventing a new validation idiom.
2. **`RelationshipColumns` storage shape** on `TableMappingConfig` — dictionary vs. list of records; no
   behavioral difference, pick whichever round-trips more naturally through the existing YAML converter.
3. **Where the new foreign-column resolution helper lives** — alongside `CachedColumn`'s existing
   extensions, or a new small type; not identified this session.

## How to verify when built

- Unit tests for `TableMappingConfig`/`ColumnMapping`/`RelationshipConfig`'s YAML round-trip (matching this
  repo's existing `*YamlRoundTripTests` pattern for other config additions, e.g.
  `DefaultSegmentingYamlRoundTripTests`), including a self-join relationship.
- Unit tests for the new validation checks (relationship not found, zero join keys, duplicate name).
- An integration test proving `refresh-metadata` populates `RelationshipColumns` for a real foreign table
  against a real SQL Server source.
- Every existing `IChangeReader`/`IStatementPreview` implementer still compiles and its existing tests still
  pass unchanged — this phase's interface change should be behaviorally invisible to every reader except
  the two/three that 187J/188J modify.
