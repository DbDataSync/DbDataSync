# Phase 34 — PostgreSQL logical replication (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/change-tracking-postgres.md`. Depends on phase 32 for
`PositionExpiredException` and on phase 33 for `IPositionAcknowledging`, both of which this phase needs
and neither of which it should invent.

## What this builds

**`PgLogicalSlot`** — a reader over `pg_logical_slot_peek_changes` with the `wal2json` output plugin,
using the slot's LSN as the watermark.

The reason this is a reader and not a subsystem is the first finding in
`change-tracking-strategies.md`: it is a **plain SQL function returning rows**.

```sql
SELECT lsn, xid, data
FROM pg_logical_slot_peek_changes('dbdatasync_orders', :uptoLsn, NULL,
                                  'format-version', '2', 'add-tables', 'public.orders');
```

`data` is one JSON object per change with an action (`I`/`U`/`D`), the table and the column values —
which maps onto `ChangeRow` almost directly. No streaming subsystem, no long-lived process, no change
to `IChangeReader`, and the LSN stores as a string like every other position.

## Peek, never get — this is the whole safety argument

`pg_logical_slot_get_changes` **consumes**. Once read, those changes are gone from the slot forever.

DbDataSync persists a watermark only after the write succeeds. A consuming read breaks that invariant
outright: the slot advances at read time, the write then fails, and the changes are unrecoverable —
silent data loss, of the kind that shows up only as a target that quietly disagrees with its source.

So:

1. `SELECT pg_current_wal_lsn()` — fix the window's end
2. `pg_logical_slot_peek_changes(slot, <that lsn>, NULL, …)` — read without consuming
3. stage, write, commit, persist the watermark
4. **then** `pg_replication_slot_advance('dbdatasync_orders', <that lsn>)`

Step 4 is `IPositionAcknowledging`, which phase 33 builds for shadow-table pruning. This phase is the
second caller and the one that makes the interface's ordering guarantee load-bearing: acknowledge
**after** the watermark is persisted, never before.

## The slot is server-side state we create, and it can fill the source's disk

Nothing in DbDataSync has ever created persistent state on a source before. A slot nobody consumes pins
WAL indefinitely. This is the failure mode where **this tool takes a production database down**, and it
deserves more than a comment.

- **Lifecycle tied to the replication.** Created through phase 25's provisioning flow — previewable,
  applied deliberately — and **dropped when the replication or mapping is deleted**. Deleting a
  replication must not silently leave a slot behind.
- **Lag surfaced.** `pg_replication_slots.confirmed_flush_lsn` against `pg_current_wal_lsn()` gives
  retained bytes. The phase 19 connection card is the natural home; an operator needs to see this
  before the disk fills, not after.
- **Orphan detection.** Slots named `dbdatasync_%` with no matching config are findable and worth
  reporting rather than leaving to be tripped over.
- **One slot per replication**, not per mapping: a slot decodes the whole database and filters by
  table, so per-mapping multiplies slots for no gain.

## `wal2json`, with `pgoutput` as the fallback

| plugin | ships with Postgres | shape |
| --- | --- | --- |
| `test_decoding` | yes | text, **documented as not for machine consumption** — ruled out |
| `pgoutput` | yes (10+) | binary replication protocol; Npgsql speaks it, but streaming |
| `wal2json` | no — an extension | JSON, from a plain `SELECT` |

Start with `wal2json` because it keeps the reader a query and therefore inside the existing contract.
It is available on RDS, Aurora and Cloud SQL, which is where the "requires an extension" objection
would otherwise bite hardest.

**The dev container needs it.** `postgres:17-alpine` has no `wal2json`, so `docker-compose.yml` needs
either an image that carries it or a small build step — and `wal_level = logical` needs a command-line
argument and a restart. That is a real environment change and it is part of this phase, not a
precondition someone discovers.

`pgoutput` via `Npgsql.Replication` is the fallback and is strictly more work: streaming, so the
bounded window is enforced by stopping at a target LSN rather than by the query doing it. Worth having
because it needs no extension. Not first.

## What the source has to have

- `wal_level = logical` — **requires a restart**, and is the single biggest adoption obstacle
- a role with `REPLICATION` (or `rds_replication`)
- `REPLICA IDENTITY` — the **default** is right. It puts the primary key in the delete and update-old
  record, which is exactly what DbDataSync needs, and `FULL` only matters if a transform wants
  pre-update values while making the WAL substantially larger. Say so, so nobody enables `FULL`
  reflexively.
- **A table with no primary key and `REPLICA IDENTITY DEFAULT` produces no delete records at all.**
  Detect at configuration time and refuse; discovering it as a target that never loses rows is the
  worst possible way to find out.

## Delivery guarantees, and what not to build

Logical decoding is **at-least-once**: after a crash a change can be re-delivered. DbDataSync's writers
upsert and its reconciling writers replace a scope wholesale, so a duplicate is harmless. Worth stating
because the instinct is to build deduplication that is not needed.

Transactions arrive **in commit order and complete** — a transaction's changes never straddle a peek
boundary. That is a stronger guarantee than the watermark reader has.

## Position expired

A slot cannot fall behind its own WAL, so the expired case is narrower here than elsewhere: it happens
when the **slot is dropped and recreated**, leaving the stored LSN meaningless. Detect it (no row in
`pg_replication_slots`, or `restart_lsn` ahead of the stored watermark) and raise phase 32's
`PositionExpiredException`, so it reads the same as SQL Server's and recovers the same way.

## What this phase does not build

`pgoutput`. The trigger fallback — phase 33 covers it generically and Postgres's trigger DDL belongs
there. Any change to the Postgres driver's batch or watermark paths.

## How to verify when built

- `Category=Integration` against a `wal_level=logical` container: insert, update and delete reaching
  the target, including a delete carrying only its key.
- **Peek, not get**: read a window, fail the write deliberately, read again, and get the same changes.
  This is the test that proves the data-loss path is closed, and it should exist before the reader is
  trusted.
- Acknowledge advances the slot; a failed write does not.
- Slot dropped out from under a stored LSN raising `PositionExpiredException`.
- A table with no primary key refused at configuration time.
- Slot lag reported on the connection card.
- `tools/dev-harness` able to stand the environment up, since the compose change is part of this.

## Open questions

- **Is `wal2json` an acceptable prerequisite**, or must `pgoutput` come first? A product question about
  who the first user is, not a technical one.
- **DDL.** Postgres does not decode DDL, so a column added to a tracked table changes what `wal2json`
  emits with no warning. Probably: detect a shape change against the mapping's columns and fail loudly
  rather than silently dropping the column.
