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
- **The 9.3x allocation win of typed columns is real; the first pass called it "unredeemable", which
  was wrong.** See the third pass below — it is unredeemable *through `SqlBulkCopy` specifically*, and
  that is one driver's sink, not a property of the architecture.
- **Context for the ~11x**: the reader's own measured throughput against a local SQL Server is roughly
  3.7 µs/row (150,000 rows in 560 ms, from the Phase 12 integration test). The dictionary work is about
  0.2 µs of that — so **~5% of reader time**, before counting GC. The allocation reduction is the more
  meaningful half: 389 MB per million rows is ~100 MB/s of garbage at observed throughput.

### Options (not yet chosen)

1. **Positional `object?[]` + a shared schema.** Replace `ChangeRow.Values` with an ordinal-indexed
   array and a `ChangeSchema` resolved once per run. Blast radius is small and entirely internal: the
   record, four readers, and `ChangeRowDataReader`. Captures nearly all the available win.
2. **Column-oriented batches over pooled typed arrays.** Ties option 1 against `SqlBulkCopy` and costs
   more peak heap there; against any sink that can take typed values it allocates **nothing at all**.
   See the third pass. Whether this is worth building now turns on how soon a non-`SqlBulkCopy` target
   exists, not on the numbers.
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


## Third pass — end to end through a real sink, measuring peak, not just totals

The first two passes measured *cumulative allocated bytes* in isolation, which cannot distinguish
"box every row up front and hold it" from "box one cell transiently at the sink boundary, where it
dies in gen0 immediately". Those have very different GC behaviour, and the first pass's conclusion
that the typed win was "unredeemable" generalised `SqlBulkCopy`'s object-per-cell contract to the
whole architecture. `IStagingProvider` is engine-neutral; a Postgres binary COPY or a Parquet writer
has no such constraint. **That claim was wrong and is withdrawn.**

Re-measured properly with `scripts/benchmarks` (`tools/DataSync.Benchmarks`, committed so these
numbers can be re-checked rather than taken on trust): 200,000 rows × 50 columns (10M cells) into a
real 50-column SQL Server table,
each variant in **its own process** so peak working set belongs to it alone, with GC counts, GC pause
time and a 2 ms-sampled peak managed heap. `columnar` is hand-rolled batches over `ArrayPool`-rented
typed arrays, boxing only at `GetValue`, one cell at a time.

**Through `SqlBulkCopy`** (median of three):

| representation | ms | allocated | peak heap | peak WS | gen0 | gen1/2 | GC pause |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Dictionary` per row | 2,850 | 1,288 MB | 16.9 MB | 94 MB | 81 | 0 | 15 ms |
| positional `object?[]` | 2,618 | 335 MB | 16.7 MB | 92 MB | 21 | 0 | 6 ms |
| columnar, pooled typed | 2,623 | 335 MB | 23.5 MB | 97 MB | 21 | 0 | 5 ms |

**Through a typed sink** — same production code, consumer reads `GetInt32`/`GetDecimal`/`GetDateTime`
/`GetString` instead of `GetValue`:

| representation | ms | allocated | peak heap | peak WS | gen0 | GC pause |
| --- | --- | --- | --- | --- | --- | --- |
| `Dictionary` per row | 1,000 | 1,130 MB | 16.5 MB | 94 MB | 70 | 8 ms |
| positional `object?[]` | 245 | 153 MB | 16.4 MB | 91 MB | 9 | 1 ms |
| columnar, pooled typed | **133** | **0.0 MB** | **7.3 MB** | **78 MB** | **0** | **0 ms** |

### What this actually shows

- **Against `SqlBulkCopy`, columnar buys nothing over a row array** — identical allocation to the byte,
  identical GC, and *higher* peak heap because it buffers a batch. `SqlBulkCopy` asks for every cell as
  `object`, so 10M cells means 10M boxes whichever way they were stored. The transient-versus-upfront
  distinction does not reduce the box count when the sink demands one per cell.
- **Against a typed sink, columnar allocates literally nothing** — zero bytes, zero collections, and
  both the lowest peak heap (7.3 MB against 16.4) and the lowest working set. Pooled arrays are rented
  once and reused; nothing boxes. This is the "reduce GC and stabilise memory" outcome, and it is real.
- **The boxing floor is the whole difference.** `SqlBulkCopy` costs ~335 MB where the same
  representation costs 0 MB against a typed consumer.
- **Peak memory is a dial, not a consequence.** Batch size against a typed sink:

  | batch | peak heap | peak WS | ms |
  | --- | --- | --- | --- |
  | 1,000 | 0.9 MB | 76 MB | 138 |
  | 10,000 | 7.3 MB | 79 MB | 123 |
  | 50,000 | 27.9 MB | 95 MB | 161 |
  | 200,000 | 110.4 MB | 158 MB | 230 |

  Small batches are both the cheapest *and* the fastest — 1,000 rows is 0.9 MB of steady-state heap
  with zero allocation, against today's 1,288 MB of garbage. Larger batches cost memory and lose
  throughput to cache pressure. So "standardised batch sizes + a typed column pool" is a memory
  guarantee that can be stated as a number, which per-row allocation can never be.

### Still not measured

Offloading batches to disk; multi-target fan-out from one read; several mappings running concurrently
(where per-slot buffering multiplies, and where the columnar peak-heap dial matters most); Server GC
rather than workstation; and reading from a real source `DbDataReader` rather than generated values —
a columnar reader must use typed getters (`GetInt32`) to stay unboxed, and no reader does that today.
