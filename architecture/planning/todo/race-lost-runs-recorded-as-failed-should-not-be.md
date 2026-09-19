# A run that loses a race against other in-flight work is recorded as "Failed" — it shouldn't be

**Status: open, raw thought — not yet designed.**

## The problem

Two existing cases in `RunExecutor` (`src/DbDataSync.TaskRunner/RunExecutor.cs`) catch a run that did
nothing wrong — it simply lost a race against other work already in flight for the same mapping — and
still record it as `RunStatus.Failed`:

- `WorkQueueCollisionException` → `RunFailureKinds.ConcurrentLoadInProgress` — an auto-triggered initial
  load lost its race against other in-flight work for the same mapping+segment (phase 143).
- `MappingLoadingException` → `RunFailureKinds.MappingStillLoading` — a manual "Run Now" landed on a
  mapping still `ReadHold.Loading`.

Both catch blocks say outright, in their own comments, that there is nothing for an operator to do and
the mapping resolves itself on its next pass. That's not a failure — it's an expected, self-healing
non-event — but the Runs tab currently shows it exactly like a real failure needing attention, and
whatever ties failure counts/health rollups together (see `architecture/planning/done/run-metrics.md`)
would count it as one too.

## What the user suggested

Some other outcome instead of `Failed` — a warning, an info-level result, or a distinct "skipped" result.
`RunStatus` today is `Queued | Pending | Running | Succeeded | Failed | Cancelled` — `Cancelled` already
exists but is reserved for an explicit user-initiated cancel (`ProcessSupervisor`/`BulkLoadService`'s
"Cancelled by user request."), a different thing from a race the system itself decided to back off from,
so reusing it as-is would blur two meanings that are worth keeping distinct.

## Open questions

- New `RunStatus` value (e.g. `Skipped`), or keep `Failed`/`Cancelled` as the only two terminal-but-not-
  `Succeeded` states and instead lean entirely on `RunFailureKinds`-style metadata to say "this one's
  fine, ignore it"? A new status is the more honest fix but touches every place that already
  pattern-matches `RunStatus` (`JournalRecovery`, health rollups, the Runs tab, filters, exit codes).
- Does this apply to *only* these two existing exception cases, or does it become the general answer for
  "the run system itself decided not to run this rather than something failing" — which is exactly the
  shape the sibling doc
  (`architecture/planning/todo/mapping-added-to-running-replication-without-source-provisioning-never-syncs.md`)
  also wants for a mapping skipped due to unmet source provisioning?
- Whatever aggregates "failures" for a health view needs to actually exclude the new outcome, not just
  the Runs tab's own status label — an easy thing to fix in one place and miss in another.
- Naming: "Skipped" reads right for the provisioning case above (a tick chose not to run it) but slightly
  off for these two (a run *did* start and then bailed once it discovered the collision) — "warning" per
  the user's own suggestion might fit the existing two better than "skipped" does, or these could end up
  as two different outcomes rather than one.
