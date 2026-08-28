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

---

# Retrospective

Built as planned. The control took an afternoon; what took the rest of it was that making
provisioning reachable meant it ran for the first time, and two things on the path had never
executed.

## The Apply button has never worked

`[HttpPost("{action}/apply")]`. `action` is a **reserved token** in an MVC route template — it names
the controller method rather than binding a URL segment — so the route never matched. Every Apply
since phase 25 returned a 404, and a 404 from that endpoint reads as "no such mapping", which is
exactly the sort of wrong answer nobody investigates.

It survived because nothing had ever pressed the button. Phase 25 tested `IProvisioner` and
`ProvisioningService` thoroughly, and both are correct; the gap was one route attribute between them
and the UI, and the only way to find it was to press it.

`ProvisioningRoutingTests` pins it now, and deliberately needs no database: an unknown action comes
back 400 naming the action, which proves both that the route matched and that the segment bound.
A routing miss is a 404 and a service reject is a 400, and telling those two apart is the whole test.

## A failed step looked like nothing happening

`ApplyAsync` returns per-step results, and a step that fails does **not** fail the request — that is
correct, and deliberate: a least-privileged connection that cannot run `ALTER DATABASE` is the
enterprise-normal outcome phase 25 explicitly designed for, with Copy there for exactly that case.

But the card threw the results away. So a failed Apply left a plan that still said `missing`, no
error, and no way to tell it from a button that did nothing — which, thanks to the route bug, is what
was actually happening. Two failures presenting identically is what made the first one take as long
as it did to see. Each step's outcome is now shown, with its message.

## The column editor is not a convenience

The plan called this the part most likely to be got wrong quietly, and it was right about why. For a
target that does not exist there are no catalog columns, so the editor shows the source's — and that
looks like filling an empty screen with something plausible.

It is not. `ProvisioningService.PlanCreateTargetTableAsync` builds the `CREATE TABLE` from the
mapping's **column mappings**. What the editor lists is what gets created. Showing the source's
columns is the only answer that makes the table that appears match the table that was described;
showing nothing would create nothing, and showing a guess would create the guess.

## Loading is a third state, not a default

`targetExists` started as `boolean`, defaulting to `true` while the catalog loaded. That default asked
the columns endpoint about a table that was not there, which threw and came back 500.

It is `boolean | undefined` now, and the difference matters in both directions: defaulting to "exists"
asks about a table that may not be there, and defaulting to "missing" flashes *does not exist yet* at
someone who just picked an existing table. Neither is right, so neither is the default — nothing is
asked until the catalog has answered. The endpoint also returns 404 rather than 500 for a table it
cannot find, because the UI now legitimately asks about tables that may not exist.

## The test found the thing tests are for

Writing test 18 against the existing `playwright-sync` replication failed with *"Source column 'Name'
was not returned by the reader"* — test 16 binds a row transform at the **replication** level, and
every mapping under it inherits that. Which is the hierarchy working exactly as designed, and a
useful reminder that a replication is a real scope: the new mapping got its own replication.

The other test-shaped lesson: asserting on the shared run-history table for "succeeded" is meaningless
on a continuous schedule, because a pass that ran before Apply and failed for the very reason the test
is about is *expected*. The assertion is on this mapping's own runs.

## Verification

- Playwright test 18, the whole loop the phase exists to make enterable: name a table that is not
  there, see it marked as such, see the column editor take the source's columns and say so, save,
  reload and find the name still there, see the generated `CREATE TABLE`, apply it, see each step
  succeed, confirm the table in `sys.tables`, run the mapping, and read the rows back out of the table
  that did not exist a minute earlier.
- Playwright test 05 updated: the target is a combobox now, and naming a table that *does* exist
  works the same way and says nothing about creating it.
- `ProvisioningRoutingTests` — the Apply route matches and binds its segment; a missing mapping is a
  404 from the handler.
- Full .NET suite green: 535 tests. Playwright: 18 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions

- **Suggestions against a large catalog.** Still not measured. `useTables` returns everything and the
  old `<select>` rendered everything, so a `<datalist>` is no worse — but typing invites filtering, and
  filtering invites a server-side endpoint. Worth measuring against a real catalog first.
- **A schema that does not exist either.** `CREATE TABLE app.Orders` fails if `app` is not there, and
  the schema box now makes typing one easy. Creating schemas is a larger permission than creating
  tables, so it stays a decision rather than a default.
- **A cancelled provisioning request logs a `SqlException` stack.** react-query cancels an in-flight
  plan on unmount, the `CancellationToken` cancels the SQL command, and SqlClient reports that as
  `SqlException: A severe error occurred on the current command` — which reaches the developer
  exception page looking like a fault. Harmless and Development-only, but it costs a minute of
  confusion every time someone reads the log.
