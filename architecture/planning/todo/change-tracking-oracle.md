# Change tracking — Oracle

**Status: proposal, not agreed.** Read `change-tracking-strategies.md` first. No Oracle driver exists
yet — `additional-database-drivers.md` puts it around phase 23–24 — so this is design ahead of the
driver, deliberately, because *which* mechanism Oracle gets changes what the driver has to expose.

Oracle has more change-tracking options than any other engine here, and they differ by orders of
magnitude in cost and privilege. Picking the right first one matters more here than anywhere else.

## Recommendation up front

**Flashback Version Query first. LogMiner later, if at all.**

That is not the conventional answer — LogMiner is what commercial CDC tools use — and the reason is
worth stating plainly: Flashback Version Query is a **single ordinary `SELECT` against the user's own
table** that returns the operation and the SCN, needs no supplemental logging, no `V$` views, no
`DBMS_LOGMNR` session, and no DBA involvement beyond what the operator already has to read the table.
LogMiner needs all of those and is CPU-expensive besides.

Flashback's limitation is real and bounded: it can only look back as far as undo retention, typically
hours. For a replication polling every 15 seconds that is not a constraint, it is a safety margin.

## Option 1 — Flashback Version Query (recommended first)

```sql
SELECT VERSIONS_OPERATION, VERSIONS_STARTSCN, VERSIONS_XID, t.*
FROM   app.orders VERSIONS BETWEEN SCN :fromScn AND :toScn t
WHERE  VERSIONS_OPERATION IS NOT NULL
ORDER  BY VERSIONS_STARTSCN;
```

- `VERSIONS_OPERATION` is `I`, `U` or `D` — a direct `ChangeOperation` mapping, and one of the few
  mechanisms that hands us the operation without inference.
- `VERSIONS_STARTSCN` is the position. `SELECT CURRENT_SCN FROM V$DATABASE` (or
  `DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER`, which needs a smaller grant) fixes the window's end,
  giving the bounded window the strategies doc describes.
- It is **non-consuming** by construction, so the peek-versus-consume trap does not apply and the
  watermark-on-success-only rule holds with no contract change. That is a significant simplification
  against Postgres.
- A deleted row's version carries its column values as they were, so a delete arrives keyed — which is
  all the writers need.

**Constraints:**

- Bounded by `UNDO_RETENTION` and actual undo tablespace size. Exceeding it raises `ORA-01555`
  (snapshot too old) or `ORA-30052` (invalid lower limit snapshot expression) — the position-expired
  case, mapping onto the shared `PositionExpiredException`.
- Does not survive DDL on the table. A column added mid-window makes the query fail rather than lie,
  which is the right failure.
- `FLASHBACK` privilege on the table (or `FLASHBACK ANY TABLE`) plus `SELECT`.
- Row movement: a table with `ENABLE ROW MOVEMENT` and partition maintenance can produce version rows
  that are physical relocations rather than logical changes. Worth checking, not blocking.

For a polling replication this is a genuinely good fit and it is by far the cheapest thing on this
list to build: it is a statement builder plus an operation-column mapping, which the generic pipeline
almost already has.

## Option 2 — LogMiner

The industrial answer, and the one to reach for when undo retention is genuinely too short or the
source cannot grant `FLASHBACK`.

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
`DBMS_LOGMNR.MINE_VALUE(REDO_VALUE, 'SCHEMA.TABLE.COLUMN')` to extract a named column's value
directly. That turns the query into a column-per-column projection built from the mapping's own column
list — which is exactly the kind of metadata-driven statement generation the scripting work is about,
and a good argument for the two efforts meeting here.

**Constraints:**

- Supplemental logging must be on: `ALTER DATABASE ADD SUPPLEMENTAL LOG DATA` and, per table,
  `ALTER TABLE … ADD SUPPLEMENTAL LOG DATA (ALL) COLUMNS`. DBA-level DDL, and without it `UNDO_VALUE`
  is incomplete for updates.
- `SELECT ANY TRANSACTION`, `LOGMINING` (12c+), access to `V$LOGMNR_CONTENTS`. Not grants a cautious
  DBA hands out casually.
- Archived redo logs must still exist for the window. Their removal is the position-expired case.
- `CONTINUOUS_MINE` was deprecated in 12.2 and **removed in 19c**, so a modern implementation has to
  enumerate and register log files itself (`V$ARCHIVED_LOG`, `DBMS_LOGMNR.ADD_LOGFILE`). This is the
  bulk of the work and it is fiddly.
- CPU-expensive on the source, and the session holds state.
- RAC multiplies the log-file enumeration by thread.

Real work. Worth doing only when Flashback provably is not enough.

## Option 3 — trigger shadow table

The same shape as everywhere else, and on Oracle the triggers are mature and cheap to write. Needs DDL
on the source but no DBA-level grants, no supplemental logging and no `V$` access — which for many
Oracle shops is the difference between "next sprint" and "next quarter".

Worth building because it is the only option that needs nothing from a DBA beyond `CREATE TRIGGER`.

## Ruled out

- **Oracle CDC (`DBMS_CDC_PUBLISH`/`DBMS_CDC_SUBSCRIBE`).** Deprecated in 12c and desupported since.
  Do not build on it.
- **`ORA_ROWSCN`.** A pseudo-column giving a row's commit SCN — but only per *block* unless the table
  was created with `ROWDEPENDENCIES`, which is not the default and cannot be added without recreating
  the table. Block-level granularity means every row in a block looks changed. It also cannot see
  deletes. Looks like a free watermark; is not.
- **GoldenGate / XStream.** What the commercial tools actually use, licensed separately, and XStream
  requires a GoldenGate licence even when used through the API. Out of scope, worth naming so the
  question is answered rather than open.
- **Audit trail (`DBA_AUDIT_TRAIL`, Unified Audit).** Statement-level and not designed for row capture.

## Where this meets the driver work

The Oracle driver does not exist yet, and two of its listed unknowns from
`additional-database-drivers.md` are settled by this document rather than by the driver:

- **"Oracle's database is a service/schema"** — a Flashback reader works entirely in schema terms and
  never needs a `ListDatabases` that means anything. `SqlDialect.UseDatabaseAsync` (phase 17) already
  has the validate-and-refuse shape Postgres needed; Oracle needs the same, and this is a second
  independent confirmation that hook was worth adding.
- **`AuthMode` beyond SqlAuth/IntegratedAuth** — wallets. Unaffected by the choice of CT mechanism, so
  it stays a driver-phase question.

## Open questions

- **Is undo retention acceptable in practice?** A 15-second poll against a 900-second retention has a
  60× margin, but a replication that is paused for a day comes back to an expired position. The
  recovery is a reload, which exists — but the operator should be warned at configuration time about
  the relationship between poll interval and retention, not after.
- **Flashback across a restart.** Undo does not survive an instance restart in the way archived redo
  does. Worth confirming what a restart does to a stored SCN, and treating it as position-expired if
  it invalidates it.
- **Which comes first in practice** — is there a real Oracle source to design against? Both options
  above are shaped by assumptions about privileges that a real deployment would settle immediately.
