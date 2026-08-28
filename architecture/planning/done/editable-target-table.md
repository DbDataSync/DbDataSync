# Automatic target table, and an editable target table

**What this actually is (2026-08-28): a UI gap, not a provisioning gap.** Both halves are about how the
operator picks the target table in the UI, not about `IProvisioner`'s `CREATE TABLE` generation from
phase 25 — that piece is unaffected.

The current UI presents target-table selection as a dropdown populated from the tables that already
exist in the target database. That has two consequences, and each is one of these two todo items:

- **Automatic target table**: because the dropdown only lists existing tables, there is no way for the
  operator to type/select a target table name that doesn't exist yet and have DataSync create it (via
  phase 25's provisioning) as part of setting up the replication. The dropdown itself is the blocker —
  provisioning exists, but nothing in the UI can invoke it for a not-yet-existing name.
- **Editable target table**: the end user cannot edit the dropdown's value to an arbitrary table name —
  it's a closed selection over what already exists, not a free-text or free-text-with-suggestions field.
  So even renaming/retargeting to a not-yet-existing table name is not possible today.

Both point at the same fix: replace (or augment) the dropdown with an editable/combobox-style input that
accepts a name not in the existing-tables list, and wire that case to phase 25's provisioning flow
(show the generated DDL, let the operator confirm) instead of leaving it unreachable.

---

# Outcome — resolved 2026-08-28

Agreed, as `implementation/todo/phase-040-editable-target-table.md`. The diagnosis is confirmed in the
code: `MappingSide.tsx` renders the target table as a closed `<select>` over `useTables(connection,
database)`, so a name not already in the target database cannot be expressed at all — and phase 25's
provisioning, which exists and is tested, is unreachable from the screen where the need arises.

Three decisions the phase adds beyond the note:

- **Target side only.** A source table that does not exist is a typo, not something to create.
- **Saving a mapping whose target does not exist stays legal.** Phase 16 validates a mapping at save and
  does not check the target's existence; keeping it that way means an operator can describe what they
  want before provisioning it, rather than being forced into the opposite order.
- **Column mappings for a not-yet-existing target default to the source's columns**, auto-mapped by
  name — because those *are* what `IProvisioner` will create. This looks like a UI convenience and is
  actually the thing that decides what gets created, which makes it the part most likely to be got
  wrong quietly.

Explicitly out: editing an existing target's shape. That is schema evolution, which phase 27's hook
example already touches, and it is a different problem from creating a table that is not there.
