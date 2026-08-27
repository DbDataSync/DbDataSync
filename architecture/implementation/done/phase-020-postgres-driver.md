# Phase 20 — PostgreSQL Driver

**Status**: Built
**Plan reference**: `architecture/planning/done/additional-database-drivers.md`; depends on phases 17
and 18.

## Why Postgres first

Best-documented ADO.NET provider of the five (Npgsql), a genuinely different bulk path to exercise
later (`COPY`), and it is the first engine that makes cross-engine replication real. It is also the
sink `planning/todo/columnar-change-batches.md` has been waiting on — though that question belongs to
phase 21, not this one.

If phases 17 and 18 did their job, this phase is small: a dialect, a connection factory, catalog
queries, and registration. Anything here that turns out *not* to be small is a finding about the
generic layer, and should be recorded as one.

## What this phase will build

**`DataSync.Drivers.Postgres`** on Npgsql:

- **`PostgresDialect`** — `"identifier"` quoting, Npgsql parameter placeholders, and the identity hook
  (`OVERRIDING SYSTEM VALUE`) phase 18 defines.
- **`PostgresDriver : IDriver`** — connection creation from `ConnectionConfig` + resolved credential,
  registering phase 17/18's generic `Watermark`, `BatchReload`, `StagingTable` and `DeleteInsert`
  Kinds with its dialect. No engine-specific reader or writer in this phase.
- **Catalog metadata** — `ListDatabases` over `pg_database`, tables and columns via
  `Generic.InformationSchemaQueries`. Postgres separates database from schema cleanly, so the SPA's
  connection → database → table cascade maps without argument.
- **Typed parameter binding** — the Postgres equivalent of `MsSqlValueBinding`: a column's native type
  to an `NpgsqlDbType`, so segment bounds bind as the column's own type rather than as text. The
  reason this matters is recorded in phase 9: a bound bound as a string makes the engine convert on
  the *column* side, which changes the comparison and prevents an index seek.

**`ConnectionDriverType.Postgres`**, and the SPA's Driver picker enabled — it is currently hardcoded
to `MsSql` and disabled. Capability discovery needs no change: the picker is already driven by
`GET /api/connections/{name}/capabilities`.

**Environment**: a `postgres` service in `docker-compose.yml`, and a `--engine postgres` scenario in
`tools/dev-harness` so a Postgres source or target can be stood up the same way MSSQL is.

## What this phase does not build

`COPY` staging — phase 21, along with the columnar decision. Any CDC/logical-replication reader:
watermark and batch only, per the plan. Oracle, MySQL, ODBC, JDBC.

## How to verify when built

- **`Category=Integration`, the milestone: MSSQL → Postgres and Postgres → MSSQL**, both directions,
  watermark mode, asserting the target matches the source. This is the first time the engine-neutral
  abstraction is doing what it was built for, and it either works or the abstraction is wrong.
- Postgres → Postgres, to isolate driver bugs from cross-engine ones.
- A segmented batch reload against Postgres — list, range and auto — reusing the shape of
  `MsSqlBatchReloadTests`, including that out-of-segment rows are untouched.
- `tools/dev-harness verify` extended to compare across engines, so the harness can prove a
  cross-engine replication the same way it proves an MSSQL one.
- Type round-trip coverage: the types the dev-harness scenario uses at minimum (int, text, decimal,
  timestamp), since type mapping is where cross-engine replication actually breaks.

## Open questions

- **Type mapping across engines is the real risk in this phase**, not the plumbing. `decimal(18,2)` →
  `numeric(18,2)` is easy; `datetime2(3)` → `timestamp(3)`, `nvarchar(max)` → `text`, and anything
  with a timezone are not. Does a mapping fail loudly on a type it cannot carry faithfully, or coerce?
  Failing loudly is the safer default and should be the starting position.
- Whether the target table must already exist. Today every writer assumes it does. Cross-engine makes
  "create the target from the source's shape" much more attractive — and much more dangerous. Out of
  scope here; worth its own planning item if it keeps coming up.

---

# Retrospective

**Cross-engine replication works, both directions.** `CrossEngineReplicationTests` moves rows SQL
Server → PostgreSQL and PostgreSQL → SQL Server through the same pipeline with nothing but the
dialect, catalog and value binder swapped, and compares actual values rather than counts.

The phase asked that anything here which turned out *not* to be small be recorded as a finding about
the generic layer. Two were, and both were real bugs.

## Finding 1: the watermark bound was bound as text

`WatermarkReader` bound the previous watermark with an untyped `AddParameter`. SQL Server converts
`datetime2 > nvarchar` implicitly, so it had always worked; Postgres refuses outright —
`42883: operator does not exist: timestamp without time zone > text`.

It was a latent defect on SQL Server too, for exactly the reason phase 9 recorded about segment
bounds: an untyped bound makes the engine convert on the *column* side of the comparison, which
prevents the index seek the whole watermark strategy depends on. The reader now takes an
`ITableCatalog` and an `ISegmentValueBinder` and binds the bound as the watermark column's own type —
the same treatment segment bounds have had since phase 9. The catalog lookup happens only when there
is a previous watermark, so a first pass pays nothing.

This is the kind of bug a second engine finds and a second test never would.

## Finding 2: the watermark was stored without sub-second precision

`Convert.ToString(DateTime)` uses the general format, which carries no fractional seconds. A watermark
of `09:00:00.500` was persisted as `09:00:00`, so every row in that half-second was re-read on the
next pass — harmless, because writers upsert, and invisible unless someone counted the rows.
`WatermarkValue.Format` now round-trips (`"O"` for temporal values, hex for a rowversion), and
`MsSqlToPostgres_WatermarkModeReadsOnlyWhatIsNew` asserts a `.250` watermark survives the trip through
the work queue's text column.

## What the plumbing actually cost

Beyond those two findings, small — as the phase predicted. `PostgresDriver` registers **nothing of its
own**: every reader, staging provider and writer is `DataSync.Drivers.Generic`'s. The driver is a
dialect, a connection factory, a catalog and a `version()` probe.

Two dialect hooks earned their keep immediately:

- **`UseDatabaseAsync`** — added in phase 17 against the objection that it was speculative. A Postgres
  connection cannot change database; Npgsql's `ChangeDatabase` closes and reopens, silently discarding
  the transaction and any cursor being streamed. `PostgresDialect` validates and throws a message
  naming both databases instead, and a test asserts that.
- **`WriteWithGeneratedColumnOverrideAsync`** — phase 18 made it "run this write" rather than "give me
  a clause". Postgres needed the opposite shape: `OVERRIDING SYSTEM VALUE` is *part of* the INSERT.
  That forced one addition, `SqlDialect.RenderInsertInto`, and the two engines now use one hook each —
  which is the evidence the seam was in the right place rather than the wrong one.

`PostgresCatalog` sets `IsIdentity` only for `GENERATED ALWAYS`: a `BY DEFAULT` identity and an
old-style `serial` accept explicit values without an override, and claiming otherwise emits an
`OVERRIDING SYSTEM VALUE` the server rejects.

## Open question 1: type mapping

Answered in the direction the plan preferred, by not building a mapping layer at all. The staging
table is created from the **target's own** column types, read from its catalog, so a value is only
ever written into the column the operator mapped it to and the target engine decides whether it fits.
Nothing coerces, and nothing invents a type: `numeric(18,2)` vs `decimal(18,2)` never has to be
reconciled because neither side is ever asked to name the other's type.

That means an incompatible mapping fails loudly at apply time with the target engine's own error —
which is the safe default the plan asked for. It also means the failure arrives at write time rather
than at save time, which is worth revisiting when there is a reason to.

`CrossEngineReplicationTests` covers the types the plan named — `int`, `nvarchar`/`text` (including
non-ASCII), `decimal(18,2)` (including a negative and a zero), and `datetime2(3)`/`timestamp(3)` with
a sub-second component — comparing values, not counts.

The dev harness's `verify` needed one genuine accommodation: cross-engine comparison cannot use
`Equals` alone, because the same stored value comes back as a different CLR type from each provider.
`ValuesMatch` compares values rather than boxes.

## Open question 2: creating the target table

Unchanged and out of scope — every writer still assumes the target exists.

## The finding the harness surfaced: there is no generic upsert writer

A Postgres target cannot run the incremental pipeline. The only reconciling writer a non-SQL-Server
target has is `DeleteInsert`, which replaces everything in scope; pairing it with a Change Tracking
reader would delete the target and re-insert only the rows that changed. So `--target-engine postgres`
configures **BatchReload → StagingTable → DeleteInsert** — a full reload every pass.

That is correct and honest for the plan's stated scope ("watermark or batch mode only"), and it is
what the harness now says out loud: `drift` tells the operator this target repairs itself on the next
scheduled run rather than repeating the "an incremental run will not fix this" advice, which is true
of a SQL Server target and false here.

A generic upsert writer — `MERGE`, `ON CONFLICT`, `ON DUPLICATE KEY` behind one Kind — is the obvious
next thing and belongs with phase 21's `COPY` work.

## Verification

- `CrossEngineReplicationTests` — the milestone. MSSQL → Postgres and Postgres → MSSQL reload,
  value-for-value; watermark mode across engines with a sub-second watermark; and a source delete
  reconciled away cross-engine. Components are resolved **from the driver by Kind**, exactly as
  `RunExecutor` resolves them, so the test proves the driver registers them and not merely that the
  components work.
- `PostgresPipelineTests` — 8 tests Postgres → Postgres, isolating driver bugs from cross-engine ones:
  full reload, delete reconciliation, range and list segments, auto-segment tiling, watermark mode,
  `GENERATED ALWAYS` identity, and the change-database rejection.
- **The dev harness, run for real**: `up --target-engine postgres` bootstrapped both databases,
  configured the replication through the API, and replicated 300 seeded rows; a 20/s workload of 195
  inserts, 144 updates and 64 deletes converged to `431 row(s) identical`; `drift` corrupted the
  Postgres target 30 ways and the next scheduled pass repaired it.
- Full .NET suite green: 272 tests across eight projects. Playwright: 13 green. `tsc -b` clean.

## Still not built

`COPY` staging and a generic upsert writer (phase 21). Any logical-replication reader. Oracle, MySQL,
ODBC, JDBC. The harness's *source* is still SQL Server only — Change Tracking is what makes the
incremental story demonstrable, and no other engine in scope has an equivalent yet.
