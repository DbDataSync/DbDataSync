# Phase 186J — Relationship config shape, validation, and shared reader plumbing

**Status**: Built. See Retrospective.
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

**Not built, on reflection**: a relationship-aware foreign-column resolution helper alongside
`CachedColumn`'s `RequireColumn`/`RequireAll`. Tracing what 187J/188J's own `JOIN` rendering actually needs
— `RelationshipConfig.JoinKeys`' own `LocalColumn`/`ForeignColumn` strings, and `ColumnMapping.SourceColumn`
for the projected alias — neither needs a column's *type*, only its *name*, both already sitting on the
config objects with no lookup required. `RelationshipColumns` (the cache below) turned out to be read
infrastructure for 189J's picker, not something the read/write pipeline itself consults — see Retrospective.

## What this phase does not build

- No SQL generation, no `JOIN` rendering, no reader actually consuming `relationships` to change its
  statement — every reader still queries exactly as it does today; only the parameter exists and is
  threaded. See 187J/188J.
- No frontend — see 189J.
- No support for a relationship whose foreign side is a query source, or whose connection/database differs
  from the mapping's primary source — both remain out of scope, per 185J's own confirmed decisions.
- No cross-mapping relationship reuse (a per-connection catalog) — per-mapping only, per 185J.

## Open questions — resolved during implementation

1. **Uniqueness-check precedent**: no existing precedent needed mirroring — a plain
   `HashSet<string>(StringComparer.OrdinalIgnoreCase)` walk over `mapping.Relationships` in
   `ConfigValidation.ValidateRelationships`, same shape every other check in that file already uses.
2. **`RelationshipColumns` storage shape**: `Dictionary<string, List<CachedColumn>>` keyed by relationship
   name, on `TableMappingConfig`. Round-trips through YamlDotNet with no special handling.
3. **Foreign-column resolution helper**: not built — see the "Not built, on reflection" note above. Nothing
   in this phase's own scope, nor 187J/188J's planned `JOIN` rendering, ends up needing one.

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

## Retrospective

Built as designed, with two real deviations found along the way rather than assumed up front:

- **The foreign-column resolution helper was dropped.** The doc's own draft assumed 187J/188J's `JOIN`
  rendering would need to look up a relationship column's *type* the way `RequireColumn` looks up a
  primary-table column's. Reading what a `JOIN ... ON base.[Local] = r0.[Foreign]` clause and a `SELECT
  ... AS r0.[Col]` projection actually need — both are string column *names*, straight off
  `RelationshipConfig.JoinKeys` and `ColumnMapping.SourceColumn`, no type required — showed there is
  nothing to resolve. `RelationshipColumns` (the cache this phase does build) turned out to exist purely
  for 189J's mapping-editor picker, not for anything the read/write pipeline consults. Caught by tracing
  the actual consumer before writing a helper nothing would call, rather than building it speculatively.
- **`IChangeReader.ReadChangesAsync` got a required parameter, not an optional one**, despite briefly
  considering optional-with-a-default (this session's own `DriverRegistry.Register(..., hostReaders =
  null)` is a real, recent precedent for exactly that shape). Ruled out because `CancellationToken
  cancellationToken` — required, no default — already sits last in the parameter list; C# requires every
  optional parameter to follow every required one, so an optional `relationships` would have had to sit
  *after* `cancellationToken`, breaking this codebase's own convention of `CancellationToken` always being
  last. Went required instead, positioned with the other mapping-level parameters as originally planned,
  and fixed every call site (119 across 27 test files, plus `RunExecutor`/`PreviewService`/two hand-written
  `IChangeReader` test doubles) with a small paren-and-angle-bracket-aware Python script rather than by
  hand — one real bug in the first version (treated a comma inside `Dictionary<string, string>`'s own
  generic argument list as a top-level argument separator, corrupting one call site) was caught by reading
  the diff before building, reverted via `git checkout` on the affected files, and fixed by tracking `<`/`>`
  depth too before re-running.
- **Verified for real, not just compiled**: full `DbDataSync.Core.Tests` (289/289, 13 new), every driver
  project's own test suite against its real container (MsSql 259/261 + 2 pre-existing skips, Postgres
  118/132 — the 14 failures are `PgLogicalSlotTests`, confirmed via `SHOW wal_level` on the container itself
  to be a pre-existing `wal_level = replica` local container misconfiguration, nothing this phase's diff
  touches — MySQL 62/62, Oracle 58/58, JDBC 32/32, plus Generic/DuckDb/Descriptor/Loader/Scripting/State/
  Libraries/Verification all green), `DbDataSync.Api.Tests` (659/682, 23 Windows-only skips, including the
  two new `RelationshipMetadataRefreshTests` against a real MsSql database), `DbDataSync.TaskRunner.Tests`
  (80/80), and a frontend `tsc --noEmit` (clean, confirming zero frontend impact even though none was
  touched).
- **Not proven, on purpose**: no reader yet does anything with `relationships` beyond receiving it — that's
  187J (batch readers) and 188J (change readers), both still open in `todo/`.
