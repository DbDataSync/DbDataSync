# Change tracking — MySQL/MariaDB, via trigger audit

**Status: resolved 2026-09-16 — turned into an implementation plan.** See
`architecture/implementation/todo/phase-147-mysql-mariadb-driver-and-trigger-audit.md`, which builds this
design (plus the rest of the MySQL/MariaDB driver) directly. The open questions below were carried into
that phase doc rather than answered here.

**Narrower and much less blocked than it looks.** Extracted from
`change-tracking-mysql.md`'s own "Option 1" (that doc is being refined to cover the binlog-based native
alternative only — see its own updated status line). Split out because this option turned out, on
closer inspection, to need essentially none of the design work the binlog path does: phase 33
(`architecture/implementation/done/phase-033-trigger-audit-change-tracking.md`) already built the
*entire* generic half of this mechanism — `TriggerAuditReader`, `IPositionAcknowledging` (the pruning
hook both this doc and the binlog doc used to flag as an open question), and `TriggerAuditPlan` — and
proved it against two genuinely different engines (SQL Server, Postgres) already. What MySQL/MariaDB
need is the one piece phase 33 deliberately left unbuilt for them: **the per-engine DDL.**

## What already exists, and what this doc is actually scoping

Read `TriggerAuditReader`/`TriggerAuditStatement`/`TriggerAuditPlan` (`DbDataSync.Drivers.Generic`)
before this — they're the entire read side, and this doc adds nothing to them. The pattern, confirmed
directly from the real Postgres implementation (`PostgresTriggerAudit.cs`, ~75 lines) and its
`IProvisioner` wiring (`PostgresProvisioner.cs`):

- One static class, `MySqlTriggerAudit`, with two methods: `CreateShadowTable(...)` and
  `CreateTriggers(...)` returning plain SQL strings, built from `MySqlDialect` for quoting.
- Wired into `MySqlProvisioner`'s own `EnableSourceChangeCapture` case exactly the way Postgres's is —
  check `TriggerAuditState` (does the shadow table/trigger already exist, what's the source's primary
  key), hand the generated DDL to `TriggerAuditPlan.Build`.
- Register `GenericDriverKinds.TriggerAudit` as a reader Kind the MySQL driver offers (the driver already
  needs its basic dialect/connection/catalog work regardless — see `cli-setup-and-api-parity.md`'s sibling
  concern about not gating unrelated work behind unrelated work, and `additional-database-drivers.md`'s
  own scoping of what a driver phase covers).

That is genuinely the whole of it. No new reader logic, no new pruning logic, no new state-store
concept — all three already exist and are already proven against a second engine.

## The DDL — MySQL specifically

The one real syntactic difference from both existing implementations: **MySQL cannot combine
`INSERT OR UPDATE OR DELETE` in one trigger**, unlike Postgres (which branches on `TG_OP` inside one
function) or SQL Server (one statement-level trigger, `inserted`/`deleted` pseudo-tables). MySQL's
`CREATE TRIGGER` takes exactly one event per definition, so this needs **three triggers**, not one —
already correctly anticipated in the original doc's own sketch, worth confirming here rather than
re-deriving:

```sql
CREATE TABLE ds_changes_orders (
    seq        BIGINT AUTO_INCREMENT PRIMARY KEY,
    op         CHAR(1) NOT NULL,
    id         INT NOT NULL,                    -- one column per key column
    changed_at TIMESTAMP(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB;

CREATE TRIGGER ds_trg_orders_ai AFTER INSERT ON orders
FOR EACH ROW INSERT INTO ds_changes_orders (op, id) VALUES ('I', NEW.id);

CREATE TRIGGER ds_trg_orders_au AFTER UPDATE ON orders
FOR EACH ROW INSERT INTO ds_changes_orders (op, id) VALUES ('U', NEW.id);

CREATE TRIGGER ds_trg_orders_ad AFTER DELETE ON orders
FOR EACH ROW INSERT INTO ds_changes_orders (op, id) VALUES ('D', OLD.id);
```

Inline trigger bodies, like SQL Server and unlike Postgres — no separate function needed. `seq` is the
watermark, exactly as `TriggerAuditStatement.SequenceColumn` already expects; `AUTO_INCREMENT` gives it
monotonicity for free, the same role `BIGSERIAL` plays in the Postgres implementation.

## MySQL vs. MariaDB — the overlap, checked precisely rather than asserted

The original doc's claim ("the trigger option covers both with one implementation") holds up under
closer inspection, and it's worth stating exactly *why* rather than repeating the assertion:

- **`CREATE TRIGGER ... AFTER {INSERT|UPDATE|DELETE} ON t FOR EACH ROW <body>`** — identical syntax in
  both. MariaDB forked from MySQL 5.1-era and never diverged here.
- **`NEW`/`OLD` row references** — identical in both, same names, same access pattern.
- **`BIGINT ... AUTO_INCREMENT`** — identical, same InnoDB-backed guarantee, in both.
- **`TIMESTAMP(6) DEFAULT CURRENT_TIMESTAMP(6)`** (fractional-second precision) — supported since MySQL
  5.6.4 and MariaDB 5.3; a non-issue for any reasonably current deployment of either.
- **Identifier quoting (backtick), `ENGINE=InnoDB`** — identical in both.

**One real, version-gated caveat that affects both engines identically — not a MySQL-vs-MariaDB
difference at all.** Older versions of *either* engine (MySQL before 5.7.2, MariaDB before 10.2.1) allow
only one trigger per `(table, timing, event)` combination. If the tracked table already has an
operator-authored `AFTER INSERT`/`UPDATE`/`DELETE` trigger of its own, DbDataSync's own trigger creation
fails outright on an old-enough server of either family; on a current one, both support stacking multiple
triggers (with `FOLLOWS`/`PRECEDES` for explicit ordering, though DbDataSync doesn't need to care about
ordering relative to an unrelated trigger). Worth a real provisioning-time check — "does this table
already have a trigger on this event, and does this server version allow stacking" — rather than letting
an operator discover it as a raw DDL error.

**Conclusion: one `MySqlTriggerAudit` implementation, unconditionally, for both.** No engine-detection
branch needed inside it — this is squarely the case the original doc predicted ("another point in its
favour"), now confirmed rather than assumed. The version-gated stacking check above is the one thing
worth building explicitly, and it's a provisioning-time *check*, not a fork in the DDL itself.

## What this does not do

- Does not build the binlog-based native alternative — see the refined `change-tracking-mysql.md`.
- Does not change `TriggerAuditReader`, `IPositionAcknowledging`, or `TriggerAuditPlan` — all reused
  unmodified.
- Does not attempt to detect or migrate an operator's pre-existing triggers automatically — the
  version-gated stacking check above is a refusal-with-a-reason, not an automatic resolution.

## Open questions

- **Is the version-gated multiple-triggers-per-event check worth building for v1**, or is "the DDL fails
  with MySQL's own error message, which already says why" an acceptable first answer? Leaning toward
  building the check — the phase 33 precedent (`TriggerAuditPlan`) already has a place for exactly this
  kind of "state a cost/blocker at the point of choice" reasoning, and a raw engine error is a worse first
  experience than a plain refusal.
- **Sequence generation before `AUTO_INCREMENT` was reliable across replication topologies** (a historical
  MySQL replication caveat, less relevant to a standalone shadow table with no replica of its own) — worth
  a sanity check against the target MySQL/MariaDB versions actually in scope, not assumed a non-issue.
