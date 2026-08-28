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

**Next step**: this is now specific enough to design — find the current dropdown component (target-table
selection in the replication setup UI) and scope a combobox replacement plus the "name not found ->
offer to provision" path.
