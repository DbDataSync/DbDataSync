# Change tracking — MySQL / MariaDB

**Status: proposal, not agreed — and blocked.** There is no MySQL / MariaDB driver. This cannot become an
implementation phase until one exists; the driver is tracked in
`planning/done/additional-database-drivers.md`'s table, where it is still marked *not yet written*.
No MySQL driver phase has been written either; this doc argues triggers should come before the binlog, which would make phase 33 most of the answer once a driver exists.

Left in `todo/` deliberately rather than turned into a phase that could not be built. The
reasoning below stands and should be picked up when the driver lands. Read `change-tracking-strategies.md` first. No MySQL driver exists
yet (`additional-database-drivers.md` puts it around phase 23).

MySQL is the one engine in scope where the good mechanism is **not** a query, and that changes the
recommendation.

## Recommendation up front

**Build the trigger shadow table first. Build the binlog reader second, if the demand is there.**

This inverts the usual advice, and the reason is the strategies doc's first finding. Every other engine
here exposes its change history as a `SELECT`, so a CDC reader is a statement builder and a result
mapping — days of work against a contract that already fits. MySQL's binlog is a **replication protocol
stream**, with:

- no first-party .NET client (unlike Npgsql for Postgres or ODP.NET for Oracle)
- a third-party dependency (`MySqlCdc`, or writing the protocol) carrying a wire format that changes
  between server versions
- a streaming shape that has to be forced into the bounded-window pattern by hand

Meanwhile MySQL's triggers are mature, need no server configuration, no restart, no replication
privileges, and produce a shadow table that is read with an ordinary `SELECT` — the shape everything
else already uses. It is a fraction of the work for most of the value.

That is a cost/benefit call rather than a technical impossibility, and it should be made explicitly
rather than by defaulting to "CDC means binlog".

## Option 1 — trigger shadow table (`MySqlTriggerAudit`)

```sql
CREATE TABLE datasync_changes_orders (
  seq        BIGINT AUTO_INCREMENT PRIMARY KEY,
  op         CHAR(1) NOT NULL,
  id         INT NOT NULL,              -- the tracked table's key, one column per key column
  changed_at TIMESTAMP(6) DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB;

CREATE TRIGGER orders_ai AFTER INSERT ON orders FOR EACH ROW
  INSERT INTO datasync_changes_orders (op, id) VALUES ('I', NEW.id);
-- …and _au / _ad for UPDATE and DELETE
```

The reader then reads the shadow table above the stored `seq`, collapses to one row per key (latest
operation wins), and joins back to the base table for current values — emitting deletes from the shadow
row alone.

**`seq` is the watermark**, and `AUTO_INCREMENT` gives it monotonicity for free.

Two traps worth writing into the implementation from the start, both already learned once:

- **The key must come from the shadow row, never from the joined base row.** Phase 12 found exactly
  this bug in the SQL Server reader: `base.*` overwrote the change record's key, so a row deleted since
  capture had its key nulled — destroying the one value still reliable. The same LEFT JOIN shape has
  the same trap here.
- **Collapse to net change per key.** SQL Server's `CHANGETABLE` does this natively (one net row per
  key); a shadow table does not, so a row updated 50 times yields 50 shadow rows. Collapsing in the
  reader keeps the staged set proportional to *changed rows* rather than to write volume.

Costs: DDL rights on the source, a write-path latency penalty on every transaction touching a tracked
table, and shadow-table pruning — which needs an owner. Pruning to below the persisted watermark is
safe and is the obvious place for it, but the watermark lives in DataSync's state store, not the
source, so something has to carry it back. Probably a `CleanupAsync`-style call after a successful
write, which is a contract question the Postgres slot-advance decision also raises. **They are the same
question and should be answered once.**

## Option 2 — binlog

The complete answer, and the one to build when someone actually needs sub-second latency or cannot put
triggers on the source.

**Requirements on the source:**

- `binlog_format = ROW` — `STATEMENT` and `MIXED` yield SQL text, not rows, and are useless here
- `binlog_row_image = FULL` — the default. `MINIMAL` gives only changed columns plus the key on an
  UPDATE, which breaks a target that needs whole rows
- `gtid_mode = ON` and `enforce_gtid_consistency = ON` — see below
- a user with `REPLICATION SLAVE` and `REPLICATION CLIENT`

**The watermark is the GTID set**, not the file-and-position pair. `binlog.000042:19483` is meaningless
after a failover to a replica; a GTID set (`3E11FA47-…:1-1004`) survives it and is comparable across
servers. Given that DataSync's watermark is already an opaque string, a GTID set stores as-is with no
model change. Anyone reaching for file+position because it is simpler is choosing a position that
breaks on the day the source fails over — which is precisely the day nobody wants a second incident.

**Forcing a stream into a bounded window:**

1. `SELECT @@GLOBAL.gtid_executed` — fix the window's end
2. connect as a replica from the stored GTID set, stream events
3. stop when the received GTID set covers the end captured in step 1, disconnect
4. return that set as the new watermark

Awkward but workable, and it preserves the terminating-read property `IChangeReader` requires. It also
means one TCP connection per pass, which for a 15-second schedule is a lot of connection churn — worth
measuring before assuming it is fine.

**Retention** is `binlog_expire_logs_seconds` (or `expire_logs_days` pre-8.0). A stored GTID set that
has been purged raises the position-expired case; `@@GLOBAL.gtid_purged` is the check, and it maps onto
the shared `PositionExpiredException`.

**Client risk.** `MySqlCdc` is the realistic .NET option and it is a single-maintainer project. That is
not disqualifying — but taking a wire-protocol implementation as a dependency for a data-integrity path
deserves to be a conscious decision, with a look at its issue tracker and its version coverage, not a
line item.

## MariaDB

Diverges from MySQL 8 in ways that matter here: its GTID format is different (`domain-server-sequence`
rather than a UUID set), and its binlog events have drifted. A client that speaks MySQL 8 does not
necessarily speak MariaDB. If MariaDB matters, the trigger option covers both with one implementation —
another point in its favour.

## Managed platforms

RDS MySQL, Aurora MySQL and Cloud SQL all expose the binlog to a replication user, and all allow
triggers. Aurora's binlog has historically had extra latency. No blocker either way — which means the
choice really is about build cost, not availability.

## Ruled out

- **`information_schema` / `performance_schema` polling.** No row-level change history.
- **Timestamp column watermark.** Already available as the generic `Watermark` reader. No deletes.
- **`ROW_COUNT`/checksum table comparison.** That is the snapshot-diff strategy, which belongs in its
  own document rather than as a MySQL specific.

## Open questions

- **Trigger first or binlog first?** Stated as a recommendation above, but it is a product decision
  about who the first MySQL user is and what they need.
- **Shadow-table pruning needs a post-write hook**, which is the same contract question as Postgres's
  slot advance. Answer once, for both.
- **Is a per-pass binlog connection acceptable** at a 15-second interval, or does binlog mode need a
  resident reader — and therefore a different run model? This is the one thing in the whole
  change-tracking set that might genuinely not fit the existing architecture, and it is worth
  establishing before committing to binlog rather than after.
