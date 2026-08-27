# Change tracking — PostgreSQL

**Status: proposal, not agreed.** Read `change-tracking-strategies.md` first — the bounded-window
pattern, the position-expired rule and the peek-versus-consume trap are common to every engine and are
not repeated here.

The Postgres driver exists (phase 20) and registers only generic readers: `Watermark` and
`BatchReload`. This adds the first engine-specific one.

## Recommendation up front

**`PgLogicalSlot` — a reader over `pg_logical_slot_peek_changes` with the `wal2json` output plugin,
using the slot's LSN as the watermark.** Fall back to a trigger shadow table where logical replication
cannot be enabled.

The reason is the strategies doc's first finding: this is a plain SQL function returning rows. It drops
into `IChangeReader` with no new contract, no streaming subsystem and no long-lived process.

## The mechanism

Logical decoding turns the WAL into a stream of row-level changes, via an output plugin.

```sql
SELECT lsn, xid, data
FROM pg_logical_slot_peek_changes('datasync_orders', @uptoLsn, NULL,
                                  'format-version', '2',
                                  'add-tables', 'public.orders');
```

`data` is one JSON object per change with `action` (`I`/`U`/`D`), the table, and the column values.
That maps onto `ChangeRow` almost directly: `action` → `ChangeOperation`, the columns → `Values`
against a `ChangeSchema` built from the mapping's own column list.

### Output plugin: `wal2json`, with `pgoutput` as the alternative

| plugin | shipped with Postgres | shape | notes |
| --- | --- | --- | --- |
| `test_decoding` | yes | text | Documented as **not a stable format for machine consumption**. Rule out. |
| `pgoutput` | yes (10+) | binary protocol | The built-in logical replication protocol. Npgsql speaks it via `LogicalReplicationConnection`. Streaming, not a `SELECT`. |
| `wal2json` | no — an extension | JSON | A plain `SELECT`. Available on RDS, Aurora and Cloud SQL. |

**Start with `wal2json`** because it keeps the reader a query and therefore keeps it inside the
existing contract. The cost is a required extension, which is real for a self-hosted instance the
operator does not control — but it *is* available on the three big managed platforms, which is where
the objection would otherwise bite hardest.

`pgoutput` via `Npgsql.Replication` is the fallback and is strictly more work: it is a streaming
connection, so the bounded window has to be enforced by stopping at a target LSN rather than by the
query doing it. Worth having eventually, because it needs no extension at all. Not first.

## Peek, do not get — this is the critical one

`pg_logical_slot_get_changes` **consumes**. Once read, those changes are gone from the slot.

DataSync persists a watermark only after the write succeeds. A consuming read breaks that invariant
outright: the slot advances at read time, the write then fails, and the changes are unrecoverable —
silent data loss, and the kind that only shows up as a target that quietly disagrees with its source.

So:

1. `SELECT pg_current_wal_lsn()` — fix the window's end
2. `pg_logical_slot_peek_changes(slot, <that lsn>, NULL, …)` — read without consuming
3. stage, write, commit
4. **only then** `SELECT pg_replication_slot_advance('datasync_orders', <that lsn>)`

Step 4 does not fit `IChangeReader`, which has no "the write succeeded" callback. Options:

- extend the reader contract with an optional `IPositionAcknowledging` interface that `RunExecutor`
  calls after a successful write — the same opt-in-by-interface shape as `ISegmentExpandingReader` and
  `IConnectionTester`
- or let the *next* pass advance the slot to the previous pass's watermark before reading, which needs
  no new contract at all and self-heals, at the cost of the slot always lagging one pass behind

The second is cheaper and needs no interface change; the first is more obviously correct and releases
WAL sooner. Given that WAL retention is the dangerous failure mode here, **the first is probably worth
the contract change** — but it is a genuine decision and not one to make in passing.

## What the source has to have

- `wal_level = logical` — **requires a restart**, and is the single biggest adoption obstacle
- `max_replication_slots` with room to spare
- a role with `REPLICATION`, or `rds_replication` on RDS
- `REPLICA IDENTITY` on each tracked table. The default (`DEFAULT`) puts only the primary key in the
  delete and update-old record, which is **exactly what DataSync needs** — deletes are keyed and the
  writers match on the primary key. `FULL` is only needed if a transform wants pre-update values, and
  it makes the WAL substantially larger. Default is right; say so, so nobody turns on `FULL` reflexively.
- a table with no primary key and `REPLICA IDENTITY DEFAULT` produces **no delete records at all**.
  That has to be detected at config time and refused, not discovered as a target that never loses rows.

## The slot is server-side state we create — and can kill the source with

Nothing in DataSync has ever created persistent state on a source before. A slot that nobody consumes
pins WAL forever and fills the source's disk. That is the failure mode where this tool takes a
production database down.

What it needs:

- **Lifecycle tied to the replication.** Created when the reader is first configured
  (`pg_create_logical_replication_slot('datasync_orders','wal2json')`), dropped when the replication or
  mapping is deleted. Deleting a replication must not silently leave a slot behind.
- **A lag reading, surfaced.** `pg_replication_slots.confirmed_flush_lsn` against `pg_current_wal_lsn()`
  gives retained bytes. The phase 19 connection card is the natural home; an operator needs to see this
  before the disk fills, not after.
- **An orphan check.** Slots named `datasync_%` with no matching config are a real, findable condition
  and worth reporting rather than leaving for someone to trip over.
- **Naming.** One slot per what — connection, replication, or mapping? A slot decodes the whole
  database and filters by table, so one slot per *replication* is the natural unit: fewer slots, one
  retention point. Per-mapping would multiply slots for no gain. Worth confirming.

## Position expired

A slot cannot fall behind its own WAL — Postgres retains it, which is the whole problem above. So the
expired-position case here is narrower than elsewhere: it happens when the **slot is dropped** and
recreated, at which point the stored LSN is meaningless.

Detect it (`pg_replication_slots` has no row for the slot, or `restart_lsn` is ahead of the stored
watermark) and raise the shared `PositionExpiredException` from the strategies doc, so it reads the
same as SQL Server's already does and can be recovered the same way.

## Delivery guarantees

Logical decoding is **at-least-once**: after a crash a change can be re-delivered. DataSync's writers
upsert, and its reconciling writers replace a scope wholesale, so a duplicate is harmless. Worth
stating explicitly in the doc for the reader, because the instinct is to build deduplication that is
not needed here.

Transactions are delivered **in commit order and complete** — a transaction's changes never straddle a
peek boundary. That is a stronger guarantee than the watermark reader has, and it means a bounded
window never leaves half a transaction applied.

## The trigger fallback — `PgTriggerAudit`

For instances where `wal_level` cannot be changed, which is common on managed platforms the operator
does not own:

```sql
CREATE TABLE datasync_changes_orders (seq bigserial primary key, op char(1), key jsonb, changed_at timestamptz default now());
CREATE FUNCTION ... RETURNS trigger AS $$ ... $$ LANGUAGE plpgsql;
CREATE TRIGGER ... AFTER INSERT OR UPDATE OR DELETE ON orders FOR EACH ROW EXECUTE FUNCTION ...;
```

The reader then joins the shadow table's keys back to the base table for current values, and emits
deletes from the shadow table alone — which is exactly the shape `MsSqlChangeTrackingReader` already
has, including its hard-won lesson from phase 12 that the key must come from the change record and
never from `base.*`.

`seq` is the watermark. Costs: DDL rights, a write-path penalty, and someone has to prune the shadow
table. Needs no server configuration and no restart, which is why it is worth building at all.

## Ruled out

- **`xmin`.** The system column is a transaction id, wraps around, and is not monotonic across a
  wraparound. It also cannot see deletes. It looks like a free watermark and is a trap.
- **`pg_stat` / `pgaudit`.** Statement-level, not row-level.
- **`pglogical`.** A third-party extension that predates built-in logical replication. No advantage here.

## Open questions

- **Advance-after-write versus advance-next-pass**, above. The one real decision in this document.
- **One slot per replication, or per mapping.** Leaning per replication.
- **DDL.** A column added to a tracked table changes what `wal2json` emits mid-stream. Postgres does
  not decode DDL itself, so the reader sees new columns appear without warning. Probably: detect a
  shape change against the mapping's columns and fail loudly rather than silently dropping the column.
- **Is `wal2json` an acceptable prerequisite**, or does `pgoutput` have to come first? This is a
  product question about who the first Postgres CDC user is, not a technical one.
