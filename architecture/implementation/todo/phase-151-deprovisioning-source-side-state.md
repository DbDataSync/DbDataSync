# Phase 151 — deprovisioning: source-side state goes when the config that asked for it does (planned)

**Status**: Planned, not started. Split out of phase 34 on 2026-09-16 — see
`architecture/implementation/done/phase-034-postgres-logical-replication.md`'s "What became its own
phase". Not a Postgres phase: it owes the same duty to phase 33's trigger-audit shadow tables.

## The gap

**DbDataSync creates state on other people's source databases and has no way to remove it.** Deleting a
table mapping or a replication removes configuration, and nothing else. What is left behind today:

| Left behind | Created by | Cost of leaving it |
| --- | --- | --- |
| A logical replication slot | Phase 34's `EnableSourceChangeCapture` | **Pins WAL indefinitely.** A slot nobody consumes makes the source keep every write-ahead log segment since the slot last advanced, until the disk fills |
| A shadow table | Phase 33's `EnableSourceChangeCapture` | Grows for as long as the trigger fires; storage only |
| An `AFTER` trigger and its `plpgsql` function | Phase 33 | **A write cost on every insert, update and delete on that table, forever** |

The slot is the one that can take a production database down, which is why phase 34 surfaced this; the
trigger is the one that silently costs somebody throughput on a table they may no longer associate with
this tool at all.

Phase 34's own plan asked for the slot half specifically — *"dropped when the replication or mapping is
deleted. Deleting a replication must not silently leave a slot behind."* Building only that half would
mean inventing the mechanism and then declining to use it for the case that already existed.

## What this builds

**A deprovisioning plan, in the same shape as `IProvisioner`'s existing one.** `PlanAsync` already
returns previewable steps with rationale; the symmetric call returns the steps that would remove what
this mapping's reader put there. Reusing the shape is the point: an operator sees the DDL and applies it
deliberately, exactly as they did on the way in, rather than a delete button silently running `DROP`
against their source.

**The statements already exist** for the Postgres slot — `PgLogicalSlotStatement.RenderDropSlot`, used
today only to quote the drop in the create step's rationale — and for the trigger-audit side the DDL is
a mirror of what `PostgresTriggerAudit` and the MsSql equivalent already render.

## The questions this has to answer, which is why it is a phase and not a fix

- **Is deprovisioning automatic on delete, or a step an operator takes first?** Automatic is what the
  phase 34 plan asked for and is what stops a slot being orphaned by someone who did not know to look.
  It is also this tool running `DROP` on somebody's production source as a side effect of a UI delete,
  which is the single most destructive thing it would ever do, and it is not obviously recoverable: a
  slot recreated later starts at the current WAL position, so the changes in between are gone. **The
  recommendation is: plan it automatically, apply it never** — deleting the config offers the
  deprovisioning steps and refuses to forget about them, rather than running them.
- **What about state a second mapping still needs?** Two mappings can share a slot (phase 34 allows it
  explicitly, as long as neither advances it) and two mappings on the same table share a shadow table.
  Dropping on the first delete would break the second. So the plan has to be computed against the
  *remaining* config, not against the deleted item alone.
- **What about a source that is unreachable at delete time?** Configuration deletion cannot depend on a
  source being up. Whatever is left undone has to be findable afterwards, which is the same list phase
  149's orphan reporting builds.

## How to verify when built

- Deleting a mapping whose reader is `PgLogicalSlot` offers the slot's drop as a step, with the same
  preview shape as the create.
- Deleting one of two mappings sharing a slot offers nothing, and says why.
- The same for a trigger-audit shadow table and trigger.
- A source that cannot be reached at delete time does not block the delete, and what was left behind is
  reported.
- `Category=Integration` for both readers' state, since the whole value is that the statements actually
  run against a real server.
