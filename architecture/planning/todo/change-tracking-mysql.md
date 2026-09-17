# Change tracking — MySQL/MariaDB, native (binlog)

**Status: proposal, not agreed — and blocked, genuinely this time.** There is no MySQL/MariaDB driver.
Refined 2026-09-16: this doc used to also cover the trigger-shadow-table option — that's been extracted
to `change-tracking-mysql-and-mariadb-triggers.md`, since phase 33 already built the entire generic half
of that mechanism and it turned out to need almost no new design work. What's left here — the binlog
protocol client — is the one option that genuinely can't exist without bespoke, engine-specific reader
code, which needs a project (`DbDataSync.Drivers.MySql`) to live in. That project not existing is a real
blocker for *this* option specifically, not a soft one.

Read `change-tracking-strategies.md` first, then `change-tracking-mysql-and-mariadb-triggers.md` — this
doc is the **native, more robust alternative** to that one, for when triggers aren't the right answer:
sub-second latency, or a source where an operator genuinely cannot add DDL. Build the trigger option
first; build this one when — and if — the demand for what it uniquely offers actually shows up.

## Why this is the more robust option, and why it still isn't the default

MySQL's binlog is a **replication protocol stream**, not a `SELECT` — the one mechanism in this whole
change-tracking set that isn't. Every other engine here exposes its change history as a result set; a
binlog reader is a statement builder for nothing and a protocol client for everything:

- no first-party .NET client (unlike Npgsql for Postgres or ODP.NET for Oracle)
- a third-party dependency (`MySqlCdc`, or writing the protocol) carrying a wire format that changes
  between server versions
- a streaming shape that has to be forced into the bounded-window pattern by hand

Against that: no write-path latency penalty on the source (triggers cost every transaction, forever;
binlog reads what the engine was already going to write), true row-level completeness independent of
DDL rights, and — the reason to reach for it at all — genuinely lower latency than a polling trigger
read. The trade is real in both directions, which is why it's a deliberate escalation rather than a
default.

## The mechanism

**Requirements on the source:**

- `binlog_format = ROW` — `STATEMENT` and `MIXED` yield SQL text, not rows, and are useless here
- `binlog_row_image = FULL` — the default. `MINIMAL` gives only changed columns plus the key on an
  UPDATE, which breaks a target that needs whole rows
- `gtid_mode = ON` and `enforce_gtid_consistency = ON` — see below
- a user with `REPLICATION SLAVE` and `REPLICATION CLIENT`

**The watermark is the GTID set**, not the file-and-position pair. `binlog.000042:19483` is meaningless
after a failover to a replica; a GTID set (`3E11FA47-…:1-1004`) survives it and is comparable across
servers. Given that DbDataSync's watermark is already an opaque string, a GTID set stores as-is with no
model change. Anyone reaching for file+position because it is simpler is choosing a position that breaks
on the day the source fails over — which is precisely the day nobody wants a second incident.

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

## MariaDB — a genuine divergence here, unlike the trigger path

Where the trigger-audit mechanism turned out to be identical across both forks (see
`change-tracking-mysql-and-mariadb-triggers.md`'s own compatibility section), binlog is exactly where
MySQL and MariaDB actually diverge: MariaDB's GTID format is different (`domain-server-sequence` rather
than a UUID set), and its binlog events have drifted from MySQL's own. A client that speaks MySQL 8 does
not necessarily speak MariaDB. If MariaDB matters and this option is ever built, it needs its own client
compatibility check, not an assumption that "MySQL support" covers it — the opposite of the trigger case.

## Managed platforms

RDS MySQL, Aurora MySQL and Cloud SQL all expose the binlog to a replication user. Aurora's binlog has
historically had extra latency. No blocker either way — the choice really is about build cost, not
availability.

## Ruled out

- **`information_schema`/`performance_schema` polling.** No row-level change history.
- **Timestamp column watermark.** Already available as the generic `Watermark` reader. No deletes.
- **`ROW_COUNT`/checksum table comparison.** That is the snapshot-diff strategy, which belongs in its own
  document rather than as a MySQL specific.

## Open questions

- **Is a per-pass binlog connection acceptable** at a 15-second interval, or does binlog mode need a
  resident reader — and therefore a different run model? This is the one thing in the whole
  change-tracking set that might genuinely not fit the existing architecture, and it is worth
  establishing before committing to binlog rather than after.
- **Is this option worth building at all before real demand shows up for it?** The trigger path already
  delivers delete-aware change tracking to MySQL/MariaDB with far less risk. Leaning: don't build this
  speculatively — revisit once a concrete case names the latency/DDL-rights trade this exists to answer.

~~**Shadow-table pruning needs a post-write hook.**~~ Resolved by phase 33: `IPositionAcknowledging`
already exists, built for exactly this class of need (its own retrospective names MySQL binlog pruning
as one of the three motivating callers). No longer an open question for this doc specifically — it would
be reused, not re-designed, if this option is ever built.
