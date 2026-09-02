# DuckDB as a source: query-first, full/segmented load only, no watermark yet

**Status: resolved — ready for an implementation phase doc.**

## Scope, resolved

No incremental/watermark support this phase — explicitly deferred ("there are some additional design
considerations that will be necessary for proper change tracking"). No `ISegmentExpandingReader`
(auto-segment discovery) either — deferred as speculative until the basic reader is proven. What ships:
a raw-SQL, full-reload reader that can still participate in the *existing* manual segmenting-strategy
system (phase 58), a mapping-editor UI for writing and testing that SQL inline, and nothing else.

## What's there today, confirmed by reading the code

- **`ScriptedQueryReader` is not what this needs.** It's C#-script-mediated (Roslyn-compiled
  `ISourceQueryBuilder` implementations, `DataSync.Scripting`), not raw SQL text in config. The ask —
  edit and preview a query directly in the mapping's source tab — needs a narrower, genuinely raw-SQL
  reader kind.
- **`IDriver` mandates schema introspection** (`ListDatabasesAsync`/`ListTablesAsync`/`ListColumnsAsync`,
  non-optional). A query-first driver doesn't fail this — it just returns empty lists, satisfying the
  interface without lying about having tables to browse.
- **Segmenting a reload reader needs no special interface.** `BatchReloadReader` proves the pattern:
  `previousWatermark` is ignored outright and echoed back unchanged ("deliberately not incremental...
  a reload's entire purpose is to re-read rows an incremental pass has already seen"); segment info
  arrives through the same `options` dictionary every reader already gets
  (`SegmentSerializer.ReadOptional(options)`), as a `BatchReloadSegment` — `FullSegment` (unsegmented),
  `RangeSegment(Column, RangeMin, RangeMax)`, or `ListSegment(Column, Values)` are the only shapes a
  reader ever receives at runtime (`AutoSegment`/`CustomSegment` are markers resolved before reaching
  one). `RangeSegment`/`ListSegment` carry literal bindable strings — no column-type introspection
  needed to use them, only to *generate* a dialect-correct predicate automatically, which
  `BatchReloadReader` does via `SegmentScope`/`ITableCatalog` and this reader does not need to, because
  the predicate is the operator's own SQL.
- **No live result-set preview exists anywhere in this app.** `IStatementPreview` only ever returns SQL
  *text*, never executes it. The nearest precedent for "run a query, show sample rows" is
  `ScriptTestService`'s existing sample-row execution (built for testing scripted column transforms) —
  extend that, don't build parallel plumbing.
- **`CodeEditor`** (`src/DataSync.Web/src/components/CodeEditor.tsx`) is the existing Monaco wrapper,
  already used for SQL (read-only preview) and C# (script editing) — reuse it for a live-editable SQL
  box.
- **`ParameterType` is a closed, declared set** specifically so the SPA never needs to know what a
  parameter is *for* — the right way to get a real SQL editor onto this reader's query option is a new
  `ParameterType` value the generic parameter form renders as `CodeEditor` instead of a plain input, not
  a reader-kind special case in the SPA.
- **No DuckDB reference anywhere in the repo** — genuinely greenfield. DuckDB.NET (`DuckDB.NET.Data`) is
  the embedded ADO.NET provider to reference; DuckDB itself needs no server and, per the second
  deferral below, no persistent file either.

## Design

### Connection: as close to nothing as `IDriver` allows

No new `ConnectionConfig` fields. DuckDB is embeddable and can query external files/URLs directly via
its own SQL functions (`read_csv`, `read_parquet`, scanner extensions) without a persistent local
database — so the existing `ConnectionConfig.ConnectionString` field holds whatever a `DuckDBConnection`
accepts (`:memory:` is the expected default; a real file path works too, later, with zero code change,
since it's just a different string in a field that already exists). `ConnectionDriverType` gains
`DuckDb`. Schema introspection methods return empty lists rather than throwing.

### `DuckDbQueryReader` — full-reload only, segment-token substitution, no watermark

Implements `IChangeReader` and `IStatementPreview`. Not `ISegmentExpandingReader`.

- `previousWatermark` ignored and echoed back unchanged — the exact `BatchReloadReader` pattern, same
  reasoning: reads happen with the full weight of a reload, not an incremental cursor.
- The query itself is a reader option (`query`), declared with the new `ParameterType` so the SPA
  renders it as a real SQL editor rather than a text box.
- Segment handling: read `SegmentSerializer.ReadOptional(options)`. `null`/`FullSegment` → run the query
  as written. `RangeSegment`/`ListSegment` → substitute named tokens into the query text before
  executing (`{{segmentColumn}}`, `{{segmentMin}}`/`{{segmentMax}}` for a range,
  `{{segmentColumn}}`/`{{segmentValues}}` for a list) — the operator references these tokens in their
  own `WHERE` clause if and however they want segmenting to work; the reader does no predicate
  generation of its own. This is the literal reading of "up to the query writer to structure the results
  correctly," scoped down to segmenting only, now that watermark structuring is deferred.
- Every row yields as `ChangeOperation.Insert`, matching `BatchReloadReader`'s "a full scan only
  observes what exists; deletion is the reconciling writer's job."
- `DescribeAsync` (`IStatementPreview`) returns the query text itself, substituted for whatever segment
  the preview's context carries (or unsubstituted/example tokens shown as-is when unsegmented) — trivial
  here, since there's no statement to generate, only to show.

### Live preview: extend `ScriptTestService`, don't build a parallel path

A new execution path (or an extension of the existing sample-row one) that takes a DuckDB connection
(or the query text alone, if the connection is always `:memory:` for now) plus the current, possibly
unsaved query text from the editor, runs it capped at a small row limit, and returns column names plus
sample rows. This is what the mapping editor's source tab calls when an operator hits "preview" —
against the text currently in the editor, not necessarily what's saved in the mapping's config yet.

### UI: the source tab, not a separate screen

`MappingSide.tsx`'s source side, when the selected reader is DuckDB's query kind, replaces the
connection/database/schema/table pickers with: the `CodeEditor`-backed query field (bound to the `query`
option, editable), and a "Preview" action rendering a small result grid (columns + sample rows) inline,
using the new execution path above. No new page, no modal — the same tab.

## What this phase should not do

- Any watermark/incremental design — explicitly deferred, and don't half-build a hook for it (no
  `{{previousWatermark}}` token, no reserved result column, nothing) — this reader genuinely has no
  incremental concept yet.
- `ISegmentExpandingReader`/auto-segment discovery — deferred; only the manual
  `RangeSegment`/`ListSegment` shapes need handling, `AutoSegment` never reaches a runtime reader anyway.
- Real file-based or remote DuckDB connections as a distinct, configured mode — the connection string
  field already supports it structurally; making it a first-class, validated UI option is separate,
  later work.
- Column-mapping UI changes beyond what's needed to map the query's actual result columns — if the
  existing column-mapping step already works from "whatever columns a preview reports" rather than
  catalog introspection for other query-shaped readers, reuse it as-is; if it doesn't, that's the one
  piece of existing UI this phase may need to touch beyond the source tab itself.

## How to verify

- A test asserting `previousWatermark` is echoed back unchanged, never interpreted.
- A test asserting `RangeSegment`/`ListSegment` tokens substitute correctly into a query containing
  them, and that an unsegmented (`FullSegment`/null) read leaves the query text untouched.
- A test asserting the driver's introspection methods return empty lists without throwing.
- A test/story for the live preview path: editing unsaved query text and previewing reflects that text,
  not the last-saved config.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-089-duckdb-query-source.md`.
