# Phase 40 — An editable target table, and provisioning it from the picker (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/editable-target-table.md`.

## The gap, stated precisely

Phase 25 built provisioning: `IProvisioner` generates `CREATE TABLE` for a target that does not exist,
from the source's columns through the canonical type system, and shows the operator the DDL before it
runs. That machinery is complete and tested.

**Nothing in the UI can reach it for a table that does not exist yet.** `MappingSide.tsx` renders the
target table as a closed `<select>` populated from `useTables(connection, database)` — the tables the
target database already has. So:

- there is no way to name a target that does not exist and have DataSync create it, and
- there is no way to type a name at all, closed selection being the only interaction.

Both halves of the planning note are the same control. The provisioning half is the one that stings:
the feature exists, is tested, and is unreachable from the screen where an operator would want it.

## What this builds

### A combobox, not a select

The target-table field becomes a text input with suggestions drawn from `useTables`, accepting a value
that is not in the list. Three states, and the third is the new one:

| what the operator typed | what the field says |
| --- | --- |
| an existing table | picked, as today |
| a name not in the list | **does not exist yet — will be created** |
| nothing | as today |

Schema and table are separate today (`schema` and `table` on `TableSpec`), and should stay separate:
parsing `dbo.Orders` out of one box is a guess about quoting that gets wrong the moment a name contains
a dot.

### The source side stays a closed list

A source table that does not exist is not a thing to create; it is a typo. The combobox is
target-side only, and `MappingSide` already takes a `label` and knows which side it is.

### Wiring the not-yet-existing case to phase 25

When the target names a table that is not in the list, the mapping editor surfaces the existing
`ProvisioningCard` flow for it: plan (show the generated DDL), then apply. The card exists; this is a
second entry point to it, from the place the need arises rather than from a separate screen.

Two things that follow and are easy to miss:

- **The plan needs the source's columns**, which means the source side must be complete first. Until
  it is, the field can accept the name and say the plan is not available yet rather than showing an
  error — a mapping is built in whatever order the operator works in.
- **Saving a mapping whose target does not exist must remain possible.** Today a mapping is validated
  at save (phase 16) and the target's *existence* is not part of that — staging discovers it. Keeping
  it that way is right: a mapping pointing at a table that is about to be provisioned is a legitimate
  intermediate state, and blocking the save would force the operator to provision before they can
  describe what they want.

### Column mappings against a table that does not exist

`ColumnMappingEditor` lists the **target's** columns from the catalog and lets each be fed from a
source column. For a target that does not exist there are none, so the editor has nothing to show.

The honest behaviour, and the one that matches how provisioning works: **the target's columns are the
mapped source columns** until the table exists. So the editor should offer the source's columns as the
target's, auto-mapped by name — which is exactly what `IProvisioner` will then create. Once the table
exists, it goes back to reading the catalog.

That is the part of this phase most likely to be got wrong quietly, because it looks like a UI
convenience and is actually the thing that decides what gets created.

## What this phase does not build

Any change to `IProvisioner`, the canonical type system, or the DDL it generates. This is the missing
route to it.

Editing an *existing* target's shape — adding a column to a table that already exists. That is schema
evolution, phase 27's `ILifecycleHook` motivating example already touches it, and it is a different
problem from creating a table that is not there.

Renaming a target table in place. Retargeting a mapping to a different name is what this enables;
renaming the underlying table is the operator's business.

## How to verify when built

- Playwright: type a table name that does not exist, see it marked as such, see the DDL plan, apply it,
  and see the mapping run — the whole loop, since the complaint is that the loop cannot be entered.
- A mapping saved with a not-yet-existing target, reloaded, and still showing the name.
- Column mappings defaulting to the source's columns for a not-yet-existing target, and to the
  catalog's once it exists.
- The source-side field still a closed list.
- A target name typed while the source side is incomplete: accepted, plan deferred, no error.
- Full suite green.

## Open questions

- **Suggestions while typing** against a database with thousands of tables. `useTables` returns all of
  them today and the `<select>` renders all of them, so this is not a new problem — but a combobox
  invites typing, and typing invites filtering server-side. Worth measuring against a real catalog
  before adding an endpoint.
- **Schema that does not exist either.** `CREATE TABLE app.Orders` fails if `app` does not exist.
  Phase 25 generates the table; whether it should generate the schema is a decision, and creating
  schemas is a larger permission than creating tables.
