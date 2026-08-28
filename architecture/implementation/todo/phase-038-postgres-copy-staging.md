# Phase 38 — Postgres binary COPY staging, and the columnar decision (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/columnar-change-batches.md`, whose stated trigger has
arrived, and `planning/done/additional-database-drivers.md`, which listed COPY staging and named it as
the phase that answers the columnar question.

## Why now

`columnar-change-batches.md` closed with a precise condition:

> **The trigger: a target that can consume typed values.** Everything above says this is worth building
> the moment one exists, and worth nothing before.

Phase 20 built the Postgres driver. Npgsql's binary `COPY` — `NpgsqlBinaryImporter`, with
`Write<T>(value, NpgsqlDbType)` per cell — is exactly that sink, and it is currently unused: Postgres
stages through `BatchInsertStagingProvider`, the deliberately-lowest-common-denominator path phase 18
built for ODBC and JDBC.

So this phase does two things that only make sense together: it makes Postgres staging fast, and it
answers a question that has been open since phase 13.

## Part 1 — `PgCopyStaging`

A staging provider using `COPY … FROM STDIN (FORMAT BINARY)` instead of batched multi-row `INSERT`.

Prefixed Kind (`PgCopyStaging`), per the naming rule: engine-specific gets a prefix, generic does not.
`StagingTable` stays registered and stays the fallback — an instance that will not permit `COPY`, or a
column type Npgsql cannot write in binary, needs a path that works.

Expected to be dramatically faster and to allocate far less, for the reason the benchmark already
measured: `SqlBulkCopy` asks for every cell as `object`, so ten million cells means ten million boxes
however they were stored. A binary importer that takes typed values does not.

## Part 2 — the question this exists to answer

The benchmark's finding, restated because it is the whole design:

- through `SqlBulkCopy`, **columnar buys nothing** — identical allocation to the byte (335 MB),
  identical GC, *higher* peak heap because it buffers
- through a sink that takes typed values, **columnar allocates nothing at all** — 0 MB, zero
  collections, 7.2 MB peak against 16.0, at half the wall time

`COPY` is the second kind of sink. So the question is whether `ChangeRow`'s positional `object?[]`
should become, or be joined by, column-oriented typed arrays.

### The first thing to check is upstream, and it is the thing that could sink it

`columnar-change-batches.md` names this as "the first thing to check, not the last":

> **The source side.** The benchmark generates values; a columnar reader would have to call typed
> getters (`GetInt32`) on the source `SqlDataReader` to stay unboxed. No reader does that today, so
> without it the boxing floor simply moves upstream and the win evaporates.

Every reader in this codebase goes through `ResultSetSchema.ReadValues`, which is
`reader.GetValue(i)` — boxing every cell on the way in. A typed sink downstream of a boxing source
saves the *second* boxing, not the first.

**So this phase measures before it designs.** Three configurations through
`tools/DataSync.Benchmarks`, which already exists for exactly this:

1. today's row array → `COPY` (unbox on the way out)
2. typed columnar → `COPY`, with the source still boxing (the half-measure)
3. typed columnar end to end, with a reader calling typed getters

If (2) is close to (1), the answer is that the source side is the binding constraint and columnar is
not worth building until a reader is rewritten — which is a much larger change than a staging provider
and would deserve its own phase. If (3) is dramatically better than both, the case is made and the
shape is known.

**The phase ships (1) regardless**, because `COPY` beats multi-row `INSERT` whatever the in-memory
representation is. The columnar decision rides on the numbers.

## What the readers would need, if the numbers say yes

Recorded here so the measurement knows what it is measuring:

- a typed read path — `GetInt32`/`GetDateTime`/`GetDecimal` per column rather than `GetValue`, chosen
  once per pass from the catalog's types, which is the same shape `MsSqlValueBinding` and
  `PostgresValueBinding` already use for segment bounds
- **the schema before the first row**, to size buffers. Today it is reachable only from a row.
  `ReadResult` is the natural place to hoist it, and `columnar-change-batches.md` already says so.
- a batch size. The benchmark found 1,000 rows both the cheapest in steady-state heap (0.9 MB) and the
  fastest, with larger batches losing to cache pressure — but it also flagged **concurrent mappings**
  as the unmeasured case that should actually decide it, since the work queue runs several at once by
  design and per-slot buffering multiplies.

## What this phase does not build

A columnar path, unless the measurement earns it. Writing one on the strength of a benchmark that
generated its own values, when every real reader boxes on the way in, is how a large change ships and
achieves nothing.

Parquet staging, disk spilling, or multi-target fan-out. All named in
`columnar-change-batches.md` as motivating cases; all downstream of this answer.

## How to verify when built

- `Category=Integration`: `PgCopyStaging` staging a change set that the existing writers then apply,
  producing the same target rows as `StagingTable` does — same test body, two Kinds.
- Type coverage: the types the cross-engine tests already use (`int`, `text`, `numeric`, `timestamp`),
  plus nulls, plus at least one Npgsql cannot write in binary, falling back or failing clearly.
- The fallback: `COPY` refused by permissions producing a message that names `StagingTable` as the
  alternative rather than a raw provider error.
- **The benchmark numbers, recorded in the retrospective**, for all three configurations. That is the
  deliverable that closes `columnar-change-batches.md`, and it is worth as much as the provider.
- Full suite green.

## Open questions

- **Does `COPY` fit the staging contract?** `IStagingProvider.StageAsync` streams rows into a table it
  created and returns a handle. `COPY` is a different protocol on the same connection, not a
  statement — worth checking early that it composes with the existing transaction and cleanup shape
  rather than after the provider is written.
- **`COPY` and the operation marker.** Staging appends a `__Operation` column; the binary importer has
  to write it like any other, which is fine, but it is the sort of detail that only shows up when the
  column count is wrong by one.
