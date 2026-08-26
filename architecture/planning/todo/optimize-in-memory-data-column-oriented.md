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
allocation, not pause time).

## Second pass — does table shape change the answer?

Re-run across two shapes carrying the **same 5,000,000 cells**, so shape is isolated from volume.
Normalised per cell, since that is the only way the two are comparable. Absolute figures shifted a
little from the first pass because the read path is now a uniform loop rather than hand-unrolled for
five columns; only within-run comparisons are meaningful.

| representation | ns/cell (5×1M) | ns/cell (50×100k) | B/cell (5×1M) | B/cell (50×100k) |
| --- | --- | --- | --- | --- |
| `Dictionary`, no capacity | 33.7 | 31.1 | 108.8 | 108.5 |
| `Dictionary`, pre-sized | 27.4 | 23.9 | 81.6 | 51.7 |
| positional `object?[]` | 6.3 | 5.9 | 28.8 | 24.5 |
| columnar `object?[]` | 8.4 | **11.7** | 24.0 | 24.0 |
| columnar typed arrays | 5.0 | 5.0 | 8.8 | 8.8 |

**The earlier guess that dictionary cost would grow with width was wrong on time.** Per-cell cost is
essentially flat for both the dictionary and the array — 33.7 vs 31.1 and 6.3 vs 5.9 — so the ratio
between them, roughly 4.5x on time and 3x on allocation, holds at either shape. Width does not change
which option to pick.

Three things width *does* change:

- **Columnar gets worse, not better.** Untyped columnar degrades from 8.4 to 11.7 ns/cell — the only
  representation measured that gets slower as the table widens. Row-wise traversal of 50 separate
  column arrays touches 50 cache lines per row instead of one contiguous run. The argument against
  columnar for this pipeline is therefore *stronger* on wide tables, which is where it would normally
  be expected to pay.
- **Pre-sizing the dictionary goes from a minor saving to a large one**: 544 → 408 B/row at 5 columns
  (25%), but 5,424 → 2,584 B/row at 50 columns (52%). The un-pre-sized dictionary resizes
  3 → 7 → 17 → 37 → 79 on the way to 50 entries, discarding every intermediate.
- **Absolute per-row cost stops being negligible.** A 50-column table today allocates ~5.4 KB per row
  through the incremental reader.

### A defect this exposed, independent of everything above

Only one of the four readers omits the capacity hint — and it is the incremental Change Tracking path
(`MsSqlChangeTrackingReader.cs:200`, `new Dictionary<string, object?>()`), which is the hot path for
ongoing replication. `MsSqlWatermarkReader`, `MsSqlBatchReloadReader` and Change Tracking's own
full-load path all pass `reader.FieldCount`. Passing it in the one place it is missing halves that
path's per-row allocation on a 50-column table, is a one-line change, and is worth doing whatever is
decided about the representation.
