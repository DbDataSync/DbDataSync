# Phase 38 — Postgres binary COPY staging

**Status**: Implemented 2026-09-16 — **Part 1 only**, and the split is the main thing to know about this
phase. The provider is built, registered and tested. The columnar measurement this phase was also meant
to settle is *not* done, and could not be: its deliverable is a set of numbers off a real server, and the
implementing machine has no Docker. It is now
`architecture/implementation/todo/phase-150-the-columnar-decision.md`, with everything this phase learned
about what would have to be measured carried into it.
**Plan reference**: `architecture/planning/done/columnar-change-batches.md`, whose stated trigger had
arrived, and `planning/done/additional-database-drivers.md`, which named this as the phase that answers
the columnar question.

## What was built

**`PgCopyStagingProvider`** — staging through `COPY … FROM STDIN (FORMAT BINARY)`, registered on
`PostgresDriver` ahead of the generic `BatchInsertStagingProvider`, which stays registered as the
fallback. Kind `PgCopyStaging`, prefixed per the naming rule.

It creates *the same staging table* as the generic provider, using that provider's own DDL builder
(`StagingStatement.BuildCreate`) rather than a second copy of it. That is the design: only how the rows
get in differs, so every writer downstream is unaffected and a mapping can be moved between the two
without anything else noticing. The interchangeability is what the integration tests assert — one test
body, run against both Kinds.

This is the **first thing `PostgresDriver` has ever registered that is not
`DbDataSync.Drivers.Generic`'s**, and the reason is worth recording: `COPY` is a protocol on the
connection, not a statement. There is nothing for `SqlDialect` to render, so there was no hook it could
have gone through. Phases 17 and 18's claim — a new engine is a dialect, a connection factory and a
catalog — held for twenty phases and this is the shape of its first exception.

### The real difference between the two providers, which is not speed

**Binary `COPY` does no coercion.** A parameterised `INSERT` hands the server a value and lets it convert
to the column's type; the binary protocol sends the column's own wire format and the server converts
nothing. So every cell is written with the type the staging column was *declared* with — resolved from
the target's cached shape, the same place the DDL comes from — and a value the converter will not accept
fails the pass.

That is a real behavioural difference, not a detail, and it is why `StagingTable` stays registered rather
than being replaced. It gets three things:

- **One type table, not two.** `PostgresValueBinding` already had a base-type-name → `NpgsqlDbType` map
  for typing segment bounds; that half is now `PostgresNpgsqlTypes`, used by both. The binding's own
  string-parsing half is keyed on the resolved `NpgsqlDbType` rather than on the type name, so the two
  halves cannot disagree about what `money` or `bigserial` is.
- **A failure that names the column, the CLR type, the Postgres type, and `StagingTable` as the
  alternative**, rather than a raw Npgsql error about a converter.
- **A hint for the one mismatch common enough to name**: a `timestamp with time zone` column fed a
  `DateTime` whose `Kind` is not `Utc` (and the reverse). Npgsql refuses it, correctly — picking a time
  zone for somebody's data is not a decision a staging provider should make silently — and the message
  says so and points at the mapping change or the transform that would fix it.

### Two details that would each have been a silent wrong answer

- **The generated ordinal is not in the `COPY` column list.** It is `GENERATED ALWAYS AS IDENTITY`, so
  the engine fills it in as each row lands, exactly as it does for the generic provider's `INSERT`.
  Naming it would make the server reject the copy outright.
- **Values are written through a runtime-type switch, not as `object`.** Every reader in this codebase
  goes through `reader.GetValue(i)`, so a staged value arrives boxed; handing Npgsql a
  statically-`object` value leaves it resolving a converter for `object` rather than for what the value
  actually is. Naming the CLR type at the call site is what lets it pick the converter from that type to
  the column's. (This is also the first concrete evidence for the columnar question phase 150 inherits:
  the boxing is upstream, and `COPY` unboxes on the way out.)

## How this was verified

- **`PgCopyStagingStatementTests`, 25 tests, green locally, no server.** The parts that are wrong-able
  without one and that fail *quietly* when wrong: the `COPY` column list and its order (binary COPY is
  positional — a list off by one does not fail at the first row, it writes into the column next door),
  the ordinal's absence from it, the identical DDL, and every type-name spelling resolving to the right
  `NpgsqlDbType` — both the `information_schema` spellings and the internal ones, with and without a
  length or precision.
- **`PgCopyStagingTests`, `Category=Integration`** — one body, both Kinds, against a real Postgres. Every
  column of every row compared *between the two tables* rather than against literals, over the types the
  phase named (`integer`, `text`, `numeric`, `double precision`, `smallint`, `bigint`, `boolean`,
  `varchar`, `uuid`, `bytea`, `date`, `timestamp`), plus all-nulls, plus a delete, plus an empty batch.
  Then the one place they differ: the `timestamptz` mismatch, asserted to fail legibly through COPY *and*
  to succeed through the alternative the message names. Plus: a failed copy leaves no staging table
  behind.
- **The full non-integration suite is green**, after one real regression this phase caused and that only
  a full run would have found: `ChangeReaderFirstPassContractTests` reflects over every driver assembly
  with `GetTypes()`, and this provider's async methods put `NpgsqlConnection` and `NpgsqlBinaryImporter`
  into compiler-generated state-machine *fields*, which type loading has to resolve. Phase 109h's
  `ExcludeAssets="runtime"` means Npgsql is not in the output until the library is installed, so the
  Postgres driver assembly stopped being reflectable without it — it had stayed reflectable only
  because everything touching Npgsql was previously in non-async method bodies. **Production is
  unaffected**, and that was checked rather than assumed: every `GetTypes()` call in `src/` already
  catches `ReflectionTypeLoadException` and keeps the types it got, and `LibrarySurfaceExtractor` reads
  metadata through Mono.Cecil without loading anything at all. The fix is the third instance of the
  pattern 109h and 109i each hit once — an un-excluded `Npgsql` reference on the test project — and the
  contract test was deliberately *not* made lenient, because a sweep that silently covers fewer readers
  than it appears to is the exact failure mode that file exists to prevent.

### Two real bugs this phase found, in CI rather than locally

Both were found by the first `dotnet-integration` run, and both are fixed. Neither is in the new
provider; both are things it was the first caller to expose, which is the argument for having built the
integration tests around a *comparison* rather than around the new path alone.

- **A staged read leaked its source reader when the consumer threw on the first row.**
  `ChangeOrdering.DetectAsync` peeks the first row and replays it through an iterator whose
  `yield return first` sat *outside* its own `try`. An iterator suspended at a `yield` outside its `try`
  runs no `finally` when disposed — so the source data reader was never closed, and the next thing to
  use that connection failed with "a command is already in progress" instead of with whatever actually
  went wrong. Present since phase 132 and reachable by any staging provider that throws on the first
  row; this phase's own "fails legibly" test is the first thing whose bad value is in row one.
- **`library validate` was relying on the lenient path's coercion.** Its synthetic row wrote a
  `DateTime.UtcNow` — `Kind=Utc` — into a `TIMESTAMP` column, which has no time zone. That worked only
  because Postgres's first-registered staging provider was the batched-`INSERT` one, where the server
  coerces; making `COPY` the native path (and first-registered means native, per `IDriver`'s own
  documented convention, exactly as `MsSqlStagingTableProvider` is for SQL Server) turned it into a real
  failure. The harness is what was wrong, not the driver: `Unspecified` is the faithful Kind for a
  column with no zone, and SQL Server's `DATETIME2` is indifferent either way. Its own doc comment
  already carried two "real bug found running this against the real Postgres container" notes; this is
  the third, and the same lesson each time.

### What is not verified

**Nothing here was run against a real Postgres on the implementing machine** — no Docker — so the
integration tests above are verified by CI's `dotnet-integration` job, which has a `postgres:17-alpine`
service, rather than locally. That is the same bar the rest of this repo's integration suite is held to,
but it is worth saying plainly for a phase whose whole subject is a wire protocol.

**No performance claim is made.** `COPY` is expected to beat batched multi-row `INSERT` — the generic
provider spends a parameter per cell and is bounded by the server's parameter limit, so a wide table
stages in small batches however many rows arrived — but "expected" is all this phase can say. Measuring
it is phase 150's, along with the columnar question it was going to answer at the same time.

## Why Part 2 became its own phase

The plan's own words were that this phase "measures before it designs", and that the columnar decision
"rides on the numbers". Three configurations through `tools/DbDataSync.Benchmarks`, against a real
server.

There is no server here, and there is a second reason to split it that is not about this machine: the
existing harness does not model the middle configuration at all. It generates values — `RowArrayReader`
boxes them, `ColumnarReader` does not — so it covers "boxed end to end" and "typed end to end", but not
"typed columnar fed from a boxing source", which is the half-measure the whole question turns on. And its
"typed sink" is a synthetic consumer that reads typed values and discards them, not a real `COPY`.

Both gaps are work, and writing that work blind — a benchmark sink nobody has run, producing the numbers
a large architectural decision would be made on — is worse than not writing it. A benchmark that is
subtly wrong does not fail; it misleads. So phase 150 states precisely what has to be added and what each
answer would mean, and leaves the building to someone who can run it.

## Out of scope, unchanged

- A columnar path. Phase 150 decides whether there is to be one.
- Parquet staging, disk spilling, multi-target fan-out — all downstream of that answer.
- `COPY` for any engine but Postgres. There is no other `COPY`.
