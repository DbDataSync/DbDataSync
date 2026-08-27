# Phase 14 — Positional Change Rows

**Status**: Complete
**Plan reference**: `architecture/planning/done/replace-per-row-dictionary-with-positional-array.md`,
which carries the measurements that chose this shape over the alternatives. Written as a
retrospective: the design was settled by that benchmark and agreed directly, and the change is small.

## What was built

`ChangeRow` stops carrying its own column names.

```csharp
// before
public sealed record ChangeRow(ChangeOperation Operation, IReadOnlyDictionary<string, object?> Values);

// after
public sealed record ChangeRow(ChangeOperation Operation, ChangeSchema Schema, object?[] Values);
```

`ChangeSchema` is the column layout the whole read shares — built once by the reader, referenced by
every row it produces, resolving a name to an ordinal through one dictionary that lives for the run
rather than one per row. A change set has a single layout and millions of rows; carrying the names per
row meant a dictionary allocation per row, a hash insert per cell going in, and a hash lookup per cell
coming out.

All four readers build a schema once and fill a positional array per row. `ResultSetSchema` does it
for the three whose result set *is* the source row (`SELECT *`); Change Tracking's incremental query
carries two leading bookkeeping columns, so its schema is the key columns followed by the non-key ones
— exactly the select list's tail, which makes a result ordinal map to a schema ordinal by subtracting
the two.

`ChangeRowDataReader` — the one consumer — resolves each target column's source ordinal **once**,
against the first row's schema, and every cell after that is an array index.

Two deliberate behaviour changes fell out:

- **A mapping naming a source column the reader doesn't produce now throws**, listing what was
  actually available. It used to be a silent dictionary miss that wrote NULL into the target: for a
  NOT NULL column that surfaced much later as a constraint violation naming a column nobody had
  touched, and for a nullable one it never surfaced at all.
- **Column lookup is case-insensitive.** It was ordinal before, by accident of the default comparer;
  SQL Server resolves column names case-insensitively and the rest of this layer already compares them
  with `OrdinalIgnoreCase`.

And one narrowing, recorded because it is a real loss of expressiveness: a delete's non-key columns
used to be *absent* from the dictionary and are now *present but null*, so "not supplied" and
"genuinely NULL" are no longer distinguishable. Nothing distinguishes them today — `ChangeRow`'s
contract already says only key columns are reliable for a delete, and writers key off `Operation` —
but a future writer wanting that distinction would need it carried explicitly.

## Deliberately not done: reusing the row buffer

The benchmark's `rowarray` variant reuses one `object?[]` for every row, which removes the per-row
array allocation as well. That is not safe as a contract here. `IStagingProvider` is a public
extension point, and the columnar staging provider still open in planning is exactly the consumer that
would need to *buffer* rows — a shared buffer would hand it the same row N times. So each row gets its
own array, and `ChangeRow`'s documentation says the array belongs to that row.

The cost is one array per row (424 B on a 50-column table), which is why the measured improvement is
about 3x rather than the benchmark's 3.9x.

## How this was verified

- **7 new unit tests** on `ChangeSchema`/`ChangeRow`: ordinals follow declared order; lookup is
  case-insensitive; an unknown column throws and names what was available; `TryGetOrdinal` reports
  without throwing; rows read by ordinal and by name; a delete carries its key with the rest null; and
  every row in a read shares one schema instance.
- Existing reader and pipeline tests updated to the indexer and green unchanged — they are what
  establish that the four readers still produce the same data.
- Full suite: **154 non-integration** (up from 147), **47 integration**. `dotnet build` clean, no
  warnings.
- **A real before/after on the change-tracking reader**, by stashing the change and running the same
  test three times each: **811 ms → 784 ms median for 150,000 rows (~3%, inside noise)**. That table
  has three columns and the read is dominated by TDS transfer, so there is very little representation
  cost there to remove. This is worth stating plainly: on a narrow table this change does not make
  reads measurably faster, and an earlier single 560 ms sample quoted in Phase 12 was an outlier
  rather than a baseline.

  The win is allocation, and it scales with column count. `tools/benchmarks` at 50 columns:
  **1,293 MB → 335 MB** of garbage for 200,000 rows through a real `SqlBulkCopy`, with gen0
  collections dropping from 81 to 21 and GC pause from 17 ms to 6 ms.

## What's explicitly not built

Columnar batches — still open in `architecture/planning/todo/columnar-change-batches.md`, to be
revisited when a target exists that can consume typed values. Nothing about `IStagingProvider`,
`IChangeWriter` or the staging table changed.

## Notes / things to revisit later

- A columnar staging provider would want the schema *before* the first row, to size its column
  buffers. It is currently reachable only from a row. `ReadResult` would be the natural place to hoist
  it if that becomes real.
- Values are still `object?`, so every value type is still boxed on the way out of the source reader.
  Removing that is the columnar question, not this one.
