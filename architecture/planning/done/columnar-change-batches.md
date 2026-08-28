# Column-oriented change batches

**Status: resolved 2026-08-27 — see Outcome at the end.**

Split out of what was originally "Optimize the in memory layout of changes". The per-row half of that
question is answered and built (`planning/done/replace-per-row-dictionary-with-positional-array.md`);
this is the half that is deliberately still open.

## The question

Should a batch of changes be held as column-oriented, natively-typed arrays — pooled, in standardised
batch sizes, optionally spilled to disk — rather than as a sequence of rows?

## What is already established

Measured with `tools/benchmarks` (`tools/DataSync.Benchmarks`); the tables live in the done doc above.
In short, at 200,000 rows × 50 columns:

- **Through `SqlBulkCopy`, columnar buys nothing** over the positional row array now in use. Identical
  allocation to the byte (335 MB), identical GC, and *higher* peak heap because it buffers a batch.
  `SqlBulkCopy` asks for every cell as `object`, so 10M cells means 10M boxes however they were stored.
- **Through a sink that takes typed values, columnar allocates nothing at all** — 0 MB, zero
  collections, lowest peak heap (7.2 MB against 16.0) and lowest working set, at half the wall time.
- **Peak memory is a dial**: 1,000-row batches cost 0.9 MB of steady-state heap and are also the
  fastest; larger batches cost memory and lose throughput to cache pressure.
- Untyped columnar is *slower* than a row array and gets worse as tables widen (8.4 → 11.7 ns/cell from
  5 to 50 columns), because row-wise traversal touches one cache line per column.

So the shape of the answer is known and is not in doubt. What is missing is a reason to act on it.

## The trigger

**A target that can consume typed values.** Everything above says this is worth building the moment
one exists, and worth nothing before. Candidates already on the roadmap: the Parquet staging provider
in `implementation-plan.md`'s backlog, any non-MSSQL driver (a Postgres binary COPY takes typed
values), and the caching / multi-target fan-out / offline-target scenarios that motivated the original
note.

Revisit when one of those is concrete — the design should be shaped by a real sink's needs rather than
guessed at in advance.

## Still unmeasured, and worth doing when it is revisited

- **The source side.** The benchmark generates values; a columnar reader would have to call typed
  getters (`GetInt32`) on the source `SqlDataReader` to stay unboxed. No reader does that today, so
  without it the boxing floor simply moves upstream and the win evaporates. This is the first thing to
  check, not the last.
- Offloading batches to disk, and multi-target fan-out from one read.
- **Concurrent mappings**, where per-slot buffering multiplies and the batch-size dial matters most.
  The work queue runs several mappings at once by design, so this is the case that decides what a
  standardised batch size should actually be.
- Server GC rather than workstation.

## One constraint the per-row work already imposed

`ChangeRow` owns its values array precisely so a staging provider *can* buffer rows — the row buffer is
deliberately not reused. A columnar provider is the consumer that needs that, so the two fit together;
but it also means a columnar path would want the schema before the first row, to size its buffers, and
today the schema is reachable only from a row. `ReadResult` is the natural place to hoist it.

---

# Outcome — resolved 2026-08-27

The trigger this doc was explicitly waiting for has arrived, and the work goes to
`implementation/todo/phase-038-postgres-copy-staging.md`.

> **The trigger: a target that can consume typed values.** Everything above says this is worth building
> the moment one exists, and worth nothing before.

Phase 20 built the Postgres driver, and Npgsql's binary `COPY` (`NpgsqlBinaryImporter`, `Write<T>` per
cell) is that sink. Postgres currently stages through `BatchInsertStagingProvider` — the deliberately
lowest-common-denominator path phase 18 built for ODBC and JDBC — so there is a real improvement to
make regardless of what the columnar answer turns out to be.

**The phase does not assume the answer.** This doc's own warning is why:

> **The source side.** The benchmark generates values; a columnar reader would have to call typed
> getters on the source reader to stay unboxed. No reader does that today, so without it the boxing
> floor simply moves upstream and the win evaporates. This is the first thing to check, not the last.

Every reader goes through `ResultSetSchema.ReadValues`, which is `GetValue(i)` — boxing every cell on
the way in. A typed sink downstream of a boxing source saves the second boxing, not the first. So the
phase measures three configurations through `tools/DataSync.Benchmarks` before designing anything, and
ships the `COPY` provider either way, because it beats multi-row `INSERT` whatever the in-memory
representation is.

The two constraints this doc recorded are carried into the phase as things the measurement has to
account for: **the schema is needed before the first row** to size buffers (and `ReadResult` is where
it should be hoisted), and **concurrent mappings** are the case that should actually decide a batch
size, since the work queue runs several at once by design.

