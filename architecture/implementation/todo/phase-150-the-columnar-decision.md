# Phase 150 — the columnar decision, measured (planned)

**Status**: Planned, not started. Split out of phase 38 on 2026-09-16, per
`architecture/implementation/README.md`'s rule about a phase turning out bigger than expected — see
`architecture/implementation/done/phase-038-postgres-copy-staging.md`'s own "Why Part 2 became its own
phase" for the split and its reasoning.
**Plan reference**: `architecture/planning/done/columnar-change-batches.md`, which posed the question and
set the trigger; phase 38, which delivered the trigger.

## The question

Should `ChangeRow`'s positional `object?[]` become, or be joined by, column-oriented typed arrays?

`columnar-change-batches.md` measured a benchmark that said this depends entirely on the sink:

- through `SqlBulkCopy`, **columnar buys nothing** — identical allocation to the byte (335 MB),
  identical GC, *higher* peak heap because it buffers
- through a sink that takes typed values, **columnar allocates nothing at all** — 0 MB, zero
  collections, 7.2 MB peak against 16.0, at half the wall time

and closed with a precise condition: *"The trigger: a target that can consume typed values. Everything
above says this is worth building the moment one exists, and worth nothing before."*

Phase 38 built that sink. `PgCopyStagingProvider` writes typed cells into Npgsql's binary importer. So
the condition is met and the question is live.

## Why it still is not answered

`columnar-change-batches.md` named the thing that could sink it, and called it "the first thing to check,
not the last":

> **The source side.** The benchmark generates values; a columnar reader would have to call typed
> getters (`GetInt32`) on the source `SqlDataReader` to stay unboxed. No reader does that today, so
> without it the boxing floor simply moves upstream and the win evaporates.

That is still true, and phase 38 turned it from an argument into an observation. Its `WriteCellAsync`
dispatches on the *runtime* type of a boxed value, because every reader in this codebase goes through
`ResultSetSchema.ReadValues` → `reader.GetValue(i)`. The new typed sink is being fed from a boxing
source. It unboxes on the way out; the box was already paid for on the way in.

## What to measure

Three configurations, through `tools/DbDataSync.Benchmarks`:

1. **today's row array → `COPY`** — boxed in memory, unboxed on the way out. This is what phase 38 ships.
2. **typed columnar → `COPY`, source still boxing** — the half-measure: columnar arrays *filled from*
   boxed values, so the box is still paid, just earlier.
3. **typed columnar end to end** — a reader calling `GetInt32`/`GetDateTime`/`GetDecimal` per column,
   into typed arrays, into `COPY`. Nothing boxes.

**If (2) is close to (1)**, the source side is the binding constraint: columnar is not worth building
until a reader is rewritten, which is a much larger change than a staging provider and deserves its own
phase. **If (3) is dramatically better than both**, the case is made and the shape is known.

## What the harness needs first, and why this is not a five-minute job

Phase 38 checked, and `tools/DbDataSync.Benchmarks` cannot run any of the three as it stands:

- **Its "typed" sink is synthetic.** It reads typed values and discards them (`sink += ... .Length`), with
  a comment saying it "stands in for a sink that can take typed values — a Parquet/Arrow writer, a
  Postgres binary COPY". Standing in was the right call when no such sink existed. One does now, and the
  whole point of re-measuring is to use it: a real `NpgsqlBinaryImporter` against a real server, where
  the converter, the wire format and the flush all cost something the stand-in charged nothing for.
- **It has no SQL Server sink either, for Postgres.** The orchestrator creates a SQL Server scratch
  database and every child connects to it. A `COPY` sink means a Postgres scratch database alongside, or
  instead of, it.
- **Configuration (2) is not modelled at all.** `RowArrayReader` boxes, `ColumnarReader` does not, and
  both *generate* their values. Neither is "columnar arrays filled from boxed values", which is the one
  configuration the decision actually turns on. It needs a third reader.
- **Configuration (3) needs a real source reader**, not a generator, if the number is to mean anything:
  the claim being tested is that typed getters on a real `SqlDataReader`/`NpgsqlDataReader` avoid the
  box, and a generator that hands back typed values has assumed the conclusion.

Phase 38 deliberately did not build any of this blind. A benchmark that is subtly wrong does not fail —
it misleads, and it misleads about a decision to restructure the type every reader and every staging
provider in the system passes around.

## What the answer would change, if it says yes

Recorded here so the measurement knows what it is measuring — carried forward from phase 38, which
carried it from `columnar-change-batches.md`:

- **a typed read path** — `GetInt32`/`GetDateTime`/`GetDecimal` per column rather than `GetValue`, chosen
  once per pass from the catalog's types. Same shape `MsSqlValueBinding` and `PostgresValueBinding`
  already use for segment bounds.
- **the schema before the first row**, to size buffers. Today it is reachable only *from* a row — which
  is why `ChangeOrdering.DetectAsync` has to peek-and-replay the first one. `ReadResult` is the natural
  place to hoist it, and `columnar-change-batches.md` already said so.
- **a batch size.** The benchmark found 1,000 rows both the cheapest in steady-state heap (0.9 MB) and
  the fastest, with larger batches losing to cache pressure — but it also flagged **concurrent mappings**
  as the unmeasured case that should actually decide it, since the work queue runs several at once by
  design and per-slot buffering multiplies.

## Also worth taking while the harness is open

Phase 38 makes no performance claim for `COPY` over batched `INSERT` at all — it was not measured, only
expected. The expectation is well-founded (the generic provider spends a parameter per cell and is
bounded by the server's parameter limit, so a wide table stages in small batches however many rows
arrived), but configuration (1) above measures it for free, against the provider it replaces. Record it.

## How to verify when built

- The three configurations' numbers, recorded in the retrospective. **That is the deliverable** — this
  phase's output is a decision with evidence behind it, not code.
- Whichever way it goes, `architecture/planning/done/columnar-change-batches.md` gets its answer written
  into it, since the question has been open since phase 13 and the doc is where anyone will look.
- If the answer is yes: a `todo/` phase for the typed read path, which is where the actual work would be.
  This phase does not build it.
