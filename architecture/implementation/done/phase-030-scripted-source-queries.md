# Phase 30 — Scripted source queries

**Status**: Built
**Plan reference**: `architecture/planning/done/csharp-script-extension-points.md` §3. Numbered 26
there, before phases 25–27 were planned against those numbers; renumbered in that document's outcome
table.

## What is left of §3

The planning doc described three levels of ownership over the source query. Two of them already exist:

- **§3a `columnExpression` in its SQL shape** — built as `sqlColumnExpression` in phase 23. A script
  contributes the SELECT-list entry for one column and the host builds the statement.
- **§3c `sourceQueryTransform`** — string surgery on the generated statement. Described in the planning
  doc as "offered reluctantly" and "the one most likely to break silently when the generator's output
  changes underneath it". **Not built, and this phase decides not to.** See below.

So this phase is **§3b**: a script that owns the whole statement.

## `ScriptedQuery` — a reader Kind, not a hook

The planning doc framed this as a slot on the existing readers. It should not be, and the reason is the
same one phase 23 discovered when the column-expression hook moved out of `SourceProjection`: putting a
script *inside* a reader means two paths to one behaviour, and the scripted one is the path nobody can
see.

The alternative is cleaner and was already the recommendation in
`planning/todo/script-generated-change-queries.md`: **a generic reader Kind whose statement comes from a
script.** `ScriptedQuery`, unprefixed, registered by every driver alongside `Watermark` and
`BatchReload`. It appears in the SPA's reader picker like any other Kind, because capability discovery
has driven that picker since phase 10.

That gives:

- **No change to any existing reader.** `WatermarkReader` and `BatchReloadReader` are untouched.
- **The choice is visible.** An operator selects `ScriptedQuery` in the pipeline's reader picker;
  nothing is silently reshaping a statement they thought they understood.
- **It is the ODBC and JDBC answer**, and available before those drivers exist.

## The contract

```csharp
public interface ISourceQueryBuilder
{
    /// Fixes the end of the window. Null when the mechanism has no position of its own.
    SourceQuery? BuildWatermarkQuery(SourceQueryContext context);

    /// The rows to read, from the stored position up to the end fixed above.
    SourceQuery BuildReadQuery(SourceQueryContext context);
}

public sealed record SourceQuery(string CommandText, IReadOnlyList<ScriptQueryParameter> Parameters);
public sealed record ScriptQueryParameter(string Name, object? Value);
```

**Two queries, not one**, mirroring `WatermarkStatement`'s existing `BuildMaxWatermark`/`BuildRead`
pair. A reader answers "what changed" and "what is the new position" and those are usually separate
statements. A builder with no position of its own returns null, and the host echoes the previous
watermark back — exactly what `BatchReloadReader` does today.

**A script returns text plus a typed parameter list, never a finished string with values spliced in.**
That keeps parameter binding in the host, where phases 9, 17 and 22 already put it, even when the SQL
itself came from an operator. It is also what makes the output testable: `SourceQuery` is data.

`SourceQueryContext` carries the resolved `SourceTableRef`, the mapping's columns, the source catalog's
columns, the previous watermark, the segment (both the `BatchReloadSegment` and the already-rendered
predicate and parameters, so a script can use the rendering or ignore it), the dialect, and the
binding's parameters.

## Deletes and the operation column

A scripted query is the first reader whose rows might not all be inserts — an audit table has an
operation column, and that is most of why anyone would write one. So the builder also describes how to
read its result back:

```csharp
public sealed record SourceQueryShape(
    string? OperationColumn,
    IReadOnlyDictionary<string, ChangeOperation> OperationValues,
    ChangeOperation DefaultOperation,
    string? WatermarkColumn,
    IReadOnlyList<string> ExcludeColumns);
```

- **`OperationColumn` absent** → every row is an insert, which is watermark semantics. The existing
  `Watermark` reader is a degenerate case of this one.
- **`OperationValues`** maps whatever the source calls things onto `ChangeOperation` — `'I'/'U'/'D'` for
  a shadow table, `1/2/4` for SQL Server CDC, `'INSERT'/'UPDATE'/'DELETE'` for LogMiner. Configuration,
  not code.
- **`WatermarkColumn`** names the column carrying the new position, for mechanisms that report it per
  row rather than through a separate end-position query.
- **`ExcludeColumns`** drops the mechanism's own bookkeeping from the `ChangeSchema` before staging sees
  it, so `__$operation` and `seq` do not arrive as columns nobody mapped.

## `DetectsDeletes` is declared, not inferred

A scripted reader either surfaces deletes or it does not, and only its author knows. It comes from the
script's manifest, like every other declared capability since phase 17, and flows through
`ReaderCapability` into the SPA's picker.

Getting this wrong is not cosmetic: a script that claims deletes and does not surface them produces a
target that quietly accumulates rows the source removed, and the operator — seeing the UI say deletes
are covered — has no reason to look.

## Not `ISegmentExpandingReader`

A scripted query has no meaningful answer to "read bucket 3 of 4"; the segment predicate is in the
context for a script that wants it, but expansion stays the reload readers' job. Same rule
`change-tracking-strategies.md` sets for CDC readers generally.

## Why not `sourceQueryTransform`

Receiving a generated statement and returning a modified one is string surgery on SQL this codebase
generates and will keep changing. Phase 22 changed `SELECT *` to an explicit projection; phase 18
changed the batch reader's WHERE composition. Each would have silently broken a transform written
against the previous shape, with no compile error and no test — the replication would just start reading
the wrong rows.

Everything it was for is reachable another way: a column's expression is `sqlColumnExpression`, a
predicate is the mapping's own `Filter`, and owning the statement is `ScriptedQuery`. Recorded as
decided-against rather than not-yet-done.

## How to verify when built

- Unit tests over compiled scripts: a builder producing text and typed parameters; the parameters bound
  by the host rather than spliced; an operation column decoded through `OperationValues`; excluded
  columns absent from the `ChangeSchema`; a per-row watermark column; a null watermark query echoing
  the previous position.
- `DetectsDeletes` declared in the manifest reaching `ReaderCapability`.
- `Category=Integration` against MSSQL: a scripted query over a hand-rolled audit table replicating
  inserts, updates **and deletes** — the case the slot exists for.
- Playwright: `ScriptedQuery` selectable in the reader picker.
- Full suite green.

## Open questions

- **Stored procedures.** `SourceQuery` carries `CommandText`; calling a procedure needs a `CommandType`.
  Cheap to add, easy to forget.
- **A post-write acknowledgement hook** — `pg_replication_slot_advance`, shadow-table pruning — is the
  same open question in three change-tracking documents. A scripted query is where it would first be
  reachable, and it is still not answered.

---

# Retrospective

Built as a reader Kind, as planned. Three things in the plan's own sketch turned out to be wrong and
were corrected against contracts that already existed.

## The per-row watermark column had to go

`SourceQueryShape` was sketched with a `WatermarkColumn` — the position reported per row, "the highest
value seen wins". `ReadResult`'s own XML doc rules it out in as many words:

> `NewWatermark` is computed by the reader up front … before `Rows` is enumerated, **not derived from
> what was actually read** — so it's always safe for the caller to persist it as the new watermark once
> every row has been successfully staged and applied.

A per-row maximum looks equivalent and is not: it cannot *bound* the window, so rows arriving mid-read
extend it and the stored watermark claims ground the pass never covered. `BuildWatermarkQuery` running
first, returning a scalar, is the whole mechanism — and it is the bounded-window shape
`MsSqlChangeTrackingReader` already uses and every log-based reader in
`planning/todo/change-tracking-strategies.md` will need.

`TheWindowIsBoundedBeforeTheReadSoRowsArrivingMidPassAreNotClaimed` inserts a row after the read has
been set up and asserts it is left for the next pass.

## The host does not pre-render the segment

The sketch had the context carrying a rendered `SegmentPredicate` and its parameters. A builder that
owns the whole statement also owns whether and how to scope it, so the context carries the
`BatchReloadSegment` and nothing else. Pre-rendering a predicate the script may not want would have
meant the reader needed an `ISegmentValueBinder` it otherwise has no use for.

## `DetectsDeletes` is false, and that is a real limitation

A scripted query is the reader *most* likely to surface deletes — it is most of why anyone writes one.
It reports false anyway, because capabilities are per **Kind** and this Kind is one reader shared by
every script bound to it. There is no honest per-script answer to give.

Phase 17's rule decides which way to be wrong: false, because overstating the guarantee is what loses
data. A reader claiming deletes it does not surface leaves a target quietly accumulating rows the source
removed, and an operator told deletes are covered has no reason to look. The opposite error is merely
pessimistic — a reload nobody needed.

Worth fixing properly one day: it wants a per-*binding* capability, which nothing in the model has.

## The composition-root problem, and what it produced

`ScriptedQueryReader` needs the script host. A driver must not depend on Roslyn. Drivers are singletons
constructed with `new MsSqlDriver()` in two processes.

The answer was to let the **registry** hold readers the host supplies for a driver:
`DriverRegistry.Register(driver, hostReaders)`, with `Readers(driverType)` and `FindReader(…)` as the
one place to ask. Capability discovery and `RunExecutor` both go through it, so a host-supplied reader
cannot be visible to the pipeline and invisible to the SPA's picker, or the other way round —
`SupportsReader` and `SupportsSegmentation` were rewritten onto the same lookup rather than left reading
`driver.Readers` directly.

That needed one more opt-in interface beside phase 29's `IDialectProvider`:
`ITableCatalogProvider`. Both catalogs were `internal`, which is right — nothing outside a driver should
be assembling its components — except for something composing a *generic* component for that engine
from outside, which is exactly this. It is also the seam phase 29 named for a future per-connection
catalog override.

## `sourceQueryTransform` decided against

Receiving a generated statement and returning a modified one was in the plan as §3c, "offered
reluctantly". It is not built and should not be.

The evidence is in this repository's own history: phase 22 changed `SELECT *` to an explicit projection,
phase 18 changed the batch reader's WHERE composition. Each would have silently broken a transform
written against the previous shape — no compile error, no failing test, just a replication reading the
wrong rows. Everything it was for is reachable another way: a column's expression is
`sqlColumnExpression`, a predicate is the mapping's own `Filter`, and owning the statement is
`ScriptedQuery`.

## Verification

- `ScriptedQueryReaderTests` (`Category=Integration`) — 7 tests against a real SQL Server and a
  hand-rolled audit table, the shape a legacy source actually has: inserts, updates and **deletes**
  decoded through `OperationValues`; bookkeeping columns excluded from the schema; the bounded window;
  reading only what is new since the previous watermark; an unmapped operation code falling back rather
  than failing the run; a missing `script` option saying so; and an operation column the query did not
  return naming what it did.
- Full .NET suite green: 474 tests across ten projects. Playwright: 16 green.

## Still open

- **A per-binding `DetectsDeletes`**, above.
- **Stored procedures.** `SourceQuery` carries `CommandText`; a procedure needs a `CommandType`.
- **The post-write acknowledgement hook** — `pg_replication_slot_advance`, shadow-table pruning — is
  now the same open question in four documents. A scripted query is where it is first reachable, and it
  is still unanswered.
- **No E2E** selecting `ScriptedQuery` in the reader picker. The Kind reaches `DriverCapabilities`
  through the registry and is asserted there; driving it through the UI needs a source with an audit
  table, which the Playwright fixture does not have.
