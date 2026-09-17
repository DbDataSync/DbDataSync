# Phase 155 — `pgoutput`: logical decoding with nothing installed on the source (planned)

**Status**: Planned, not started. **Conditional on a product decision** — see "Why this might not be
worth building" below; do not start it without that answer.
**Plan reference**: `architecture/planning/done/change-tracking-postgres.md`, which named `pgoutput` as
the fallback, and `architecture/implementation/done/phase-034-postgres-logical-replication.md`, whose
own still-open product question this phase is the other half of.

## Why

Phase 34 shipped PostgreSQL logical decoding through the `wal2json` output plugin. It works, it is
tested against a real server, and it carries one cost the plan underestimated: **`wal2json` is a
third-party shared library that has to be installed on the source database server.**

`pgoutput` is the output plugin PostgreSQL's own native logical replication uses. It **ships with
Postgres** (10+). Same WAL, same replication slots, same decoding — a format that is already there.

The prerequisites split more usefully by *kind* than by count:

| | `wal2json` (phase 34) | `pgoutput` (this phase) |
| --- | --- | --- |
| **Settings** — a restart or reload, no new software | `wal_level = logical`, `max_replication_slots` | the same |
| **SQL objects** — ordinary DDL this tool previews and applies | a replication slot | a slot **and a `PUBLICATION`** |
| **Third-party software on the DB server** | **the plugin package, plus `output_plugin_libraries`** | **none** |

**That bottom row is the whole phase.** It is the only prerequisite that means putting somebody else's
code on a production database, and it is the one an operator is most likely to be unable to satisfy
without a change-control ticket. Phase 34 also found that no well-known public image carries `wal2json`
any more (Debezium's build only `decoderbufs` as of 3.x), which is why this repo now maintains
`docker/postgres-logical/Dockerfile`; and that since PostgreSQL 18.6/17.11/16.15/15.19/14.24 —
CVE-2026-6471 — having it installed is no longer the same as being permitted to use it.

**What this phase does not buy: the restart.** `wal_level = logical` is what makes Postgres write the
information logical decoding needs at all — notably the old tuple for an update or delete, via
`REPLICA IDENTITY`. At the default `wal_level = replica` that data is not in the WAL, so no plugin and
no client-side cleverness can recover it. If the restart alone is what blocks a deployment, neither
plugin helps and `TriggerAudit` is the honest answer.

## Why this might not be worth building

Recorded here so the decision is made once rather than re-argued:

- `wal2json` is **available on every managed Postgres that matters** — RDS and Aurora both list it as a
  supported output plugin, and there it is a parameter-group change rather than an install. A
  deployment on managed Postgres gains almost nothing from this phase.
- `wal2json` is **actively maintained**, not a dying dependency — checked during phase 34: commits
  within the last month, including one adding support for the new `output_plugin_libraries` parameter.
- It is **strictly more work than phase 34 was**, in a shape nothing else in this codebase uses.

**So the question this phase waits on is: is the expected first Postgres user on managed Postgres, or
self-hosted — and if self-hosted, can they get a package installed on the source server?** Managed, or
self-hosted-and-yes: this phase is not needed. Self-hosted-and-no: `wal2json` is an adoption blocker and
this is the answer to it.

## What this builds

**`PgOutputReader`** (Kind `PgOutput`), a **second** reader beside `PgLogicalSlotReader` rather than a
replacement. Both decode the same WAL through the same slots; they differ only in the plugin the slot
was created with, which is a per-slot property.

### The shape, which is the interesting part

`wal2json` is read with a function call on an ordinary connection:

```sql
SELECT lsn, data FROM pg_logical_slot_peek_changes('slot', '0/1A2B3C8', NULL, 'format-version', '2', …)
```

`pgoutput` is a **streaming replication session** — a different connection *mode*:

```csharp
await using var conn = new LogicalReplicationConnection(connectionString);
await conn.Open();
var slot = new PgOutputReplicationSlot("dbdatasync_orders");
await foreach (var message in conn.StartReplication(
    slot, new PgOutputReplicationOptions("dbdatasync_orders_pub", protocolVersion), cancellationToken))
{ … }
```

That is not a query that returns; it is a loop that runs until stopped. Everything below follows from
that one fact.

### The pieces

- **A bounded streaming read.** Fix the window's end with `pg_current_wal_lsn()` up front, exactly as
  phase 34 does, then consume messages until one arrives at or past it and stop. The window's end is
  still computed before anything is read, so the watermark stays safe to persist after the write.
- **Acknowledgement by feedback, not by a function call.** A streaming consumer tells the server how
  far it has durably stored via a standby status update. **Do not send it until the write has committed
  and the watermark has been persisted** — the same rule `IPositionAcknowledging` already states, and
  the same guarantee phase 34's "peek, never get" buys, reached a different way. The slot does not
  advance until the feedback is sent, so an unacknowledged window is re-delivered, which is the
  behaviour phase 34's own `AReadThatIsNotAcknowledged_SeesTheSameChangesAgain` pins.
- **Publication provisioning.** `pgoutput` has no per-read table filter — `wal2json`'s `add-tables`
  has no equivalent. Which tables a session carries is a property of the **publication**
  (`CREATE PUBLICATION … FOR TABLE …`), so the publication becomes a previewed provisioning step
  alongside the slot, and **phase 151 (deprovisioning) gains a third kind of source-side state to
  remove.** Whether that is one publication per mapping (mirroring phase 34's slot-per-table) or one
  per replication with client-side filtering is an open question below.
- **Message mapping.** `pgoutput` sends `Begin`/`Insert`/`Update`/`Delete`/`Commit` messages with
  relation metadata, and Npgsql surfaces column values as typed columns rather than JSON scalars —
  which **removes the conversion layer phase 34 needed** (`PostgresValues`, reading a `timestamp` back
  out of a JSON string). A small quality win, not a reason on its own.

### What it reuses from phase 34, unchanged

Most of the phase, which is the argument for it being a sibling rather than a rewrite:

- Slot creation and drop (`pg_create_logical_replication_slot` takes the plugin as an argument, so the
  same statements serve both), and the slot-name convention and validation.
- Every provisioning check: `wal_level`, `REPLICA IDENTITY`/primary key refusal, the
  `REPLICA IDENTITY FULL` warning, `max_replication_slots`.
- The `PositionExpiredException` handling for a slot dropped or recreated behind a stored position.
- The advance-is-opt-in-and-off decision, and the reasoning behind it — advancement is per-mapping
  because `IPositionAcknowledging` is, so a shared slot advanced by whichever mapping ran last discards
  changes the others have not read.

What it does **not** reuse: the read itself, `PostgresValues`' JSON conversion, and the environment —
`docker/postgres-logical/Dockerfile` and the `output_plugin_libraries` check exist only for `wal2json`.

## Open questions to resolve during implementation

- **`IChangeReader` hands the reader an open `DbConnection`, and this reader cannot use it.** A
  replication session needs its own `LogicalReplicationConnection`, opened in replication mode, which
  cannot run ordinary queries. This is the single biggest design question and it should be answered
  first. Two candidate shapes:
  1. **Derive it from the connection handed in** — read `NpgsqlConnection.ConnectionString` and build a
     replication connection from it. Simplest, and it has a real trap: Npgsql strips the password from
     `ConnectionString` unless `Persist Security Info=true`, so this may not work without a change to
     how the driver builds connections.
  2. **A new opt-in capability interface**, in the shape of `IPositionCapturing` and
     `IPositionAcknowledging`, through which a reader that needs to open its own connection is given
     what it needs to do so. Cleaner, larger, and it is a change to a contract several drivers
     implement.
- **One publication per mapping, or one per replication?** Per mapping mirrors phase 34's
  slot-per-table and keeps the filtering server-side. Per replication means fewer objects but
  client-side filtering of every table's changes, which throws away work the server already did. The
  slot's own answer — per table — was forced by the acknowledgement model; this one is not, so it is a
  genuine choice.
- **Which protocol version.** Npgsql's `PgOutputReplicationOptions` takes one. Version 2+ can stream
  *in-progress* transactions, which this pipeline does not want — a transaction's changes should arrive
  complete, as phase 34's own guarantee states. Either request a version that does not stream, or
  ignore the stream messages; decide which deliberately rather than by default.
- **Whether `PgLogicalSlot` is retired.** If it is, the Dockerfile and the allowlist check go with it
  and the test container returns to a stock image — a real simplification. If both stay, the repo keeps
  maintaining a built image purely so one reader's integration tests can run. Worth deciding rather
  than accumulating.

## How to verify when built

- `Category=Integration` against a **stock** `postgres:17` image with `wal_level=logical` and nothing
  installed — the point of the phase, and the thing that proves it.
- The same behavioural scenarios phase 34's suite already pins, so the two readers are demonstrably
  equivalent: insert/update/delete with the delete carrying only its key; a window that is read but not
  acknowledged being re-delivered; acknowledgement advancing the slot and the default not advancing it;
  a dropped and a recreated slot both raising `PositionExpiredException`; a keyless table refused at
  configuration time.
- A publication planned once and satisfied afterwards, the way phase 34's slot step is.
- `ChangeReaderFirstPassContractTests` updated: a `Declaring` entry naming the integration test that
  proves this reader's declared intents, and a construction recipe.
- Full suite green.
