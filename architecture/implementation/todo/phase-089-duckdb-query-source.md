# Phase 89 — DuckDB query source: full/segmented load, no watermark, inline editor and preview

**Status**: Not started.
**Plan reference**: `architecture/planning/done/duckdb-query-source.md`

## The gap

No DuckDB support exists anywhere in this repo. This phase adds a query-first source: an operator
writes raw SQL directly in the mapping's source tab (not a C# script, not a table picker), previews its
result set inline, and the reader runs it as a full reload — optionally scoped by the existing manual
segmenting-strategy system via token substitution. No watermark/incremental concept and no auto-segment
discovery this phase — both explicitly deferred.

## What to build

### New project: `DataSync.Drivers.DuckDb`

References `DuckDB.NET.Data` (the embedded ADO.NET provider — no server, no persistent file required).
`ConnectionDriverType` gains `DuckDb`. No new `ConnectionConfig` fields — its existing `ConnectionString`
holds whatever `DuckDBConnection` accepts (`:memory:` as the expected default; a real file path works
identically, later, with no code change).

### `DuckDbDriver : IDriver`

`CreateConnection` opens a `DuckDBConnection`. `ListDatabasesAsync`/`ListTablesAsync`/`ListColumnsAsync`
return empty lists — satisfies `IDriver`'s mandatory introspection without pretending there's a fixed
schema to browse.

### `DuckDbQueryReader : IChangeReader, IStatementPreview`

- `previousWatermark` ignored, echoed back unchanged — mirror `BatchReloadReader.cs`'s exact pattern and
  its doc comment's reasoning.
- Reader option `query`: the raw SQL text, declared with the new `ParameterType` (below) so the SPA
  renders a real editor.
- `SegmentSerializer.ReadOptional(options)` → `null`/`FullSegment`: run as written.
  `RangeSegment(Column, RangeMin, RangeMax)`: substitute `{{segmentColumn}}`/`{{segmentMin}}`/
  `{{segmentMax}}` tokens in the query text. `ListSegment(Column, Values)`: substitute
  `{{segmentColumn}}`/`{{segmentValues}}`. No predicate generation — the operator's own `WHERE` clause
  references these tokens however they choose.
- Every row yields `ChangeOperation.Insert` (matches `BatchReloadReader`: a scan only observes what
  exists; a reconciling writer handles removal).
- `DescribeAsync`: the query text, token-substituted for whatever segment the preview context carries
  (or shown with tokens unsubstituted when there's no segment).
- No `ISegmentExpandingReader` — `AutoSegment`/`CustomSegment` never reach a runtime reader (resolved
  before dispatch, per `BatchReloadSegment`'s own doc comment), so this reader only ever needs to handle
  the three shapes above.

### New `ParameterType`

A value the generic parameter form renders via `CodeEditor` (`language: 'sql'`) instead of a plain text
input — check `ParameterDescriptor.cs`'s existing enum and the SPA's parameter-form renderer for where
each `ParameterType` maps to a control, and add this alongside them. Reused by the `query` option
declaration above.

### Live preview: extend `ScriptTestService`

A new execution path (or an addition to the existing one) taking the current, possibly-unsaved query
text from the editor plus a DuckDB connection, running it capped at a small row limit, returning column
names and sample rows. Called from the mapping editor's source tab, against whatever's in the editor
right now — not necessarily the mapping's last-saved config.

### SPA: `MappingSide.tsx`'s source tab

When the selected reader is DuckDB's query kind: replace the connection/database/schema/table pickers
with the `CodeEditor`-backed `query` field and a "Preview" action rendering a small result grid (columns
+ sample rows) from the new execution path, inline in the same tab — no new page, no modal.

## What this phase should not do

- Any watermark/incremental hook, even a partial one — no reserved token, no reserved result column.
- `ISegmentExpandingReader`/auto-segment discovery.
- A first-class, validated file-based/remote-DuckDB connection UI — the field already supports it
  structurally; making it a distinct configured mode is separate work.
- Rework the column-mapping step beyond what's needed to map a query's actual result columns — reuse
  whatever mechanism already maps columns for other query-shaped readers if one exists; only build new
  UI here if nothing does.

## How to verify

- A test asserting `previousWatermark` passes through unchanged.
- A test asserting `RangeSegment`/`ListSegment` token substitution produces the expected query text, and
  an unsegmented read leaves the query untouched.
- A test asserting `ListDatabasesAsync`/`ListTablesAsync`/`ListColumnsAsync` return empty lists without
  throwing.
- A test/story confirming the preview path reflects unsaved editor text, not the last-saved mapping
  config.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
