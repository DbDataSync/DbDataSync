# Phase 18 — PostgreSQL Driver (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/additional-database-drivers.md`; depends on phases 16
and 17.

## Why Postgres first

Best-documented ADO.NET provider of the five (Npgsql), a genuinely different bulk path to exercise
later (`COPY`), and it is the first engine that makes cross-engine replication real. It is also the
sink `planning/todo/columnar-change-batches.md` has been waiting on — though that question belongs to
phase 19, not this one.

If phases 16 and 17 did their job, this phase is small: a dialect, a connection factory, catalog
queries, and registration. Anything here that turns out *not* to be small is a finding about the
generic layer, and should be recorded as one.

## What this phase will build

**`DataSync.Drivers.Postgres`** on Npgsql:

- **`PostgresDialect`** — `"identifier"` quoting, Npgsql parameter placeholders, and the identity hook
  (`OVERRIDING SYSTEM VALUE`) phase 17 defines.
- **`PostgresDriver : IDriver`** — connection creation from `ConnectionConfig` + resolved credential,
  registering phase 16/17's generic `Watermark`, `BatchReload`, `StagingTable` and `DeleteInsert`
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

`COPY` staging — phase 19, along with the columnar decision. Any CDC/logical-replication reader:
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
