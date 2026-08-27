# Phase 22 — Column transforms in the source SQL dialect (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/csharp-script-extension-points.md` — the "three places a
transform can happen" decision. This phase builds the first of the three, and it is a prerequisite for
the other two: a script that *generates* a source-dialect expression needs somewhere to put it.

## What this phase will build

`ColumnMapping.Transform` has existed since phase 1 and is read by nothing:

```csharp
public sealed class ColumnMapping
{
    public required string SourceColumn { get; set; }
    public required string TargetColumn { get; set; }
    public string? Transform { get; set; }   // declared, never read
}
```

It becomes **a SQL expression in the source dialect, rendered into the reader's SELECT list and
evaluated by the source engine**. `UPPER({{column}})`, `CAST({{column}} AS decimal(18,2))`,
`COALESCE({{column}}, 'UNKNOWN')`.

The source does the work. Nothing is transformed in this process, nothing is boxed twice, and an
expression that narrows the data moves less of it over the wire.

### `{{column}}` is substituted, and it has to be

The obvious design — write `UPPER(Region)` and splice it in — breaks on the reader that matters most.
`MsSqlChangeTrackingStatement` builds:

```sql
SELECT CT.SYS_CHANGE_OPERATION, …, CT.[Id], base.[Region]
FROM CHANGETABLE(CHANGES [dbo].[Orders], @previousVersion) AS CT
LEFT JOIN [dbo].[Orders] AS base ON CT.[Id] = base.[Id]
```

An unqualified `Region` happens to resolve there; an unqualified `Id` is ambiguous and the statement
fails. Which columns are safe depends on which reader is running — an operator cannot reasonably be
expected to know that.

So the expression may contain `{{column}}`, and the **reader** substitutes the correctly-qualified,
correctly-quoted reference for its own statement: `base.[Region]` for the Change Tracking reader,
`"region"` for the Postgres batch reader. An expression with no `{{column}}` is used verbatim, which
keeps `'literal'`, a reference to another column, or a correlated subquery all expressible.

## The contract change

**`IChangeReader.ReadChangesAsync` gains `IReadOnlyList<ColumnMapping> columnMappings`.**

This is the one breaking change in the phase, and it makes the three pipeline stages consistent rather
than inconsistent: `IStagingProvider.StageAsync` and `IChangeWriter.ApplyAsync` both already take the
column mappings. The reader is currently the odd one out — it is the only stage that does not know
what it is being asked to produce, which is exactly why it can only say `SELECT *`.

## Projection replaces `SELECT *`

`Generic.SourceProjection` renders the SELECT list from the column mappings and the dialect:

```csharp
public static string Render(SqlDialect dialect, IReadOnlyList<ColumnMapping> columnMappings, Func<string, string> reference)
```

`reference` is how a given source column is written in *this* statement — the hook the aliasing problem
above needs. Each entry is the expression aliased back to the **source** column name, so everything
downstream is unchanged: staging still maps source names to target names, and `ChangeSchema` still
carries source names.

```sql
SELECT [Id], UPPER([Region]) AS [Region], [Amount] FROM [dbo].[Orders] WHERE 1 = 1;
```

**With no column mappings, the projection is `*`.** That keeps every direct caller working — a reload
triggered before mappings exist, and the driver tests that call `ReadChangesAsync` with an empty list —
and it is the honest reading of "no projection specified".

A useful side effect: where mappings *are* supplied, a reader stops pulling columns nobody maps. A
mapping covering 3 of 40 columns currently reads all 40 and discards 37.

## Scope

Applies to every reader that builds a statement:

- `Generic.WatermarkReader`, `Generic.BatchReloadReader` — `SELECT *` today
- `MsSqlBatchReloadReader` — `SELECT *` today
- `MsSqlChangeTrackingReader` — both paths: the full-load `SELECT *`, and the incremental statement
  where `{{column}}` resolves to `base.[…]`

The Change Tracking incremental path keeps selecting **all** catalog columns rather than only the
mapped ones. Narrowing it interacts with the primary-key requirement and the `__BaseMissing` ordinal
arithmetic, and is a separate change worth doing on its own rather than folded in here.

## What this phase does not build

Any scripting. No Roslyn, no script registry, no `ScriptResolution`. This phase is the field that has
been declared since phase 1 finally doing something, and it is worth having on its own for operators
who will never write C#.

Validation of the expression beyond what the source engine says when it runs it. `Transform` is
admin-authored raw SQL, exactly as `SourceTableRef.Filter` already is, and phase 1's XML doc on that
field records the same reasoning: an arbitrary expression cannot be parameterised, and the operator
authoring it is the one who could already point a connection at any database.

## How to verify when built

- Unit tests on `SourceProjection` rendering, per dialect: no mappings → `*`; a mapping with no
  transform → the quoted column; a transform with `{{column}}` → substituted and aliased; a transform
  without it → verbatim and aliased.
- `Category=Integration` against MSSQL: a mapping with `UPPER({{column}})` lands upper-cased data
  through the batch reader, the watermark reader, **and** the Change Tracking reader — the third being
  the one the substitution exists for.
- Cross-engine: the same mapping against Postgres, proving the expression is the *source's* dialect and
  the target never sees it.
- A test that a mapping covering a subset of columns no longer selects the rest — the side effect is
  worth pinning, because losing it later would be silent.
- Playwright: a transform entered in the mapping editor survives a save and reload.
- Full suite green.

## Open questions

- **Should the SPA validate the expression before saving?** A `SELECT <expr> FROM <table> WHERE 1=0`
  against the source would prove it parses, and the connection-test flow from phase 19 is the
  precedent. Cheap, and it turns a failed run into a red squiggle. Probably yes, but it is a second
  round trip on every mapping save and worth deciding deliberately.
- **Does a transform apply to a delete's key columns?** A Change Tracking delete carries only the key,
  and the key comes from `CT.[…]`, not `base.[…]`. A transform on a key column would therefore be
  applied on non-delete rows and not on deletes — an inconsistency worth either forbidding or
  documenting.
