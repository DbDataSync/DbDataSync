# Should a table-mapping-level pause raise a notification, like a replication-level one does?

**Status: resolved 2026-09-15 — no, not for now.** Extracted from
`architecture/implementation/done/phase-131-pause-history-ui.md`'s own "What this does not build"
section, where it sat as an inert bullet — moved here per `architecture/implementation/README.md`'s
"Follow-up work gets its own doc, not a paragraph." Decided without further investigation: a
table-mapping pause stays silent, same as phase 131 shipped it. Revisit if it turns out to matter in
practice — nothing about `PauseEventRecord` (it already carries `MappingName`) blocks adding this later.

## The question, as phase 131 left it

> Notifications for a table-mapping pause. `SetMappingHold` raises nothing — confirmed by reading the
> method, not merely asserted; whether the mapping grain should ever notify is left as a separate,
> unresolved product question.

Phase 64 (`architecture/implementation/done/phase-064-pause-and-notes.md`) gave replication-level pause
its own notification. Phase 131 widened pause history (`PauseEvents`) to the table-mapping grain
alongside the existing replication grain — same audit trail, same UI pattern — but did not extend
notifications to match, and nobody has decided whether it should.

## What would need deciding

- Is a single table mapping being paused/resumed something worth interrupting anyone for, the way a
  whole replication being paused already is — or is it routine enough (an operator working through one
  mapping at a time, say) that a notification per mapping would just be noise?
- If yes: does it reuse the exact notification shape phase 64 already built for the replication grain, or
  does the *volume* difference (one replication vs. potentially many mappings within it) argue for a
  different treatment — batching, a digest, or a lower default severity?
- Nothing about phase 131's own schema work blocks this either way — `PauseEventRecord` already carries
  `MappingName`, so whatever decision comes out of this could be built without another schema change.
