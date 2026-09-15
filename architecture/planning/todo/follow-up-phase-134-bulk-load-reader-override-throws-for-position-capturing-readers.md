# A position-capturing reader configured as a mapping's Bulk Load override now throws

**Status: diagnosed, no fix agreed.** Extracted from
`architecture/implementation/done/phase-134-initial-load-becomes-a-bulk-load.md`'s own "Known follow-up
/ not done here" section, where it sat unactioned — moved here per
`architecture/implementation/README.md`'s "Follow-up work gets its own doc, not a paragraph."

## The problem, as phase 134 left it

Phase 134 removed the `previousWatermark is null` full-load branch from `MsSqlChangeTrackingReader`,
`MsSqlCdcReader`, and `TriggerAuditReader` — an initial load runs through the Bulk Load pipeline now,
not through a change reader's own full-load branch. `RunKind.BulkLoad` always dispatches with
`ReadIntent.InitialLoad` and a null watermark (its own contract), which every *other* reader either
never had a full-load branch for (the "Exempt four": `BatchReloadReader`, `MsSqlBatchReloadReader`,
`DuckDbQueryReader`, `ScriptedQueryReader`) or still legitimately handles (`WatermarkReader`, kept
deliberately — see phase 134's own point 8).

But nothing stops an operator from configuring one of the *removed-branch* three as a mapping's **Bulk
Load reader override** (`TableMappingConfig.BulkLoadReaderOverride` /
`PipelineResolution.BulkLoadReader`) — a real, if unusual, existing configuration surface, unrelated to
what that mapping's own `ChangeProcessing.Reader` is. If a mapping does this, every Bulk Load against it
— on-demand or auto-triggered — now throws instead of full-loading, since the reader it's pointed at no
longer has anywhere to go with `(null, InitialLoad)`. Confirmed real by phase 134's own code reading, not
reproduced against a live instance.

## Why it matters

Not raised by phase 134's own corrected spec, not caught by any test — this is a genuinely new
regression the branch removal introduced for a narrow, real configuration a previous phase's own design
explicitly allowed for (a mapping using a *different* reader for its Bulk Load than its incremental
sync). An operator who set this up before phase 134 landed would find their next reload silently starts
failing, with no obvious link back to "you configured a reader that can no longer do this."

## Candidate directions, not evaluated

- **A defensive, clear-message throw** the moment `RunKind.BulkLoad` resolves to one of the three, naming
  the mismatch plainly ("X is a Change Tracking/CDC/TriggerAudit reader; it reads changes and can no
  longer perform a full load — remove the Bulk Load override or point it at a reader that can") — turns
  today's confusing crash into a clear, actionable one, without restoring any capability.
- **Leave it** — if this configuration is rare enough (three readers, deliberately picked *as* a Bulk
  Load override, which is itself unusual since the Exempt four already exist for exactly this purpose)
  that a clear error the first time someone hits it is an acceptable cost.
- Worth checking first whether this configuration is reachable *at all* through the SPA's own UI (a
  dropdown that only offers sensible choices would make this theoretical rather than real), or only
  through hand-edited YAML.
