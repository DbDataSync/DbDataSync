# Phase 27 — C# that generates hook SQL

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/database-provisioning-and-lifecycle-scripts.md`, the
"literal SQL, with a script as the generator" tier. Depends on phase 26 for the hook points and
execution path, and on phase 25 for the type system this uses to reason about schema.

## Why a third tier, when phase 26 already runs an operator's SQL

Phase 26's hooks are static text with identifiers substituted and values bound. That covers the large
majority — `UPDATE STATISTICS`, an index rebuild, a control-table row — and it deliberately stops short
of anything that has to *decide* something. Three things it cannot express, all of them ordinary:

- **A statement whose shape depends on the target's current state.** "Add the columns this mapping needs
  that the target does not have yet" is not a fixed statement; it is a diff, and a diff is code.
- **A statement that differs by engine.** The same hook bound at connection level and inherited by
  mappings that write to SQL Server and PostgreSQL needs `SET IDENTITY_INSERT` in one place and
  `OVERRIDING SYSTEM VALUE` in another — the split phase 20 already found and `SqlDialect` already
  models.
- **A statement that should not run at all.** `IF @rowsWritten > 0 …` works on SQL Server and needs a
  `DO` block on PostgreSQL; "return an empty list" is the portable spelling of the same intent, and phase
  26 recorded the workaround precisely so this phase could remove the need for it.

The motivating case named when this was scoped is **target schema evolution**: a source that gains a
column, and a target that should gain it too on the next run rather than at the next maintenance window.
Phase 25 deliberately put `ALTER` of an existing target table out of scope because ALTER is where
destructive mistakes live and it needs its own diff-and-confirm interaction. This phase is the other half
of that answer — not DataSync deciding to alter a target, but an operator writing, reviewing and binding
the code that does, with the run log recording every statement it emitted.

## The contract

One slot, not four, because a script is a type and four bindings for one concern would be four places to
keep in sync:

```csharp
public const string LifecycleHook = "lifecycleHook";   // added to ScriptSlots.All

public interface ILifecycleHook
{
    /// Which points this hook wants. Declared once per pass, so the host does not open a source
    /// connection — or call the script at all — for a point it does nothing at. The same reason
    /// IValueColumnExpression.DeclareColumns and IRowTransform.DeclareSchema exist.
    IReadOnlyList<string> DeclarePoints(LifecycleHookContext context);

    /// Statements, with their parameters. Never a finished string with values interpolated into it.
    IReadOnlyList<HookStatement> BuildStatements(string point, LifecycleHookContext context);
}
```

`HookStatement` and `HookParameter` are phase 26's, unchanged — which is the point of phase 26 leaving
its execution path taking an ordered `IReadOnlyList<HookStatement>` per point. **This phase adds a
producer and changes nothing else in the runner.**

The rule from `csharp-script-extension-points.md` holds without exception here, and matters more than
anywhere it has been applied so far:

> A script returns a description of what to do; the host does it.

A hook script never holds a connection, never opens a transaction, and cannot commit. It emits statements
and parameters; the host binds and executes them, so parameter binding — and therefore injection safety —
stays where phases 9, 17, 22 and 26 put it, even though the SQL now came from a loop in someone's C#.

Resolved through `ScriptHost.ResolveBinding<ILifecycleHook>` like every other slot, inheriting the
compilation cache, the syntax guard, the three-level binding and the Scripts UI for free.

## What the script is told

```csharp
public sealed record LifecycleHookContext(
    string Point,
    SourceTableRef Source,
    TableRef Target,
    IReadOnlyList<ColumnMapping> ColumnMappings,
    IReadOnlyList<ColumnMetadata> SourceColumns,
    IReadOnlyList<ColumnMetadata> TargetColumns,
    IScriptDialect Dialect,
    HookRunFacts Run,
    ScriptParameters Parameters,
    Action<string> Log);

/// Everything phase 26 exposes as @parameters, as typed values a script can branch on.
public sealed record HookRunFacts(
    Guid RunId, string Replication, string Mapping, RunKind RunKind,
    string? Segment, int SegmentIndex, int SegmentCount, bool IsLastSegment,
    long? RowsStaged, long? RowsWritten, string? StagingLocation, string? Watermark);

/// Null where the fact is not yet known at this point — RowsWritten before the load, StagingLocation
/// before staging. Nullable rather than zero, because "none written" and "not yet known" are different
/// answers and a script that cannot tell them apart will get it wrong exactly once.
```

**`SourceColumns` and `TargetColumns` are what make schema evolution writable**, and they are the reason
this phase depends on phase 25: the useful hook is "for each mapped source column with no target column,
emit `ALTER TABLE … ADD`", and rendering that column's type is `SqlDialect.ToCanonicalType` +
`RenderColumnType` — the pair phase 25 builds. Without them a script author would be hand-writing a type
map inside a hook, which is the worst possible place for one.

`IScriptDialect` rather than the driver's own `SqlDialect`, for the reason `IScriptDialect`'s doc comment
already gives: `SqlDialect` lives in `DataSync.Drivers.Generic`, which references
`DataSync.Scripting.Abstractions`, so taking it would be circular — and it is a moving target that a
script written today should survive. It gains what a hook needs and no more: `RenderColumnType` for the
canonical type, exposed as a narrow method rather than by handing over the dialect.

## Ordering, and what happens when both tiers are bound

At a point with both phase 26 entries and a bound script: **the configured list runs first, in declared
order, then the script's statements.** The configured ones are visible in the config diff and in
`git log`; the generated ones are the escape hatch, and an escape hatch runs last.

## Logging is not optional here

Phase 26 logs each hook's point, name, statement count, rows affected and elapsed. A generated hook logs
**the statement text as well**, at Info, every time. A script emitting DDL that nobody can read after the
fact is precisely the failure `task-run-errors-during-high-volume-workload.md` and `ReadDiagnostics` both
exist to prevent, and it is worse here than anywhere else in the product: this is operator code emitting
schema changes on someone else's target, on a schedule, unattended.

## How it will be verified

**Unit** (`DataSync.Scripting.Tests`)
- `DeclarePoints` is honoured: a script declaring only `afterLoad` is not called at the other three, and
  no source connection is opened for it
- a hook returning an empty list at a point is a no-op, not an error — this is the portable "don't run"
- parameters come back bound, not interpolated: a `HookParameter` whose value is `x'; DROP TABLE y --`
  reaches the server as a value
- `RowsWritten` is null at `beforeLoad` and populated at `afterLoad`; `StagingLocation` is null at
  `beforeStage`
- a script throwing `ScriptExecutionException` fails the run with the script's name attached, and
  `onError: warn` still downgrades it

**Integration** (`Category=Integration`)
- the schema-evolution hook end to end: add a column at the source, add it to the mapping, and a bound
  `beforeStage` hook emits the `ALTER TABLE … ADD` that lets the very same run stage and load it —
  against MsSql and against Postgres, because the generated type is the whole point
- the same hook is a no-op on the second run

**E2E** (`tests/DataSync.Web.Tests`) — authoring a `lifecycleHook` script in the Scripts area, binding it,
and seeing the statements it generated in the Runs log tail.

## Out of scope

- **Anything that lets a script hold a connection, run its own query, or open a transaction.** The
  metadata a hook needs is handed to it in the context. A script that wants to ask the database something
  the context does not carry is a request to widen the context, considered on its merits — not a reason
  to hand over a `DbConnection`. `IMetadataProvider` remains the one contract that gets one, for the
  reason its own design records: there is no way to describe "ask the catalog" as data.
- **DataSync deciding to evolve a target schema on its own.** This phase makes it writable and reviewable;
  it stays something an operator binds deliberately. Automatic target ALTER remains out of scope, as
  phase 25 recorded.
- **A per-pass scope**, still — inherited from phase 26 along with `IsLastSegment` as the workaround.

## Open questions to resolve during implementation

- **Should `DeclarePoints` be allowed to depend on run facts?** It is called once per pass, before the
  first read, so `RowsWritten` is unknowable — meaning a hook cannot decide "I only want `afterLoad` if
  rows moved". Returning an empty statement list at that point covers it, at the cost of a script call
  that does nothing. Confirm that is cheap enough to be the answer.
- **Does a generated `ALTER` need to invalidate cached target metadata?** Staging reads the target's
  columns per pass (`BatchInsertStagingProvider`, `MsSqlStagingTableProvider`), so within one run the
  ordering works — but any later caching of that lookup would silently break a schema-evolution hook, and
  this should be a note in the code rather than a fact rediscovered.
- **Whether `ILifecycleHook` should be able to emit statements for the *source* connection.** Phase 26's
  hook config picks a side per entry; a script currently generates for whichever side its binding names.
  A hook wanting to touch both would need two bindings, which may be the right answer or may be an
  awkward one — decide once someone has the use.
