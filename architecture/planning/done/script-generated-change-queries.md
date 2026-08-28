# Script-generated source change-tracking queries

**Status: resolved 2026-08-27 — see Outcome at the end.** The intersection of `csharp-scripting-host.md` and
`change-tracking-strategies.md`. Asked for as "for any of the drivers, but especially ODBC and JDBC,
I'd also like to be able to use C# scripting to generate source change tracking queries".

## Why this is the most valuable single item in either set

The strategies doc establishes that six of the seven change-tracking mechanisms in scope are, at read
time, a `SELECT` returning rows with an operation and a position. If that is true, then **a reader that
gets its query from a script can consume any of them** — including ones we will never write a driver
for.

It is not a fallback for the engines we have not got to. It is the same mechanism with the SQL supplied
by the operator:

- Reaching PostgreSQL through JDBC? `pg_logical_slot_peek_changes` is still just SQL.
- A legacy source with a hand-rolled audit table that predates this tool by fifteen years? That is a
  `SELECT` with an operation column.
- Informix, DB2, Sybase, Teradata, Snowflake streams, an AS/400 journal exposed through ODBC? All
  reachable, none of them worth a driver.
- A vendor application whose supported change feed is a stored procedure? Still a command returning
  rows.

That is a large surface for one reader Kind, and it is reachable before any of the per-engine CDC work
lands.

## The reader

One generic, unprefixed Kind — `ScriptedChangeQuery` — following the naming rule settled in
`additional-database-drivers.md`: bare for generic, prefixed for engine-specific. It takes a
`SqlDialect` and an `ITableCatalog` like every other generic reader, plus the resolved script binding.

```csharp
public interface IChangeQueryBuilder
{
    /// Fixes the end of the window. Null when the mechanism has no position of its own.
    SourceQuery? BuildEndPositionQuery(ChangeQueryContext context);

    /// The changes from the stored position up to the end fixed above.
    SourceQuery BuildChangeQuery(ChangeQueryContext context);

    /// How to read the result set back.
    ChangeQueryShape DescribeResult(ChangeQueryContext context);
}
```

`SourceQuery` is the same `(CommandText, Parameters)` record the source-query slot uses — a script
returns text plus a typed parameter list, never a finished string with values in it. That keeps
parameter binding in the host even when the SQL came from an operator, which is the rule the extension
points doc sets out and the reason it is worth having.

### Describing the result is the part that makes it general

```csharp
public sealed record ChangeQueryShape(
    string? OperationColumn,
    IReadOnlyDictionary<string, ChangeOperation> OperationValues,
    ChangeOperation DefaultOperation,
    string? PositionColumn,
    IReadOnlyList<string> ExcludeColumns);
```

- **`OperationColumn` absent** → every row is an insert. That is watermark semantics, and it means the
  existing `Watermark` reader is a degenerate case of this one rather than a separate idea.
- **`OperationValues`** maps whatever the source calls things onto `ChangeOperation`. `'I'/'U'/'D'` for
  a shadow table; `1/2/4` for SQL Server CDC; `'INSERT'/'UPDATE'/'DELETE'` for Oracle LogMiner;
  `'I'/'UO'/'UN'/'D'` for older Oracle CDC. Configuration, not code.
- **`PositionColumn`** names the column carrying the new watermark, for mechanisms that report it per
  row (a shadow table's `seq`, a Postgres `lsn`) rather than through a separate end-position query.
- **`ExcludeColumns`** drops the mechanism's own bookkeeping columns from the `ChangeSchema` before it
  reaches staging, so `__$operation` and `seq` do not turn up as columns nobody mapped.

The reader itself then does what the generic readers already do: build a `ChangeSchema` from the result
set, read values positionally, and yield `ChangeRow`s. No new pipeline.

## Capabilities have to be declared

`DetectsDeletes` cannot be inferred — a script either surfaces deletes or it does not, and only its
author knows. It comes from the manifest, exactly as the host doc describes, and flows through
`ReaderCapability` into the SPA's picker like every other driver's.

Getting this wrong is not cosmetic. A script that claims deletes and does not surface them produces a
target that quietly accumulates rows the source removed — and the operator, seeing the UI say deletes
are covered, has no reason to look.

`ISegmentExpandingReader` should **not** be implemented. A log position has no answer to "bucket 3 of
4". If a scripted query genuinely does want segmenting, the segment predicate is already in
`ChangeQueryContext` for the script to use — but expansion stays the reload readers' job.

## Position handling comes free, and so does its failure

The bounded window, the watermark-on-success-only rule, and `PositionExpiredException` are all in the
host and the reader, not in the script. A script author supplies SQL; they do not have to understand
the run model.

The one thing a script may need to participate in is **acknowledgement** — the post-write hook that
Postgres's `pg_replication_slot_advance` and a MySQL shadow table's pruning both need. That is the same
open contract question in three documents now (`-postgres`, `-mysql`, here), which is a strong signal it
should be answered once, as an opt-in `IPositionAcknowledging` on the reader in the same shape as
`ISegmentExpandingReader` and `IConnectionTester`.

For a script, that would be a fourth optional method — `BuildAcknowledgeQuery` — returning a statement
the host runs after a successful write, or null.

## ODBC and JDBC specifically

These are *meta*-drivers: the engine behind them is not knowable at design time, so no native
change-tracking reader is possible in general. That is not a gap to apologise for, it is a fact about
what an ODBC DSN is.

What they get instead:

- `ScriptedChangeQuery` as the primary answer, with the operator supplying the engine's own SQL
- the generic `Watermark` reader, which already works
- a scripted metadata provider (`csharp-script-extension-points.md`), because JDBC's `DatabaseMetaData`
  and ODBC's catalog functions do not reach through the generic `information_schema` path either

Two dialect notes that matter here more than anywhere else:

- **Positional parameters.** ODBC and JDBC both bind `?` positionally rather than by name. `SqlDialect`
  models named placeholders (`ParameterReference` / `ParameterName`, phase 17). A positional dialect
  either renders `?` from both and relies on ordering, or `SqlDialect` grows a positional mode. The
  first works if — and only if — parameter order is preserved end to end, which is a real constraint to
  impose on `SourceQuery` rather than to discover later.
- **There is no dialect to infer.** An ODBC connection to Postgres and one to DB2 quote identifiers
  differently and neither is knowable from the DSN. The dialect has to become configuration on the
  connection, which is a driver-phase question (`additional-database-drivers.md` phases 25–26) that
  this work depends on and does not answer.

## Testing a change query before it runs

More important here than for any other script slot, because a wrong change query is not a crash — it is
a target that silently disagrees with its source.

The Test action from the host doc should, for this slot, run the query against a real connection with a
supplied position and show: the rows, the operations it decoded them to, the position it would store,
and the `ChangeSchema` it derived. That is enough to catch the three common mistakes — an unmapped
operation value, a bookkeeping column leaking into the schema, and an off-by-one on the position bound
that re-reads or skips a row.

The `sys.fn_cdc_increment_lsn` detail in `change-tracking-mssql-cdc.md` is exactly that third mistake,
in a mechanism we control. A script author will hit it more often, not less.

## Sequencing

This should come **after** at least two real change-tracking readers exist — the strategies doc suggests
SQL Server CDC and Postgres. Building the scripted shape first would mean guessing at the contract;
building it third means generalising from two working implementations that already disagree in
interesting ways (one has an end-position query and a separate acknowledgement, the other reports its
position per row).

It should come **before** the ODBC and JDBC drivers, so that when those land they have something to
offer beyond watermark mode on day one.

## Open questions

- **Where does the script get the table's columns?** From the catalog, or does the script declare them?
  A hand-rolled audit table's shape may not match the base table's. Probably: the result set is
  authoritative and `ChangeSchema` is built from it, with `ExcludeColumns` trimming — which is what the
  contract above assumes, and is worth stating rather than assuming.
- **Multi-table queries.** A single audit table serving many source tables is a common legacy shape. The
  script would need the table name as a parameter and a filter — which the context already carries. Does
  anything else break? Probably not, but it has not been thought through.
- **Stored procedures rather than statements.** `SourceQuery` carries `CommandText`; it would need a
  `CommandType` to call a procedure. Cheap to add, easy to forget.

---

# Outcome — resolved 2026-08-27: substantially built as phase 30

This doc proposed a `ScriptedChangeQuery` reader Kind whose statement comes from a script.
**`implementation/done/phase-030-scripted-source-queries.md` built it**, as `ScriptedQuery`, before this
doc was revisited — the two were designed from the same reasoning and arrived at the same shape.

What matched:

- a **generic, unprefixed reader Kind** rather than a hook inside the existing readers
- a script returning **text plus a typed parameter list**, never a spliced string, so parameter binding
  stays in the host even when the SQL came from an operator
- a `ChangeQueryShape`-equivalent describing how to read the result back — operation column, value map,
  excluded bookkeeping columns — which is what makes one reader consume a shadow table, SQL Server CDC,
  LogMiner output and a hand-rolled audit table alike

What changed on contact:

- **`WatermarkShape.WatermarkColumn` was dropped.** This doc proposed a per-row position column, "the
  highest value seen wins". `ReadResult`'s own contract rules it out: the new watermark is "computed by
  the reader up front … not derived from what was actually read". A per-row maximum cannot bound the
  window, so rows arriving mid-read would extend it. `BuildWatermarkQuery` running first is the whole
  mechanism.
- **The host does not pre-render the segment.** A builder that owns the statement owns the scoping.

What is still open, and now tracked elsewhere:

- **the acknowledgement hook** this doc named as a fourth caller — built in phase 33 as
  `IPositionAcknowledging`. A scripted builder gaining a `BuildAcknowledgeQuery` is a small follow-on
  once that exists.
- **positional parameters** for ODBC and JDBC — recorded in `change-tracking-odbc-jdbc.md`'s outcome.
- **`CommandType`** for calling a stored procedure — recorded in phase 30's own open questions.
