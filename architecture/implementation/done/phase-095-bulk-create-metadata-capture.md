# Phase 95 — Bulk-created mappings arrive runnable: captured metadata and auto-mapped columns

**Status**: Complete.
**Plan reference**: `architecture/planning/done/bulk-created-mappings-have-no-cached-metadata.md`

## The gap this closed

`TableMappingsController.BulkCreate` built each mapping with `NewMapping(...)` — name, one source spec,
one mirrored target spec — and handed it straight to `configRepository.SaveTableMapping`. That path
never went near `MappingMetadataCapture.Apply`, and it would have had nothing to give it if it had: the
bulk screen (`MappingsOverview.tsx`) fetches *table* names via `useTables` and never fetches columns, so
unlike the mapping editor there was no already-loaded column list waiting to be persisted.

Every bulk-created mapping therefore landed with `SourceColumns` and `TargetColumns` empty,
`ColumnsCapturedUtc` null, **and `ColumnMappings` empty** — two independent reasons it could not run:

- Phase 91 made readers and writers cache-only with no live fallback; an empty cache throws
  `MetadataNotCachedException`.
- `MsSqlStagingTableProvider.StageAsync` throws `"ColumnMappings must be specified to stage changes."`
  on an empty list (`MsSqlStagingTableProvider.cs:41`), and `BatchInsertStagingProvider` carries the
  identical check (`:52`). Readers tolerate empty — `SourceProjection.Render` returns `*`, deliberately
  — so the failure landed on the write side, after a read had already happened.

Forty tables meant forty mappings that could not run until somebody opened each one and pressed
Refresh, then auto-mapped its columns by hand — putting back, one screen at a time, the gesture the
bulk screen exists to remove. Fixing only the cache would have moved the failure from one exception to
another, which is why this phase covered both.

## What was built

### 1. `MappingColumnReader` — one introspection path

`MappingMetadataService`'s private `ReadAsync`/`Unreadable`/`SideRead` lifted into
`src/DbDataSync.Api/Services/MappingColumnReader.cs`, registered as a singleton and injected into both
`MappingMetadataService` and `TableMappingsController`. `RefreshAsync` is otherwise untouched and its
behaviour is unchanged.

Two things about the extraction turned out to matter more than the move itself — see "Real bugs found"
below: the catch had to widen, and `SideRead` grew `IsMissingTable`/`Shape`.

### 2. Capture inside the bulk loop

`BulkCreate` now loads the replication once up front (it needs it to resolve each side's endpoint —
a bulk-created mapping states only its tables), then per table, before the existing save:

```csharp
var mapping = NewMapping(name, table);
var source = await columnReader.ReadAsync(EndpointResolution.ResolveSource(task, mapping.Sources[0]), ct);
var target = await columnReader.ReadAsync(EndpointResolution.ResolveTarget(task, mapping.Targets[0]), ct);
if (source.Shape is not null) mapping.SourceColumns = source.Shape;
if (target.Shape is not null) mapping.TargetColumns = target.Shape;
if (either) mapping.ColumnsCapturedUtc = DateTime.UtcNow;
mapping.ColumnMappings = ColumnAutoMap.Between(source.Shape, target.Shape);
configRepository.SaveTableMapping(...);   // still one save, one commit
```

Sequential, one table at a time, as planned: no bounded concurrency and no new batched
`IColumnCatalog` method. Forty tables is eighty catalog calls on an endpoint that previously made none,
accepted in exchange for keeping the loop's "names the table it failed on, reports everything created
before it" contract exactly as it stood.

### 3. `ColumnAutoMap` — the editor's rule, server-side

Pairs by name, ordinally, 1:1, `Transform` and `TargetType` null (the type is inferred through the
canonical system on every pass — writing an inference into config freezes today's answer) and `Renames`
empty. Target read: `source ∩ target`. Target not available: every source column — not a fallback but
the same rule, since `ColumnMappingEditor` already treats a not-yet-created target's columns *as* the
source's, precisely because `ProvisioningService` builds its `CREATE TABLE` from the column mappings.
Source not available: nothing.

The rule exists twice, in two languages. `ColumnMappingEditor.autoMap` and `ColumnAutoMap` now point at
each other in comments, and `ColumnAutoMapTests` writes out each case the editor answers as the same
question.

### 4. Reporting

- `BulkCreateResult` gained `Notes` — `BulkCreateNote(Mapping, Side, Reason)`, one per side that could
  not be fully captured, added only after that mapping's save succeeds. Empty in the ordinary case.
- `BulkCreateProgress` gained `Stage` (`"reading"` / `"created"`), so a screen that now waits on two
  catalog reads per table shows what it is waiting on. A closed set of strings rather than an enum:
  these go over SignalR, whose protocol has its own serializer configuration separate from MVC's
  `JsonStringEnumConverter`, so an enum would be a number on the wire.
- `MappingsOverview.tsx` renders the stage in the button and a "Created, but not fully captured" card,
  **and suppresses its navigate-to-the-first-mapping when there is anything to report** — navigating
  away is exactly how an operator would never see it.

## Real bugs and surprises found while building

- **A missing table does not throw — it returns an empty list.** `MappingMetadataService`'s doc
  comment, and `FakeColumnCatalog.Missing`, both model "table not in the catalog" as an
  `InvalidOperationException`. The real drivers do not: `MsSqlSchemaQueries.GetColumnsAsync` selects
  from the catalog views and returns *no rows*. Nothing in the suite covered the real path, and the
  first cut of `ColumnAutoMap` would have mapped **zero columns for every not-yet-provisioned target** —
  i.e. the common case, silently. `SideRead.IsMissingTable` now reads an empty-but-successful answer as
  "the table is not there", which is sound: no engine has a zero-column table, since `CREATE TABLE`
  requires at least one and dropping the last is refused. `Shape` folds "could not read" and "not
  there" together for callers that wanted a shape.
- **The catch had to widen from `InvalidOperationException` to everything except cancellation.** An
  unconfigured connection throws `FileNotFoundException` out of `ConfigRepository.LoadConnection`, and
  `BulkCreate` catches `FileNotFoundException` to mean "replication not found" — so one bad connection
  name would have turned a forty-table batch into a bogus 404. A login refused or a host down is a
  `DbException`, a bound `metadataProvider` script that throws is a `ScriptExecutionException`, and a
  dialectless driver is an `InvalidOperationException`; the set is open, and enumerating it means the
  next kind nobody thought of takes down a batch. This also makes `RefreshAsync` behave the way its own
  doc comment always claimed ("a source that is unreachable this minute is not a source with no
  columns") — previously an unreachable host escaped as a 500.
- **Notes have to be collected after the save, not before.** First cut added them as capture returned;
  a mapping whose save was then rejected by `ConfigValidationException` would have been named in a
  result that says it was not created.

## How it was verified

- `ColumnAutoMapTests` — 10 cases pinning the rule against the editor's: identical tables, a target
  column the source lacks, a source column the target lacks, a target that is not there, a target whose
  catalog answered empty, no source at all, case sensitivity, target ordering, and that nothing beyond
  the names is invented.
- `BulkMappingCaptureTests` — 8 endpoint tests behind `CatalogApiFactory`, which swaps only
  `IColumnCatalog` for `FakeColumnCatalog` and leaves config, git and endpoint inheritance real:
  both sides captured and mapped; **commit count unchanged at one per mapping**; a target that does not
  exist mapping every source column and saying why; a narrower target mapping only the intersection; an
  unreadable source still creating the mapping and not stopping the batch; nothing invented from the
  target alone; each side asked exactly once; a skipped table not introspected at all.
- `BulkCreateRunIntegrationTests` (`Category=Integration`, real SQL Server) — **the point of the
  phase**: two databases, one table name, mappings created the way the screen creates them, and
  **no `refresh-metadata` call and no hand-written column mappings anywhere in the file**. One test runs
  a pass against an existing target; the other lets provisioning create the target and asserts the
  table it created has the columns auto-map described. Every other integration test in the project has
  to call `refresh-metadata` first — see the note in `RunLifecycleIntegrationTests` — and this one's
  refusal to is the assertion.
- Playwright test 30 extended: a bulk-created mapping now has its `columnMappings`, `sourceColumns` and
  `columnsCapturedUtc`, and the skip-path response asserts `notes: []`.
- Full suite green — `Category!=Integration` and `Category=Integration` — `tsc -b` and the SPA build
  clean. Three unrelated failures on this machine (`SecretCommandTests`,
  `InviteCommandTests`, `ChangePollingGateTests.CdcAndChangeTrackingInOneDatabase_...`) were confirmed
  pre-existing on a clean checkout of `main` before this work started.

## What was deliberately not built

- **No backfill.** Mappings bulk-created before this phase keep their empty caches and empty column
  mappings until an operator refreshes and auto-maps them, per phase 90's no-silent-migration rule.
- **No provisioning from `BulkCreate`.** Creating target tables so both sides could be captured would
  turn a config-only endpoint into one that runs DDL. Phase 94 already covers that ground at run time,
  and the integration test above proves the two compose.
- **No new `IColumnCatalog` method** and no concurrency in the bulk loop.
- **No change to `MappingMetadataCapture`** — the `PUT` path's client-supplied-cache reconciliation is
  untouched.
- **No new auto-mapping rule.** The editor's is reused; if it is wrong it is wrong in both places.

## Loose ends for a later phase

- **`RefreshAsync` still empties a cache when the table has gone.** Because a missing table reads as an
  empty list rather than as unavailable, refreshing a mapping whose target has been dropped stores zero
  columns and stamps the capture time, rather than keeping the picture and saying the table is not
  there. That is pre-existing behaviour and out of scope here, but `SideRead.IsMissingTable` now exists
  to fix it with a one-line change when somebody decides it should be fixed.
- **`TableMappingConfig.SourceColumns`' doc comment still says "Nothing reads this yet"**, which phase
  91 made untrue. Not touched here.
- **`phase-094-...`'s doc still says `**Status**: Not started`** although it is in `done/` and its code
  is in `RunExecutor`. Also not touched here.
