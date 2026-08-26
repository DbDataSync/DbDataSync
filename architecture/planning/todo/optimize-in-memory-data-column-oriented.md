# Optimize the in memory layout of changes


We need to consider replacing the sequence of ChangeRow objects with either a column oriented ChangeBatch concept, or even a simple fixed object[] or object[,] instead of the dictionary we are using.

Batches with properly typed arrays containing a single column data will take less memory and are generally faster for certain types of operations, and would be faster to serialize to disk for caching / multi target distribution / offline target scenarios. Because we will mainly be using the batches to iterate over in a row by row fashion, they may not work as well for our in memory representation.

If column store will be better, we should consider using an existing dotnet library
(Apache Arrow / Parquet.NET / Microsoft.Data.Analysis.DataFrame)
or rolling our own depending on our needs and if it offers any improvements.

---

## Investigation (Claude, 2026-08-26) — measured, plan not yet agreed

### How the shape is actually used

Four producers, exactly one consumer. Every reader (`MsSqlChangeTracking`, `MsSqlWatermark`,
`MsSqlBatchReload`, plus the full-load path) builds a `Dictionary<string, object?>` per row from a
`DbDataReader`. The only thing that ever reads it back is `ChangeRowDataReader`, which adapts the
stream to `IDataReader` for `SqlBulkCopy` and resolves each cell as
`sourceColumnByTarget[targetColumns[ordinal]]` then `Values[sourceColumn]` — **two hash lookups per
cell, per row**, on top of a dictionary allocation per row.

Three properties of that pipeline decide this question:

- Access is **forward-only, one row at a time**, never random, never retained.
- The column set and the source→target mapping are **fixed for the whole run** and known before the
  first row is read — nothing about them needs to be carried per row.
- The sink is `SqlBulkCopy`, which consumes through `IDataReader.GetValue(int)` — **row-wise, and
  returning `object`**. Anything columnar has to be re-materialised row-wise at that boundary, and
  anything typed has to be re-boxed there.

### Measurements

1,000,000 rows × 5 columns (`int`, `string`, `string`, `decimal`, `DateTime` — the dev-harness
`Orders` shape), built and then consumed the way `ChangeRowDataReader` consumes them. Release build,
allocation via `GC.GetAllocatedBytesForCurrentThread`:

| representation | time | allocated | per row |
| --- | --- | --- | --- |
| `Dictionary<string, object?>` per row (today) | 203 ms | 389 MB | 408 B |
| positional `object?[]` per row, ordinals resolved once | 18 ms | 137 MB | 144 B |
| columnar `object?[]` per column, batched 10k | 29 ms | 114 MB | 114 B |
| columnar natively-typed arrays, batched 10k | 12 ms | 42 MB | 44 B |

### What the numbers say

- **The dictionary is the expensive part, and it is not the row-vs-column question.** Simply moving to
  a positional `object?[]` with the ordinal map resolved once per run is **11x faster and allocates
  2.8x less**, with no change to streaming, no batching, and no new dependency.
- **Column-oriented is *slower* here, not faster** — 29 ms against the row array's 18 ms. The doc's own
  reservation was right: the batch is consumed row by row, so a columnar layout pays stride costs on
  every access and gets nothing back.
- **The 9.3x allocation win of typed columns is real but currently unredeemable.** It comes from not
  boxing — and `SqlBulkCopy` re-boxes at `GetValue(int)` regardless. That win is only collectable by a
  sink that can consume typed columns; none exists today.
- **Context for the ~11x**: the reader's own measured throughput against a local SQL Server is roughly
  3.7 µs/row (150,000 rows in 560 ms, from the Phase 12 integration test). The dictionary work is about
  0.2 µs of that — so **~5% of reader time**, before counting GC. The allocation reduction is the more
  meaningful half: 389 MB per million rows is ~100 MB/s of garbage at observed throughput.

### Options (not yet chosen)

1. **Positional `object?[]` + a shared schema.** Replace `ChangeRow.Values` with an ordinal-indexed
   array and a `ChangeSchema` resolved once per run. Blast radius is small and entirely internal: the
   record, four readers, and `ChangeRowDataReader`. Captures nearly all the available win.
2. **Column-oriented batches.** Measurably worse for this pipeline, and the part that would pay —
   typed columns — cannot be collected through `SqlBulkCopy`. The honest trigger for revisiting is a
   **columnar sink**, and one is already on the backlog: the Parquet staging provider. Caching,
   multi-target fan-out and offline targets are the same trigger, and none of them exist yet either.
3. **Take an Arrow / Parquet.NET / DataFrame dependency for the in-memory shape.** Nothing in these
   numbers justifies it. Those libraries earn their place on serialisation and interchange, which is
   option 2's trigger, not this one's.

Not measured: whether GC pauses are visible in end-to-end run duration (the microbenchmark reports
allocation, not pause time), and whether the dictionary's share grows on wider tables — 5 columns is
narrow, and per-row dictionary cost scales with column count while the array's does not.
