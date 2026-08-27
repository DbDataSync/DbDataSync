# Change tracking — the strategies, and how they meet `IChangeReader`

**Status: proposal, not agreed.** The umbrella for the per-engine documents
(`change-tracking-postgres.md`, `change-tracking-mysql.md`, `change-tracking-oracle.md`,
`change-tracking-mssql-cdc.md`, `change-tracking-odbc-jdbc.md`). Read this
one first: everything below is common to all of them, and repeating it five times would guarantee the
five copies drift.

Scope is the engines named in `planning/done/additional-database-drivers.md` — Oracle, PostgreSQL,
MySQL, ODBC, JDBC — plus SQL Server, which already has Change Tracking and is the reference
implementation everything else is measured against.

## Three findings that shape all of it

### 1. Most change-tracking mechanisms are, at the point of consumption, a query

This is the important one and it is not obvious. The mechanisms differ enormously in *setup* — what has
to be enabled, what privileges, what the source has to retain — but at read time the majority are a
SQL result set:

| engine | how you read the changes |
| --- | --- |
| SQL Server CT | `SELECT … FROM CHANGETABLE(CHANGES t, @since)` |
| SQL Server CDC | `SELECT … FROM cdc.fn_cdc_get_all_changes_x(@from, @to, 'all')` |
| PostgreSQL logical | `SELECT … FROM pg_logical_slot_peek_changes(slot, @upto, NULL, …)` |
| Oracle LogMiner | `SELECT … FROM V$LOGMNR_CONTENTS` after `DBMS_LOGMNR.START_LOGMNR` |
| Oracle Flashback | `SELECT …, VERSIONS_OPERATION FROM t VERSIONS BETWEEN SCN a AND b` |
| trigger shadow table | `SELECT … FROM t_changes WHERE seq > @since` |
| MySQL binlog | **not a query** — a replication protocol stream |

Six of seven are `SELECT`s. That means they fit `IChangeReader` as it stands, and it means the
**scripted change query** (`script-generated-change-queries.md`) is not a lesser fallback for ODBC and
JDBC — it is the same mechanism with the SQL supplied by the operator instead of by us. Reaching
PostgreSQL *through* JDBC, `pg_logical_slot_peek_changes` is still just SQL.

MySQL is the exception and the per-engine doc says so plainly.

### 2. The watermark is already the right abstraction for a log position

`ReadResult.NewWatermark` is an opaque string that the work queue persists **only on a successful
write**. Every position type in scope is a string:

| engine | position | example |
| --- | --- | --- |
| SQL Server CT | version | `4211` |
| SQL Server CDC | LSN (binary(10)) | `0x0000002A000001BC0003` |
| PostgreSQL | LSN | `0/16B2C50` |
| MySQL | GTID set | `3E11FA47-…:1-1004` |
| Oracle | SCN | `10293847` |

**Nothing in the state store or the run model has to change to support log-based readers.** That is a
large saving and it should be stated up front, because the instinct on hearing "CDC" is to assume a new
streaming subsystem.

### 3. Two opposite failure modes, and one of them can take down the source

Every log-based mechanism needs the source to retain history between passes. That creates a pair of
failures that are each other's mirror image:

- **The source kept too much.** A PostgreSQL replication slot that is never consumed pins WAL
  indefinitely and fills the source's disk. This is the failure mode where *DataSync takes a production
  database down*, and it is the single most dangerous thing in this whole area. An orphaned slot left
  behind by a deleted replication is the obvious way to get there.
- **The source discarded what we needed.** Binlog expiry, undo retention, archived logs removed, or
  SQL Server CT's retention window passing. The stored watermark no longer exists in the source's
  history and the reader cannot resume.

The second already has a precedent worth copying exactly. `MsSqlChangeTrackingReader` checks
`CHANGE_TRACKING_MIN_VALID_VERSION` before reading and throws:

> Change Tracking history for 'dbo.Orders' no longer covers watermark '41' (minimum valid version is
> 68). A full resync is required — clear the stored watermark for this table.

Every log-based reader needs the same check, and doing it five more times by hand is the argument for
hoisting it: a `PositionExpiredException` in the abstractions, thrown by any reader, caught by
`RunExecutor`, surfaced as a distinct run status rather than a generic failure. There is a real gap
even in the MSSQL case — it *fails* correctly, but nothing offers the operator the reload that fixes
it. Given the work queue and the backfill machinery both exist, "enqueue a reload" is a small step from
"tell the operator to".

The first failure mode has no precedent because nothing in DataSync has ever created server-side state
on a source. It needs: explicit slot lifecycle tied to the replication's lifecycle, a slot-lag reading
surfaced next to the connection (the phase 19 reachability card is the natural home), and a refusal to
delete a replication silently while its slot survives.

## The strategies

**Log-based.** Read the engine's own redo/WAL. Complete — sees every change including deletes, and sees
them in commit order. Low overhead on the source's write path. Costs: elevated privileges, source
configuration that usually needs a restart, retention management, and DDL is hard (a column added
mid-stream changes the shape of what the log yields).

**Engine-maintained change tables.** SQL Server CT and CDC. The engine does the log harvesting and hands
you a table. All the completeness of log-based with none of the protocol work, but only SQL Server
offers it among the engines in scope.

**Trigger-based shadow tables.** We create `AFTER INSERT/UPDATE/DELETE` triggers writing key +
operation + sequence into a side table. Works on every engine here, needs no server configuration and
no restart. Costs: DDL rights on the source, a write-path latency penalty on every transaction, and a
shadow table someone has to vacuum. Underrated — it is often the only option on a managed instance
where the operator cannot change server parameters, and it is far less work to build than a binlog
client.

**Watermark.** Already built and already generic. No deletes, and the tool says so
(`IChangeReader.DetectsDeletes`, surfaced in the SPA since phase 17). Right for append-only and
append/update-only tables, which are common.

**Snapshot diff.** Read the source's keys and a hash of each row, compare against the target, emit the
difference. Detects deletes with *no* source cooperation at all — no privileges, no DDL, no
configuration. Costs a full scan of both sides. Worth a document of its own eventually: DataSync
already has the reconciling reload, and this is a cheaper version of it that moves only what changed.
It is the honest answer for a source we are given read-only `SELECT` on and nothing else.

**External CDC platforms** (Debezium and friends). Out of scope — but worth naming, because the
question "why not just point Debezium at it" will be asked, and the answer is that it moves the problem
to Kafka and a second operational surface rather than removing it.

## Reconciling a log with a pull

`ReadChangesAsync` is a **pull** that must terminate, returning a bounded set of rows and one new
position. A log is unbounded and, for MySQL, arrives as a stream.

The pattern that fits, and which every per-engine doc below uses, is the **bounded window**:

1. ask the source for its current end position (`pg_current_wal_lsn()`,
   `CHANGE_TRACKING_CURRENT_VERSION()`, `SELECT @@gtid_executed`, `SELECT CURRENT_SCN`)
2. read changes from the stored watermark **up to that end**, and no further
3. yield them, return the end position as the new watermark

This is exactly what `MsSqlChangeTrackingReader` already does with `@previousVersion` and
`@targetVersion`, and it is why that reader is the template rather than a special case. It gives a
terminating read, a position that means something, and — because the window's end was fixed before the
read began — a result that does not grow while it is being consumed.

### The consuming-read trap

For any mechanism where reading *advances* the source's position, the bounded window is not enough.
PostgreSQL is the sharp case: `pg_logical_slot_get_changes` consumes, and once consumed those changes
are gone from the slot forever.

DataSync persists a watermark **only after the write succeeds**. A consuming read breaks that
invariant: the slot advances at read time, the write then fails, and the changes are unrecoverable.

So the rule is: **read non-destructively, advance only after the write commits.** PostgreSQL has
`pg_logical_slot_peek_changes` for exactly this, and `pg_replication_slot_advance` to move the slot
afterwards. Any engine without a peek equivalent cannot be used log-based safely without a different
guarantee. This deserves to be checked first for each mechanism, not discovered later.

## What each reader has to declare

Because capability discovery drives the UI and phases 17–19 made a point of never inferring behaviour
from a Kind string:

- **`DetectsDeletes`** — true for every log-based and trigger-based reader here, false for watermark.
- **`ISegmentExpandingReader`** — generally *not* implemented by a CDC reader. Segments describe a
  range of a table; a log position describes a point in time. A CDC reader has no meaningful answer to
  "read bucket 3 of 4", and should not pretend to. Reloads stay the reload readers' job.
- **A retention/lag reading**, where the engine can give one. New surface, but the operator needs it
  before the disk fills, not after.

## Suggested order

Not the order of engine popularity — the order of what each one teaches:

1. **`change-tracking-mssql-cdc.md`** — same engine, same driver, an existing reader to compare
   against. Shakes out `PositionExpiredException` and the bounded-window shape with no new provider.
2. **`change-tracking-postgres.md`** — the driver exists (phase 20), and it forces the slot-lifecycle
   and peek-versus-consume questions, which are the genuinely new ones.
3. **`script-generated-change-queries.md`** — once two real readers exist, the scripted shape can be
   generalised from them rather than guessed at, and ODBC/JDBC get an answer without waiting for their
   drivers.
4. **`change-tracking-oracle.md`** — Flashback Version Query first, LogMiner later.
5. **`change-tracking-mysql.md`** — last, because it is the only one needing a protocol client, and
   triggers may well be the right answer instead.
