# Phase 24 — In-process transforms

**Status**: Built
**Plan reference**: `architecture/planning/done/csharp-script-extension-points.md`, "three places a
transform can happen". Phase 22 built the first (SQL at the source) and phase 23 built the script that
generates it. This builds the other two, which run **here**, on rows as they flow from the reader to
staging.

## Why both, when phase 22 already transforms

Because they buy different things, and the table in the planning doc is the argument:

| | runs on | cost | can do |
| --- | --- | --- | --- |
| `Transform` / `sqlColumnExpression` | the **source engine** | free to us | whatever that engine's SQL can express |
| `valueColumnExpression` | this process, per cell | a delegate call per cell | whatever C# can express, on one value |
| `rowTransform` | this process, per row | a delegate call per row | whatever C# can express, across the row — including dropping it |

A source that cannot express what is needed, a value that needs a .NET library, a row that has to be
filtered on something the source cannot see: none of those are reachable from SQL, and all of them are
ordinary.

## The seam

Exact, and already clean:

```csharp
var read = await reader.ReadChangesAsync(...);
var staged = await stagingProvider.StageAsync(targetConnection, target, read.Rows, ...);
```

`read.Rows` is an `IAsyncEnumerable<ChangeRow>`. The transforms wrap that stream. Nothing else moves.

## `valueColumnExpression`

```csharp
public interface IValueColumnExpression
{
    /// Called once per pass. The source columns this transforms — everything else is untouched.
    IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext context);

    object? Evaluate(object? value, ValueColumnExpressionContext context);
}
```

**`DeclareColumns` is not ceremony.** Without it, a transform interested in one column of forty costs a
delegate call on all forty, for every row — which is precisely the class of per-cell cost phase 14
measured and designed the positional row layout to avoid. Declaring once per pass turns the hot path
into "is this ordinal in a small set", and for the common case of one or two columns it is a
one-element check.

**Deletes are skipped.** A `ChangeOperation.Delete` row carries only its key; every other slot is null,
and the writers key off the operation rather than the values. Handing those nulls to a transform that
expects values is a trap with no upside — the transformed value would be discarded.

## `rowTransform`

```csharp
public interface IRowTransform
{
    /// Called once per pass, before any row, so a transform that changes the row's shape can say so.
    ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext context);

    /// Return null to drop the row.
    ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext context, CancellationToken ct);
}
```

**`DeclareSchema` is the part that is easy to leave out and expensive to add later.** Staging builds its
table from the schema of the first row it sees. If a transform can add a column, the shape has to be
knowable *before* the first row — otherwise the staging table is built from the untransformed shape and
every added column is silently dropped. Declaring it once also means the cost is paid once.

Returning null drops the row, which makes filtering fall out for free and is the single most likely
thing anyone will want. It does mean the rows read and the rows staged diverge — which is correct, and
should be **logged**, not hidden.

Row transforms **do** see deletes, unlike value expressions: `row.Operation` is on the context, and
"drop the deletes" is a legitimate thing to want.

## Ordering

Source SQL, then value expressions, then the row transform. That order is forced by where each one
lives, and the UI should say so rather than leaving an operator to infer it.

## What this phase does not build

Metadata providers (phase 25), source query builders (phase 26), target statements (later).

Any change to staging or the writers. A transform changes a value on its way out of the source; it has
nothing to say about which target column it lands in.

## How to verify when built

- Unit tests over compiled scripts: a value expression touching one column of several, its
  `DeclareColumns` honoured, deletes skipped; a row transform adding a column via `DeclareSchema`,
  dropping a row by returning null, and seeing a delete's operation.
- A test that a value expression on an undeclared column is never called — the performance property,
  which would otherwise regress silently.
- `Category=Integration`: a replication with both bound lands transformed, filtered data.
- Playwright: bind a row transform that drops a row, and see the row count fall.
- Full suite green.

## Open questions

- **Per-cell cost, measured.** `tools/benchmarks` exists and phase 14's numbers are the baseline. Worth
  running before anyone puts a value expression on a hot column, and worth recording next to those.
- **Two transforms in one slot.** The planning doc's list-valued slots are still not built; one script
  per slot per level for now.

---

# Retrospective

Built as planned. All three transform places from the planning doc now exist, and the ordering between
them is asserted rather than assumed.

## The guard forbade the shape of the contract it was guarding

Phase 23's `ScriptSyntaxGuard` banned `System.Threading`. `IRowTransform.TransformAsync` returns
`ValueTask<ChangeRow?>` and takes a `CancellationToken` — both of which live there. So the guard made
the contract **literally unimplementable**, and the four row-transform tests failed on their first run
with a message about not reading files.

`System.Threading` came off the list. What banning it would have bought is `Thread.Sleep` — a script
blocking itself, which is a bug in that script and not a way out of the process. What it cost was the
async contract.

Worth recording as a general shape: a guard written before the thing it guards will forbid something
the thing needs, and the way you find out is by writing the first real implementation.

## `DeclareColumns` is the difference between usable and not

A value expression interested in one column of forty would otherwise cost a delegate call on all forty
for every row — precisely the per-cell cost phase 14 measured and the positional row layout exists to
avoid. Declaring once per pass turns the hot path into an ordinal lookup over a small array, and
`AnUndeclaredColumn_IsNeverPassedToTheScript` pins it, because losing it later would be silent and
would show up only as a replication that got slower.

Three more things fell out of building it:

- **A script that declares nothing makes the pipeline empty**, so binding a transform that turns out
  not to apply costs nothing at all rather than a wrapper per row.
- **A value returned unchanged copies nothing.** `ApplyValues` clones the row's values only on the
  first actual change, so a script that declares three columns and rewrites one allocates one array.
- **A declared column the reader did not return is ignored, not fatal.** A mapping's projection may
  legitimately not carry it — and since phase 22, projections are narrower than they used to be, so
  this is more likely than it was.

## Deletes: skipped by value expressions, seen by row transforms

Left open in the planning doc, answered here, and the two answers differ on purpose.

A `ChangeOperation.Delete` carries only its key; every other slot is null and the writers key off the
operation rather than the values. Handing those nulls to a value expression is a trap with no upside —
whatever it computed would be discarded. So deletes skip that slot entirely.

A row transform sees them, because `row.Operation` is right there and "drop the deletes" is a
legitimate thing to want.

## Ordering is now demonstrated, not documented

Source SQL, then values, then the row. Playwright test 16 proves it by accident-turned-on-purpose: the
row transform's filter was first written against `Gadget` and had to be rewritten against `tegdaG`,
because by the time a row reaches the transform the source has already evaluated `REVERSE(Name)` from
test 15. The test now says so in a comment, and `BothSlots_RunInOrder_ValuesThenTheRow` asserts the
value-then-row half in isolation.

## `DeclareSchema` is called, and called early

Before the first row is yielded, not lazily — staging builds its table from the schema of the row it
sees first, so a column added without saying so would be silently dropped. Nothing consumes the
declared schema yet (no built-in path changes shape), which makes it the piece most likely to rot: it
is called and its result discarded. That is deliberate — a contract that is never called cannot be
relied on when the first shape-changing transform arrives — but it should be wired into staging the
first time something actually adds a column.

## Verification

- `TransformPipelineTests` — 11 tests, all driven through genuinely compiled scripts: declared columns
  honoured and undeclared ones never seen, deletes skipped by values and seen by rows, a row dropped
  and counted, values rewritten, the run log reached, both slots in order, a throwing script naming
  what it was doing, a script declaring nothing, and a declared column the reader did not return.
- **Playwright test 16** — write a row transform in the UI, bind it on the *replication* (the middle
  level, which nothing had exercised), clear the target, touch the source, run, and find one row staged
  out of two read.
- Full .NET suite green: 335 tests. Playwright: 16 green.

## Still open

- **Per-cell cost, unmeasured.** `tools/benchmarks` exists and phase 14's numbers are the baseline.
  Worth running before anyone puts a value expression on a hot column.
- **`DeclareSchema`'s output is discarded** until something changes shape, above.
- **One script per slot per level.** The planning doc's list-valued slots are still not built.
