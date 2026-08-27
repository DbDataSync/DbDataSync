# C# scripting — the extension points

**Status: resolved 2026-08-27 — see Outcome at the end.** The contracts an operator implements, and where each one is invoked.
The host that compiles and resolves them is `csharp-scripting-host.md`.

Every contract below lives in `DataSync.Scripting.Abstractions` and is referenced by the driver
projects, so a driver can consume a script without depending on Roslyn.

## The rule these all follow

**A script returns a description of what to do; the host does it.** A query script returns command
text *and a parameter list*, never a finished string with values in it. A transform returns values,
never a `DbCommand`. This is not ceremony:

- it keeps parameter binding — and therefore injection safety — in the host, where phases 9 and 17
  already put it, even when the SQL itself came from an operator
- it keeps the script testable, because its output is data you can assert on
- it means a script never holds a connection, a transaction or a reader, and so cannot leak one

The one place this is relaxed is the metadata provider, which genuinely needs the connection.

---

## 1. Row transform — `rowTransform`

The seam is exact and already clean. `RunExecutor` currently does:

```csharp
var read = await reader.ReadChangesAsync(...);
var staged = await stagingProvider.StageAsync(targetConnection, target, read.Rows, ...);
```

`read.Rows` is an `IAsyncEnumerable<ChangeRow>`. A transform wraps that stream. Nothing else moves.

```csharp
public interface IRowTransform
{
    /// Called once per pass, before any row. Lets a transform that changes the row's shape say so.
    ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext context);

    /// Return null to drop the row.
    ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext context, CancellationToken ct);
}
```

**`DeclareSchema` is the part that is easy to leave out and expensive to add later.** A transform that
adds a column changes what the staging provider has to create a table for, and staging reads the schema
from the first row it sees. If the shape can change, it has to be knowable before the first row —
otherwise the staging table is built from the untransformed shape and every added column is silently
dropped. Declaring it once per pass also means the cost is paid once, not per row.

Returning null to drop a row makes filtering fall out for free, and filtering is the single most likely
thing anyone will want. It does mean `RowsRead` and the staged count diverge — which is correct and
should be logged, not hidden.

`RowTransformContext` carries the resolved `SourceTableRef` and `TableRef`, the column mappings, the
binding's parameters, and a logger that writes into the run's log stream (so an operator debugging a
transform sees its output in the Runs tab, where they are already looking).

### Per-column, too — `columnExpression`

Per-row is more powerful; per-column is more ergonomic and much cheaper, because the engine can skip
columns nothing touches.

```csharp
public interface IColumnExpression
{
    object? Evaluate(object? value, ColumnExpressionContext context);
}
```

**Measure before choosing.** A delegate call per cell over millions of rows is precisely the class of
cost phase 14 measured for the row representation, and `tools/benchmarks` already exists to answer it.
The stated house rule applies: the only correct answer is to measure the impact.

### …and a third place a transform can happen — settled 2026-08-27

The two above both run **in this process**. There is a third option that runs in the *source engine*,
and it is the one the config model has always had a field for:

```csharp
public sealed class ColumnMapping
{
    public required string SourceColumn { get; set; }
    public required string TargetColumn { get; set; }
    public string? Transform { get; set; }   // declared in phase 1, read by nothing until phase 22
}
```

`Transform` is **a SQL expression in the source dialect**, rendered into the reader's SELECT list and
evaluated by the source. `UPPER("region")`, `CAST("amount" AS numeric(18,2))`,
`COALESCE("code", 'UNKNOWN')`.

It is **not** a script and does not become one. It is kept, implemented, and supported on its own —
`implementation/todo/phase-022-source-sql-column-transforms.md`.

The three are genuinely different tools and all three are wanted:

| | runs on | cost | can do |
| --- | --- | --- | --- |
| `Transform` (SQL) | the **source engine** | free to us; the source does the work, and it moves less data when the expression narrows | anything that engine's SQL can express |
| `columnExpression` (C#) | this process, per cell | a delegate call per cell | anything C# can express, on one value |
| `rowTransform` (C#) | this process, per row | a delegate call per row | anything C# can express, across the whole row, including dropping it |

**And a script may generate the first.** That is the fourth combination and it is the interesting one:
a C# script that emits a source-dialect SQL expression gets the flexibility of code at *configuration*
time and the cost profile of SQL at *run* time. A script that computes a per-engine cast, or builds a
`CASE` from a lookup table, writes it once and the source evaluates it a billion times for free.

So `columnExpression` has two shapes, and the manifest says which:

```csharp
/// Emits SQL evaluated by the source engine. Generated once per pass.
public interface ISqlColumnExpression
{
    /// Return null to emit the column unchanged.
    string? RenderSql(ColumnMetadata column, SqlDialect dialect, ColumnExpressionContext context);
}

/// Transforms the value in this process, per cell.
public interface IValueColumnExpression
{
    object? Evaluate(object? value, ColumnExpressionContext context);
}
```

`ISqlColumnExpression` and `ISelectListContribution` below are the same idea reached from two
directions; they should be **one interface**, not two. The name that survives is
`ISqlColumnExpression`, because "select list contribution" describes the mechanism and this describes
the intent.

---

## 2. Metadata provider — `metadataProvider`

Replaces or supplements what a driver's catalog reports. The motivating cases:

- ODBC and JDBC, where the catalog behind the driver is not knowable generically
- a source whose real schema lives in an application metadata table rather than in `information_schema`
- a source where the operator needs to hide, rename or synthesise columns before anyone maps them

```csharp
public interface IMetadataProvider
{
    Task<IReadOnlyList<string>> ListDatabasesAsync(MetadataContext ctx, CancellationToken ct);
    Task<IReadOnlyList<TableMetadata>> ListTablesAsync(MetadataContext ctx, string database, CancellationToken ct);
    Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(MetadataContext ctx, string database, string schema, string table, CancellationToken ct);
}
```

`MetadataContext` carries an **open `DbConnection`** and the `SqlDialect`. This is the one contract
that hands a script a live connection, because there is no way to describe "ask the catalog" as data.

It maps onto two existing surfaces at once: `IDriver`'s three metadata methods (what the SPA's pickers
call) and `ITableCatalog` (what the generic pipeline calls to build a staging table and resolve a
segment column). **Both must be satisfied by one script**, or the pickers and the pipeline can disagree
about what columns a table has — which would surface as a staging table missing a column the operator
can see in the UI.

This is also the slot that runs **inside the API**, which is the isolation problem the host doc leaves
open. It is worth building this slot second precisely because it forces that question early rather than
after four other slots have shipped.

---

## 3. Source query — three levels, deliberately

The brief asks for both "generating column expressions" and "completely rebuilding the entire query
from metadata". Those are different amounts of ownership and should be different contracts, because a
script that only wants to add a computed column should not have to take responsibility for the WHERE
clause, the segment predicate and the watermark.

### 3a. `columnExpression` in its SQL shape — the smallest

Already described above as `ISqlColumnExpression`: contributes one entry to the SELECT list for a
column, and the host still builds the statement. Listed here too because it is the smallest rung of the
source-query ladder as well as the largest rung of the transform ladder — it is the same thing seen
from both ends.

Use: `CAST("amount" AS numeric(18,2))`, `COALESCE("region", 'UNKNOWN')`, a spatial column rendered as
WKT because the target cannot hold the native type.

Cheap, composable, and it cannot break the parts of the statement that make a segmented reload correct.

### 3b. `sourceQueryBuilder` — the whole statement

```csharp
public interface ISourceQueryBuilder
{
    SourceQuery BuildChangeQuery(SourceQueryContext context);
    SourceQuery? BuildWatermarkQuery(SourceQueryContext context);   // null: no watermark of its own
}

public sealed record SourceQuery(string CommandText, IReadOnlyList<ScriptParameter> Parameters);
public sealed record ScriptParameter(string Name, object? Value, string? NativeType = null);
```

`SourceQueryContext` carries everything the generic readers already compute: the resolved
`SourceTableRef`, the column metadata, the `SqlDialect`, the previous watermark, and the segment — as
both the `BatchReloadSegment` and the already-rendered predicate and parameters, so a script can either
use the rendering or ignore it and do its own.

**Two queries, not one**, mirroring `WatermarkStatement`'s existing `BuildMaxWatermark` /
`BuildRead` pair. A reader has to answer "what changed" and "what is the new position" and those are
usually separate statements. A builder that has no position of its own returns null and the host echoes
the previous watermark back, exactly as `BatchReloadReader` does today.

`ScriptParameter.NativeType` is optional and lets a script ask for the driver's typed binding — the
same `ISegmentValueBinder` that phase 17 introduced, for the reason phase 9 recorded: an untyped bound
makes the engine convert on the column side and kills the index seek.

### 3c. `sourceQueryTransform` — string surgery, offered reluctantly

```csharp
public interface ISourceQueryTransform
{
    SourceQuery Transform(SourceQuery generated, SourceQueryContext context);
}
```

Receives the generated statement and returns a modified one. This is the pragmatic escape hatch — "add
a hint", "wrap it in a CTE", "append `FOR UPDATE SKIP LOCKED`" — and it is also the one most likely to
break silently when the generator's output changes underneath it.

Worth shipping because the alternative is that anyone with a small need has to reimplement the whole
builder. Worth documenting as brittle, and worth putting last of the three.

---

## 4. Target statement — `targetStatement`

Explicitly "eventually" in the brief, and it should stay last. A wrong reader reads the wrong rows; a
wrong writer destroys the target.

```csharp
public interface ITargetStatementBuilder
{
    /// Statements run in order, inside the writer's transaction.
    IReadOnlyList<SourceQuery> BuildApply(TargetStatementContext context);
}
```

`TargetStatementContext` carries the `TargetShape` (mapped columns, primary key, whether a generated
column is being written explicitly), the staging location, the column mappings and the segment scope.

Three things this has to get right, all of which the built-in writers already handle and a script
author will not think about:

- **Reconciliation is declared, not inferred.** `SupportsReconciliation` comes from the manifest. The
  UI offers a writer for a backfill based on that flag; a script that claims it and does not do it
  produces a reload that silently fails to converge.
- **The transaction is the host's.** The script returns statements; the host runs them in one
  transaction and rolls back on failure. A script must not be able to commit.
- **Generated-column override** is a dialect concern (`SET IDENTITY_INSERT` versus
  `OVERRIDING SYSTEM VALUE`, phase 20's finding). The context should expose it as a rendered fragment
  rather than leaving each script to know both spellings.

---

## 5. Change query — `changeQuery`

Its own document, because it is the answer to the ODBC/JDBC change-tracking problem and not just
another slot: `script-generated-change-queries.md`.

---

## What is deliberately not scriptable

- **Staging.** The staging provider owns batching, parameter limits and cleanup — all of which phase 18
  got wrong once already and now has tests for. A script here would re-open a solved problem.
- **Scheduling, the work queue, run locking.** These are correctness machinery, not policy.
- **Connection creation.** A script that builds a connection string is a script that can exfiltrate a
  credential to any host, and unlike the rest of this it has no legitimate use we have been asked for.

## Open questions

- **Ordering of the three transform stages.** SQL runs at the source, then `columnExpression`, then
  `rowTransform` — that ordering is forced by where each one lives, but a mapping that uses two of them
  on the same column needs the UI to make it obvious which runs first.
- **Does a row transform see deletes?** A `ChangeOperation.Delete` row carries only its key. Passing it
  through a transform that expects values is a trap. Options: pass it and document; skip transforms for
  deletes; or let the manifest declare which operations the transform wants. The third is probably
  right and costs a manifest field.
- **Ordering within a list slot.** Declared order in the binding list, presumably — but two transforms
  that both rename a column are order-dependent in a way nothing will warn about.
- **Where preview data comes from.** The Test action needs sample rows. Reading N rows from the real
  source is the honest sample and also a real query against production. Probably fine with an explicit
  row cap; worth deciding rather than defaulting.

---

# Outcome — resolved 2026-08-27

Agreed. The substantive change from the original proposal is that **a transform can happen in three
places, not two**, and the config field that has been dangling since phase 1 is the third:

- `ColumnMapping.Transform` — SQL in the source dialect, evaluated by the source. **Kept and
  implemented**, independent of scripting, in phase 22.
- `columnExpression` — a script, in either of two shapes: `ISqlColumnExpression` generating the above,
  or `IValueColumnExpression` transforming the value in process.
- `rowTransform` — a script, per row, between the reader and staging.

Phasing is recorded in `csharp-scripting-host.md`'s Outcome. Phase 22 is the prerequisite for all of
it, because a source-dialect transform requires the reader to build an explicit projection rather than
`SELECT *` — and once it does, a generated expression has somewhere to go.
