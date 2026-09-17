# Change tracking — Oracle, via trigger audit

**Status: resolved 2026-09-16 — turned into an implementation plan.** See
`architecture/implementation/todo/phase-148-oracle-driver-trigger-audit-and-flashback.md`, which builds
this design alongside Flashback Version Query and the rest of the Oracle driver in one phase — the
pre-12c identity-column question below was resolved there by flooring the driver at 12c+. LogMiner
(`change-tracking-oracle.md`'s Option 2) stays deferred, out of that phase's scope.

**Narrower and much less blocked than it looks.** Extracted from
`change-tracking-oracle.md`'s own "Option 3" (that doc is being refined to cover Flashback Version Query
and LogMiner only — see its own updated status line). Same reasoning as the sibling MySQL/MariaDB
extraction: phase 33 (`architecture/implementation/done/phase-033-trigger-audit-change-tracking.md`)
already built the entire generic half of this mechanism (`TriggerAuditReader`, `IPositionAcknowledging`,
`TriggerAuditPlan`), proven against two engines already. Oracle needs only the one piece phase 33
deliberately left for it: the per-engine DDL.

## What already exists, and what this doc is actually scoping

Same shape as the MySQL/MariaDB doc, and worth being precise about since it's the whole point: a
`OracleTriggerAudit` static class with `CreateShadowTable(...)`/`CreateTrigger(...)` methods returning
plain SQL, wired into `OracleProvisioner`'s `EnableSourceChangeCapture` case the same way
`PostgresProvisioner.cs` already does it, registering `GenericDriverKinds.TriggerAudit` as a reader Kind
the Oracle driver offers. No new reader, pruning, or state-store work — all already built and proven.

## The DDL — Oracle specifically

Oracle sits between the other two engines' shapes: like Postgres (and unlike MySQL), one trigger can
handle all three events — `AFTER INSERT OR UPDATE OR DELETE`, branching inside with the `INSERTING`/
`UPDATING`/`DELETING` boolean predicates. Like MySQL (and unlike Postgres), the body is **inline PL/SQL**,
no separate function needed. Row references are colon-prefixed — `:NEW`/`:OLD`, not bare `NEW`/`OLD`.

```sql
CREATE TABLE ds_changes_orders (
    seq        NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,  -- 12c+; see below for older versions
    op         CHAR(1) NOT NULL,
    id         NUMBER NOT NULL,                                  -- one column per key column
    changed_at TIMESTAMP(6) DEFAULT SYSTIMESTAMP NOT NULL
);

CREATE OR REPLACE TRIGGER ds_trg_orders
AFTER INSERT OR UPDATE OR DELETE ON orders
FOR EACH ROW
BEGIN
    IF INSERTING THEN
        INSERT INTO ds_changes_orders (op, id) VALUES ('I', :NEW.id);
    ELSIF UPDATING THEN
        INSERT INTO ds_changes_orders (op, id) VALUES ('U', :NEW.id);
    ELSE
        INSERT INTO ds_changes_orders (op, id) VALUES ('D', :OLD.id);
    END IF;
END;
```

**One version fork worth naming precisely, not glossed over.** `GENERATED ALWAYS AS IDENTITY` (Oracle's
answer to `AUTO_INCREMENT`/`BIGSERIAL`, and the cleanest match to what `TriggerAuditStatement.SequenceColumn`
needs) is 12c+ only. Before 12c, `seq` needs a `CREATE SEQUENCE` plus either a `BEFORE INSERT` trigger on
the shadow table populating it from `NEXTVAL`, or a `DEFAULT sequence.NEXTVAL` (11g does not support a
sequence in a column default; 12c does directly via identity columns, making this moot there). Worth
confirming which Oracle versions are actually in scope before picking one shape over branching on
version — the doc that answers this precisely should be whichever Oracle driver phase gets written, not
guessed here.

## Where the harder decisions already got made, correctly, by the sibling doc

`change-tracking-oracle.md`'s own "Where this meets the driver work" section already settled two things
relevant here, and this doc inherits them rather than re-deciding: Oracle's database-is-a-schema mapping
(a trigger-audit reader works entirely in schema terms, same as `UseDatabaseAsync`'s existing
validate-and-refuse shape) and `AuthMode` needing to grow beyond `SqlAuth`/`IntegratedAuth` for wallets —
neither is specific to the trigger-audit mechanism, both are driver-phase questions regardless of which
change-tracking option an operator picks first.

## What this does not do

- Does not build Flashback Version Query or LogMiner — see the refined `change-tracking-oracle.md`.
- Does not change `TriggerAuditReader`, `IPositionAcknowledging`, or `TriggerAuditPlan`.
- Does not resolve the pre-12c identity-column question definitively — named above, left for whoever
  scopes the actual Oracle driver phase against real target versions.

## Open questions

- **Which Oracle versions are actually in scope** — decides whether the 12c `IDENTITY` shape is the only
  one worth building, or whether a sequence-based fallback for 11g needs to exist from the start.
- **`FLASHBACK`/`LOGMINING`-adjacent privileges vs. trigger privileges.** Worth confirming explicitly:
  this mechanism needs only ordinary `CREATE TRIGGER` rights, none of the DBA-level grants the native
  alternatives need — which is the whole reason this option is worth having at all for a shop that can't
  get those grants. Stating this plainly at the point an operator chooses a mechanism (the same posture
  `TriggerAuditPlan.Costs` already takes) is worth building explicitly, not left implicit.
