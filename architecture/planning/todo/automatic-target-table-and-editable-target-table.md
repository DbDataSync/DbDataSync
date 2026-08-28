# Automatic target table, and an editable target table

**Empty as of 2026-08-27 — nothing to plan against yet.** The filename is the whole content, and it is
not enough to design from because the obvious reading may already be built.

**Phase 25 built target-table provisioning**: `IProvisioner` generates `CREATE TABLE` for a target that
does not exist, from the source's columns through the canonical type system, and shows the operator the
DDL before running it. So "automatic target table" may already be done, or may mean something this
does not cover.

Guesses at what "editable" might mean, none of which should be acted on without confirmation:

- editing the generated DDL before applying it — a plan the operator can amend, rather than take or leave
- editing an *existing* target's shape as the source changes (schema evolution), which phase 27's
  motivating example already touches with an add-missing-columns hook
- editing which target columns exist independently of the mapping, so a target can carry columns
  DataSync does not populate

**Next step**: a sentence saying which of those it is, or what phase 25 does not do.
