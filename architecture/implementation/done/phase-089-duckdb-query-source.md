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

---

## Outcome

**Shipped.** A driver for an engine that is a library, a reader whose configuration is one statement,
and a source tab that runs it before anyone commits it.

### What was built

`DataSync.Drivers.DuckDb` on `DuckDB.NET.Data.Full` 1.5.5. `DuckDbDriver` is source-only — one
reader, no staging provider, no writer — and answers all three `IDriver` introspection methods with
empty lists. `ConnectionDriverType` gains `DuckDb`; no `ConnectionConfig` field was added.

`DuckDbQueryReader` implements `IChangeReader` and `IStatementPreview` and nothing else. It ignores
`previousWatermark` and echoes it back, projects no column mappings into the operator's SELECT list,
and yields every row as an `Insert`. `QuerySegmentTokens` is the whole of its segmenting: it
substitutes `{{segmentColumn}}` as a quoted identifier and `{{segmentMin}}`/`{{segmentMax}}`/
`{{segmentValues}}` as SQL literals, and returns an unsegmented query untouched.

`ParameterType.Sql` is the new member; `ParameterForm` renders it through the same `CodeEditor` every
other SQL in this app is written in. `ScriptTestService.PreviewQueryAsync` runs a query and returns
columns and rows, behind `POST /api/connections/{name}/query-preview`. `QuerySourcePanel` replaces the
source card's schema and table pickers with that editor and a Preview button, inline.

### Substituted as literals, not bound as parameters

The doc says "token substitution" and leaves the form open. Literals are the only shape that works
here, and the reason is that the reader does not know the statement's shape: an operator may put
`{{segmentColumn}}` in a `WHERE`, inside a `read_csv` path, in a `QUALIFY`, or nowhere. A bound
parameter has to be placed; a literal only has to be spelled. And DuckDB casts a string literal to the
column's own type on comparison — `WHERE id >= '2'` narrows an `INTEGER` column correctly, as does
`WHERE d >= '2024-01-01'` on a `DATE` — which is what lets this reader segment a typed column without
the catalog lookup `SegmentScope` needs. Both halves are escaped: quotes doubled in literals,
identifiers quoted through the dialect.

`AutoSegment`/`CustomSegment` throw rather than substituting nothing. Neither reaches a runtime reader,
but the failure if one ever did is silent — the unsegmented query running under a segment's name, in a
run history that says otherwise.

### Additions the doc did not ask for, and why each was necessary

**`DuckDbDialect`.** The doc scopes the reader to needing no dialect, which is true of the reader and
false of everything around it: `RunExecutor.ResolveDialect` builds the watermark key from the source
driver's dialect and throws for a driver that names none, and `ProvisioningService` translates the
*source's* native column types through one to render the target's DDL. Without it a DuckDB source
mapping would have failed on its first pass rather than at configuration time. Only four members are
abstract, so it was cheap; `UseDatabaseAsync` is a no-op rather than `PostgresDialect`'s validate-and-
throw, because a DuckDB connection can `ATTACH` several databases and the endpoint's database field is
a label a query-first source has no fact to put in.

**Connection-string normalization.** The doc expects `:memory:` as the default, and
`DuckDBConnection` rejects exactly that — a bare path throws "Format of the initialization string does
not conform to specification". But a bare path is what every DuckDB example takes and therefore what an
operator types, so `BuildConnectionString` reads a value with no `=` in it as the data source it plainly
is, and an absent one as `Data Source=:memory:`. The builder canonicalizes the key, which is why the
tests expect `data source=`.

**`DuckDB.NET.Data.Full`, not `DuckDB.NET.Data`.** The judgment call the brief flagged, and it had a
real answer: the bare package ships managed bindings only and expects a `libduckdb` on the host. CI is
a Linux container with no DuckDB, so `.Full` — which carries the native assets per RID and needs no
`RuntimeIdentifier` — is the only one that works. Verified by probe before a line of driver code was
written, and again by the driver's own tests, which open a real in-memory database on this Linux box.

### Deviations from the doc

**Connection and database stay on the source card; only schema and table are replaced.** The doc says
to replace "the connection/database/schema/table pickers". Connection cannot go: it is how an operator
picks *which* DuckDB, and the reader kind is only knowable once it is chosen. Database cannot go
either, because `EndpointResolution.Resolve` rejects at save time any mapping whose source database is
blank — relaxing that for one driver would be a special case in the one place inheritance is resolved
for every driver. So the two fields with no answer for a query source are the two that went, and the
dialect's `UseDatabaseAsync` is what makes the remaining label harmless.

**Editing the query seeds a reader override.** The query lives in the reader's options and there is
nowhere else for it to go, so typing into the source tab overrides the mapping's reader — the gesture
`MappingPipelineCard`'s natural-key field already makes. It is also right rather than merely
available: a query is what *this table* reads, and writing into the replication's reader from one
table's tab would change every other table inheriting it.

### Judgment calls

- **`ParameterType.Sql`**, named for the value like every other member, not `CodeEditor`, which would
  be the SPA's word for the control this enum exists to stop declarations knowing about.
- **The reader Kind is `DuckDbQuery`**, engine-prefixed like `MsSqlChangeTracking` — it is one
  engine's mechanism, not a strategy every driver could offer, which is what `GenericDriverKinds`'
  unprefixed names mean.
- **The preview endpoint hangs off the connection, not the mapping**, and takes the query in its body.
  The text being previewed may belong to a mapping that does not exist yet, and routing it through the
  mapping would make saved config the subject — the exact thing the phase is avoiding. It carries no
  `[Authorize(Policies.Viewer)]`, unlike every other endpoint on that controller, matching
  `POST /api/scripts/{name}/test`: it runs operator-typed SQL against a real system.
- **The row cap stops the reader rather than wrapping the statement in a `LIMIT`.** Rewriting SQL
  somebody else wrote means parsing it — theirs may already carry a `LIMIT`, an `ORDER BY` it depends
  on, a CTE, or several statements — and a preview that silently ran something else would defeat its
  own purpose. Stopping early costs nothing against a streaming reader.
- **Preview cells are stringified server-side, and a null stays null.** `ScriptTestService.Format`
  spells null as `NULL` and quotes strings, which is right for a one-line test case and wrong for a
  grid, where an empty string and a null have to look different without either being quoted.
- **The column-mapping step reuses nothing, because there was nothing to reuse.** The brief asked
  whether `ScriptedQueryReader` had solved mapping a query's result columns; it has not — it calls
  `catalog.GetColumnsAsync` on the source table like every other reader. So the preview's result
  columns are lifted into `TableMappingForm`'s draft and passed down as the source columns, and
  `ColumnMappingEditor` takes them as a prop instead of fetching them. That is one prop and one piece
  of state, no new UI, and the editor now distinguishes "not previewed yet" from "no columns" — which
  are different things to tell an operator.
- **The driver's tests are not tagged Integration.** Every other driver's end-to-end tests need a
  server somebody started, which is why that category exists. DuckDB is a library: an in-memory
  database costs milliseconds and no setup, so the reader, the driver and the preview endpoint are all
  asserted against a *real engine* in the ordinary suite.

### How it was verified

- `QuerySegmentTokenTests` (new, 12): range and list substitution, every occurrence of the column
  replaced rather than the first, quote-doubling on both literals and identifiers, an unsegmented read
  and a token-free query both left untouched, and both marker segments throwing with their own
  description in the message.
- `DuckDbQueryReaderTests` (new, 11, against a real in-memory DuckDB): the query's own result columns,
  every row an `Insert`, `previousWatermark` echoed back for both a value and an empty string and
  reported as `""` when absent, range and list segments narrowing the rows *the engine actually
  returns* — which is what proves the literal form compares correctly against a typed column — a
  missing `query` option naming itself, and both `DescribeAsync` forms.
- `DuckDbDriverTests` (new, 10): introspection empty and staying empty with real tables in front of
  it, connection-string normalization across five forms, the properties bag, the version probe, the
  absent write side, and the capability endpoint reporting no segmentation and no delete detection.
- `DuckDbQueryPreviewTests` (new, 10, over HTTP against a real DuckDB connection): the columns and
  rows, **previewing twice with different unsaved text against a connection with no saved mapping at
  all**, the visible cap, an empty result set as a grid rather than an error, a rejected query as a
  200 carrying the engine's message, the connection named in `source`, an empty query, a 404, and
  nulls surviving as nulls.
- `duckdb-query-source.spec.ts` (new, 4, Playwright, stubbed at the network boundary): the editor in
  place of the pickers with connection and database still read from the replication, **Preview sending
  the edited text and not the saved query** — asserted on the request body and on an echo column the
  stub returns, so the grid itself shows which query ran — the previewed columns reaching the
  column-mapping tab including one that exists in no catalog anywhere, and a rejected query showing
  its message. Screenshots `64-duckdb-query-source.png` and `65-duckdb-query-preview.png`.
- Full Playwright suite **57 passed** — the 53 that existed before, unaffected.
- Full suite: `Category!=Integration` **1171 passed, 18 failed**; `Category=Integration` **218 passed,
  0 failed**. `tsc -b`, the SPA build and `oxlint` clean on every file this phase touched.

### Pre-existing failures, confirmed as such

The same 18 phase 86 recorded, unchanged and none in a file this phase touched: nine
`AdminCertificateServiceWindowsTests`, `CertificateExpiryServiceWindowsTests`, two
`AdminConfigControllerTests` file-source tests, five Windows-only tests in
`DataSync.Certificates.Tests`, and `InviteCommandTests`. The baseline was taken before any change
here (1128 passed, the same 18 failed); the delta is exactly the 43 tests this phase adds.

### One flake, chased down rather than recorded

The first full Playwright run failed golden-path 21 — `metrics-card` not found — which cascaded
through the rest of the serial file. Stashing this phase's work and re-running gave 45/45, and
re-running with the work restored gave 57/57. It reproduced neither with the change nor without it,
and nothing here touches the replication Overview. Recorded as a flake rather than as a pass, because
the difference matters to whoever sees it next.

### What this phase did not do

No watermark or incremental concept, and no partial hook for one — no `{{previousWatermark}}` token,
no reserved result column, nothing. No `ISegmentExpandingReader`. No writer, staging provider or
provisioner for DuckDB, so it cannot be a target. No first-class file-based or remote connection mode:
the connection string field takes a path today and a phase that makes it a validated, named choice is
separate work. `DuckDbDialect.RenderColumnType` is implemented in full anyway, so the first phase to
add a writer finds a mapping rather than a gap.
