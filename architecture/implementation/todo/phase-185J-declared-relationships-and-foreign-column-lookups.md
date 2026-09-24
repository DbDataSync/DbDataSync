# Phase 185J — Declared relationships to a foreign source table, for column lookups

**Status**: Planned, not started.
**Plan reference**: none — raised directly in conversation ("add a feature to support relationship, and a
lookup feature to source entities"), narrowed by asking rather than guessing on four shape decisions this
repo's own "no migration" policy makes expensive to get wrong later.

## Why

`RawQueryReader`'s own doc comment (`src/DbDataSync.Drivers.Generic/RawQueryReader.cs`) already names "a
join across tables the mapping model has no notion of" as one of the reasons that escape-hatch reader
exists. Today that's the *only* way to enrich a mapped row with a value from another table: hand-write
the whole `SELECT` yourself as a query source, which discards every other reader capability a table-backed
mapping gets — segmenting, catalog-driven column mapping, incremental read modes, the mapping editor's own
column pickers.

This phase gives the mapping model itself a narrow, first-class join capability: an operator declares a
named **relationship** from a mapping's primary source table to a foreign table, then maps a target column
straight from a column reached through that relationship. The reader adds one `JOIN` per relationship
actually used by the mapping's column mappings, in the same statement it already builds — not a new
runtime per-row lookup stage, not a second query per row.

## Decisions made (asked, not guessed)

Each of these has a "the other way" that is defensible; asked rather than assumed because this repo has no
config migration path, so whichever shape ships first is the only one anyone gets without a second breaking
change.

1. **Declared per mapping**, not per connection. A new list alongside `ColumnMappings` on
   `TableMappingConfig`, visible and usable only within that one mapping. Simpler: no cross-mapping reuse,
   shared naming, or "someone edited the relationship a different mapping depends on" concerns to design.
2. **Same connection and database as the mapping's primary source, only.** The only case where "the reader
   performs 1 join" is a plain, dialect-neutral `JOIN` that means the same thing across every driver
   (MsSql/Postgres/MySQL/Oracle/JDBC/descriptor-generic) with no per-engine cross-database qualified-name
   syntax to support.
3. **Ships first for the reload/full-snapshot readers** (`BatchReloadReader` in `Drivers.Generic`,
   `MsSqlBatchReloadReader` in `Drivers.MsSql`) — the ones that already build a plain
   `SELECT <projection> FROM table WHERE <predicate>` with a swappable projection
   (`SourceProjection.Render`). Incremental/CDC/Change-Tracking readers — which already join a change table
   to the base table themselves — are an explicit, separate follow-up, not attempted here. Same phased shape
   the recent custom-source-query feature used (DuckDB-only first, generalized in a second pass — see
   `git log`, "Support a custom source query for any driver, not just DuckDB").
4. **A row with no matching foreign row is kept, with the looked-up columns `NULL`** (`LEFT JOIN`), not
   dropped (`INNER JOIN`). A replication tool silently losing source rows because a foreign key didn't
   resolve — a late-arriving or since-deleted dimension row, say — would be a surprising default; an
   operator who genuinely wants INNER semantics can already filter on the looked-up column being non-null
   via the mapping's existing per-column `Transform`/row-level `Filter`.

## What this phase will build

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
  saved mapping) means `SourceColumn` is read from the primary source table, unchanged from today.
  Non-null names a `RelationshipConfig.Name`, and `SourceColumn` then names a column on *that*
  relationship's foreign table instead.

### Reader plumbing — `IChangeReader.ReadChangesAsync` and `IStatementPreview.DescribeAsync`

A new explicit parameter, `IReadOnlyList<RelationshipConfig> relationships`, added alongside the
already-present `columnMappings`/`sourceColumns`/`mappingName` — a mapping-level concept, not a reader
Kind's own free-form setting, so it belongs with those explicit parameters rather than smuggled through the
`options` string bag (`options` stays reserved for what it is today: a reader Kind's own author-declared
settings, e.g. `RawQueryReader`'s `query`). Every existing `IChangeReader` implementation across every
driver project picks up the new parameter and ignores it, matching how most readers already ignore several
of the interface's existing parameters (e.g. `RawQueryReader` ignores `columnMappings`/`sourceColumns`
today) — only the two reload readers named above actually consume it in this phase.

### SQL generation

- `src/DbDataSync.Drivers.Generic/SourceProjection.cs` — `Render`/`RenderColumn` need to alias a
  relationship-sourced `ColumnMapping` to that relationship's own join alias (e.g. `r0.[Region]`) instead of
  the primary table's `reference`, when `mapping.Relationship` is set. This is the same seam the class
  already has for the Change Tracking reader's own aliased, joined statement (`reference` producing
  `base.[Region]` — see the method's existing doc comment) — no shape change to `Render`'s public contract,
  only new branching inside `RenderColumn` keyed on `mapping.Relationship`.
- `src/DbDataSync.Drivers.Generic/BatchReloadReader.cs`'s `BatchReloadStatement.BuildRead` gains a
  `relationships` parameter: for each relationship actually referenced by `columnMappings`, render
  `LEFT JOIN {dialect.QualifyTable(schema, table)} AS r{i} ON {primaryAlias}.[{LocalColumn}] = r{i}.[{ForeignColumn}]`
  (ANDed across a relationship's own multiple join keys), and alias the primary table itself once any
  relationship is present — mirroring why the Change Tracking reader already aliases its own base table.
  `BuildRange`(used only for auto-segmenting) is unaffected — segmenting reasons about the primary table's
  own column extent, not a joined one.
- `BatchReloadReader`/`MsSqlBatchReloadReader` thread the new `relationships` parameter into both
  `BatchReloadStatement.BuildRead` and `SourceProjection.Render`, in `ReadChangesAsync` and `DescribeAsync`
  alike (the preview surface has to show the real statement, joins included).

### Validation

A save-time check needs to reject: a `ColumnMapping.Relationship` naming a relationship absent from that
same mapping's `Relationships` list, and a `RelationshipConfig` with zero `JoinKeys`. Exact validation call
site/shape not traced this session (see Open questions).

## What this phase does not build

- **No incremental/CDC/Change-Tracking reader support.** A mapping that declares a relationship is only
  usable while that mapping's active reader is the reload override, until a follow-up phase extends this.
  Whether an operator combining a relationship with an incompatible reader Kind should get a save-time
  validation error or a silent no-op is not decided here.
- **No cross-database or cross-connection relationships.**
- **No relationship reuse across mappings** (a per-connection catalog). If wanted later, that's a distinct,
  larger follow-up — naming, edit/delete semantics, and migrating existing per-mapping declarations into a
  shared catalog are all real design questions a per-mapping-only v1 sidesteps entirely.
- **No relationship whose foreign side is itself a query source.** The foreign side is always a real,
  `dialect.QualifyTable`-reachable table name — not a `RawQueryReader`/`Query`-kind mapping.
- **No runtime per-row lookup or caching mechanism.** Explicitly not what "the reader performs 1 join"
  means — this is pure SQL-generation added to an existing statement, not a new pipeline stage between read
  and stage. (An earlier framing of this feature considered a `TransformPipeline`/`IRowTransform`-shaped
  per-row enrichment stage with an on-demand second connection — see `IRowTransform`,
  `src/DbDataSync.Scripting.Abstractions/IRowTransform.cs`, and `TransformPipeline`,
  `src/DbDataSync.Scripting/TransformPipeline.cs`, for that mechanism's shape if this constraint is ever
  revisited — but the user's own framing was specifically "the reader performs 1 join," which this phase
  takes as the actual requirement, not a suggestion among several.)

## Open questions

1. **Exact validation call site/shape** for "`Relationship` name must exist among the mapping's own
   `Relationships`", "a `RelationshipConfig` needs at least one join key", and whether a relationship whose
   `Schema`+`Table` equal the mapping's own primary source (a self-join) should be allowed or rejected — not
   decided here; the mapping-save validation pipeline's current structure wasn't traced this session.
2. **Frontend components.** The mapping editor's column-mapping tab needs a source-column picker that also
   surfaces relationship columns (e.g. grouped/prefixed as "Customer → Region"), and a relationships editor
   (name, foreign schema/table picker, join-key pairs) — likely reusing whatever component already lets an
   operator pick a source table/columns elsewhere in the mapping editor, but the exact component(s) were not
   identified this session.
3. **Whether a relationship's foreign table needs its own `CachedColumn` capture.** Phase 91's per-mapping
   catalog cache (`sourceColumns`/`TableMappingConfig.SourceColumns`) is scoped to the primary source table
   only. `RequireColumn`/`RequireAll`-style helpers a relationship column picker or auto-segmentation might
   want to reuse would need the foreign table's columns cached too — which call site would grow to fetch and
   cache them (likely `MappingMetadataService.RefreshAsync`/`MappingColumnReader.ReadAsync`, per phase 91,
   but not confirmed) is not traced here.
4. **Preview surface shape.** Whether `DescribeAsync`'s rendered statement should show the JOIN(s) as part
   of the single existing `SourceRead` `PreviewStatement`, or as their own stage — the former mirrors how a
   `ColumnMapping.Transform` already alters `SourceRead`'s SQL text inline, and is the likely answer, but not
   decided here.
5. **Whether relationship columns need their own "preview and capture real metadata" flow**, the way the
   recent custom-source-query feature captures real column metadata off a live `DbDataReader`
   (`ScriptTestService.TryReadColumnMetadata`) — so a relationship's looked-up columns get real
   native-type/nullability metadata rather than the blank/guessed metadata a plain catalog browse of the
   foreign table might already provide for free (a relationship's foreign table is a real table, unlike a
   query source, so a live catalog call may already be sufficient — not decided here).

## How to verify when built

- Unit tests for `BatchReloadStatement.BuildRead`'s new `JOIN` rendering — no live server needed, matching
  that class's own doc comment ("separated so it can be asserted without a live server"): single join key,
  multiple join keys (ANDed), multiple relationships (aliased `r0`/`r1`/...), and a relationship declared
  but not referenced by any `ColumnMapping` (should not render a join at all — no wasted join for an unused
  relationship).
- An integration test against a real SQL Server source proving `LEFT JOIN` behavior end to end: a primary
  row whose foreign key matches gets the looked-up value; a primary row whose foreign key has no match is
  still present in the read, with the looked-up column `null` — not silently dropped.
- A Playwright spec covering the new relationships editor and the column-mapping tab's relationship-column
  picker, once the frontend components in Open Question 2 are identified and built.
