# Change tracking — Oracle, native (Flashback / LogMiner)

**Status: partially resolved 2026-09-17.** Flashback Version Query (Option 1 below) is built and shipped
— see `architecture/implementation/done/phase-148-oracle-driver-trigger-audit-and-flashback.md`, which
built it alongside the trigger-audit option and the rest of the Oracle driver in one phase, answering
this doc's own "should Flashback ship alongside the trigger option" question with yes. Two real
divergences from this doc's own assumptions, found by building it: `SELECT CURRENT_SCN FROM V$DATABASE`
needs a dictionary-view-class grant an ordinary app user lacks, so the built reader uses
`DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER()` instead (its own, smaller, but still real grant); and
`FLASHBACK`/`FLASHBACK ANY TABLE` turned out to be needed only when the connecting user does not own the
table being read, not unconditionally as stated below. LogMiner (Option 2) remains genuinely open and out
of that phase's scope — this doc stays `todo/` for LogMiner specifically. Refined 2026-09-16: this doc
used to also cover the trigger-based option — that's been extracted to `change-tracking-oracle-triggers.md`
(now `done/`, see phase 148), since phase 33 already built the entire generic half of that mechanism and
it turned out to need almost no new design work.

Read `change-tracking-strategies.md` first, then `change-tracking-oracle-triggers.md` — this doc covers
the **more robust native alternatives** to that one. Unlike the MySQL case, one of the two options here
(Flashback) is cheap enough to arguably build *alongside* the trigger option rather than strictly after
it — see the recommendation below.

Oracle has more change-tracking options than any other engine here, and they differ by orders of
magnitude in cost and privilege. Picking the right first one still matters more here than anywhere else.

## Recommendation up front

**Flashback Version Query is nearly as cheap as the trigger option, and needs none of its costs.**
Unlike the MySQL binlog case, this isn't "build the cheap thing first, escalate only if demand shows up"
— Flashback needs no DDL on the source, no write-path latency penalty, and no shadow-table pruning at
all, trading those for a bounded lookback window (undo retention, typically hours) that a 15-second
polling replication comfortably fits inside. **LogMiner stays the last resort** — DBA-level grants,
CPU cost, and real implementation work (see below), worth it only when Flashback's lookback genuinely
isn't enough.

## Option 1 — Flashback Version Query (recommended)

```sql
SELECT VERSIONS_OPERATION, VERSIONS_STARTSCN, VERSIONS_XID, t.*
FROM   app.orders VERSIONS BETWEEN SCN :fromScn AND :toScn t
WHERE  VERSIONS_OPERATION IS NOT NULL
ORDER  BY VERSIONS_STARTSCN;
```

- `VERSIONS_OPERATION` is `I`, `U` or `D` — a direct `ChangeOperation` mapping, and one of the few
  mechanisms that hands us the operation without inference.
- `VERSIONS_STARTSCN` is the position. `SELECT CURRENT_SCN FROM V$DATABASE` (or
  `DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER`, which needs a smaller grant) fixes the window's end, giving
  the bounded window the strategies doc describes.
- It is **non-consuming** by construction, so the peek-versus-consume trap does not apply and the
  watermark-on-success-only rule holds with no contract change — a genuine simplification against
  Postgres, and against needing `IPositionAcknowledging` at all (unlike both trigger-audit options and
  the binlog case, there's nothing here to prune or advance after the fact).
- A deleted row's version carries its column values as they were, so a delete arrives keyed — which is
  all the writers need.

**Constraints:**

- Bounded by `UNDO_RETENTION` and actual undo tablespace size. Exceeding it raises `ORA-01555` (snapshot
  too old) or `ORA-30052` (invalid lower limit snapshot expression) — the position-expired case, mapping
  onto the shared `PositionExpiredException`.
- Does not survive DDL on the table. A column added mid-window makes the query fail rather than lie,
  which is the right failure.
- `FLASHBACK` privilege on the table (or `FLASHBACK ANY TABLE`) plus `SELECT` — real, but nothing like
  LogMiner's grant list, and nothing at all like the trigger option's DDL rights, worth naming plainly at
  the point an operator picks a mechanism, the same posture `TriggerAuditPlan.Costs` already takes for
  its own option.
- Row movement: a table with `ENABLE ROW MOVEMENT` and partition maintenance can produce version rows
  that are physical relocations rather than logical changes. Worth checking, not blocking.

For a polling replication this is a genuinely good fit and it is by far the cheapest *native* thing on
this list to build: a statement builder plus an operation-column mapping, which the generic pipeline
almost already has.

## Option 2 — LogMiner

The industrial answer, and the one to reach for when undo retention is genuinely too short or the source
cannot grant `FLASHBACK`.

```sql
BEGIN DBMS_LOGMNR.START_LOGMNR(
  STARTSCN => :fromScn, ENDSCN => :toScn,
  OPTIONS  => DBMS_LOGMNR.DICT_FROM_ONLINE_CATALOG
            + DBMS_LOGMNR.COMMITTED_DATA_ONLY
            + DBMS_LOGMNR.CONTINUOUS_MINE);   -- CONTINUOUS_MINE removed in 19c
END;

SELECT SCN, OPERATION, SEG_OWNER, TABLE_NAME, ROW_ID, REDO_VALUE, UNDO_VALUE
FROM   V$LOGMNR_CONTENTS
WHERE  SEG_OWNER = :owner AND TABLE_NAME = :table;
```

Still a `SELECT`, so still the bounded-window shape.

**The non-obvious part, and the thing to get right:** do **not** parse `SQL_REDO`. It is reconstructed
SQL text and parsing it is how CDC implementations acquire their worst bugs. Use
`DBMS_LOGMNR.MINE_VALUE(REDO_VALUE, 'SCHEMA.TABLE.COLUMN')` to extract a named column's value directly.
That turns the query into a column-per-column projection built from the mapping's own column list —
metadata-driven statement generation, the same shape the scripting work already does elsewhere.

**Constraints:**

- Supplemental logging must be on: `ALTER DATABASE ADD SUPPLEMENTAL LOG DATA` and, per table,
  `ALTER TABLE … ADD SUPPLEMENTAL LOG DATA (ALL) COLUMNS`. DBA-level DDL, and without it `UNDO_VALUE` is
  incomplete for updates.
- `SELECT ANY TRANSACTION`, `LOGMINING` (12c+), access to `V$LOGMNR_CONTENTS`. Not grants a cautious DBA
  hands out casually.
- Archived redo logs must still exist for the window. Their removal is the position-expired case.
- `CONTINUOUS_MINE` was deprecated in 12.2 and **removed in 19c**, so a modern implementation has to
  enumerate and register log files itself (`V$ARCHIVED_LOG`, `DBMS_LOGMNR.ADD_LOGFILE`). This is the bulk
  of the work and it is fiddly.
- CPU-expensive on the source, and the session holds state.
- RAC multiplies the log-file enumeration by thread.

Real work. Worth doing only when Flashback provably is not enough.

## Ruled out

- **Oracle CDC (`DBMS_CDC_PUBLISH`/`DBMS_CDC_SUBSCRIBE`).** Deprecated in 12c and desupported since.
- **`ORA_ROWSCN`.** A pseudo-column giving a row's commit SCN — but only per *block* unless the table was
  created with `ROWDEPENDENCIES` (not the default, and not addable without recreating the table). Block
  granularity means every row in a block looks changed, and it cannot see deletes. Looks like a free
  watermark; is not.
- **GoldenGate/XStream.** What the commercial tools actually use, licensed separately, and XStream
  requires a GoldenGate licence even through the API. Out of scope, worth naming so the question is
  answered rather than open.
- **Audit trail (`DBA_AUDIT_TRAIL`, Unified Audit).** Statement-level, not designed for row capture.

## Where this meets the driver work

Two of the Oracle driver's own listed unknowns from `additional-database-drivers.md` are settled by this
document rather than by the driver:

- **"Oracle's database is a service/schema"** — a Flashback reader works entirely in schema terms and
  never needs a `ListDatabases` that means anything. `SqlDialect.UseDatabaseAsync` (phase 17) already has
  the validate-and-refuse shape Postgres needed; Oracle needs the same.
- **`AuthMode` beyond SqlAuth/IntegratedAuth** — wallets. Unaffected by the choice of CT mechanism, so it
  stays a driver-phase question regardless of which option (trigger, Flashback, LogMiner) lands first.

## Open questions

- **Is undo retention acceptable in practice?** A 15-second poll against a 900-second retention has a
  60× margin, but a replication paused for a day comes back to an expired position. The recovery is a
  reload, which exists — but the operator should be warned at configuration time about the relationship
  between poll interval and retention, not after.
- **Flashback across a restart.** Undo does not survive an instance restart the way archived redo does.
  Worth confirming what a restart does to a stored SCN, and treating it as position-expired if it
  invalidates it.
- **Which comes first in practice** — is there a real Oracle source to design against? Both options above
  are shaped by assumptions about privileges that a real deployment would settle immediately.
- **Should Flashback ship alongside the trigger option, in the same driver phase, rather than after it?**
  Unlike MySQL's binlog (genuinely worth deferring until real demand), Flashback's cost is low enough
  that bundling it with the driver phase — trigger *and* Flashback, LogMiner deferred — may be the right
  scope for a first Oracle phase. Not decided here.
