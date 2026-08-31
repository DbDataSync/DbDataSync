# Phase 60 — Pruning the task run log, by age and/or per-mapping row count (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/task-run-log-pruning.md`

## What this covers

A config-file-configured retention policy for `TaskRuns` (+ its cascaded `Logs`), enforced by a new
periodic background service in the API. Two independent, optional caps — age in days and a per-mapping
row count — with simple defaults.

## 1. Config

`ApiOptions` (`DataSync.Api/Configuration/ApiOptions.cs`), matching `StateDbPath`/`StatePort`'s existing
`appsettings`/environment pattern:

```csharp
public int? RunRetentionDays { get; init; }        // null = no age cap
public int? RunRetentionMaxPerMapping { get; init; } // null = no count cap
```

Read in `FromConfiguration` under the existing `DataSync` config section, with defaults applied when
unset (exact numbers — see Open questions) rather than defaulting to "no limits."

## 2. The prune query

Per (`TaskName`, `MappingName`) pair:

- **Age**: delete `TaskRuns` rows with `StartedAtUtc` older than `RunRetentionDays`, excluding any row
  still `Running`/in-flight.
- **Count**: keep the most recent `RunRetentionMaxPerMapping` rows for that pair (by `StartedAtUtc`),
  delete the rest — a per-mapping cap, not a global one, so one noisy mapping can't crowd out another's
  history.
- **Cascade**: delete the corresponding `Logs` rows (`WHERE RunId IN (...)`) for every `TaskRuns` row
  being pruned — `Logs` has no `ON DELETE CASCADE` (SQLite, unenforced FK), so this has to be explicit,
  in the same transaction as the `TaskRuns` delete.
- Both caps apply independently; a row pruned by either is pruned.

## 3. The background service

New `RunPruningService : BackgroundService`, alongside `SchedulerService`/`RunMonitorService`
(`DataSync.Api/Services`) — same `PeriodicTimer` shape those already use, on a coarse interval (hourly or
daily; pruning isn't time-sensitive). Runs entirely within the API process, consistent with phase 39's
single-writer state-store ownership — no new process or ownership question.

## What this phase does not build

- Verification result pruning (parquet files + `VerificationResultStore` index) — a separate, already-
  named gap (phase 43's retrospective: "nothing deletes a result today"). A verification run's `TaskRuns`
  row *will* be pruned by this phase; its parquet file and index entry will not be — a real orphan this
  phase creates without cleaning up, flagged rather than silently accepted.
- A UI settings surface for retention — config-file only, per the resolved decision.
- Failed-run protection (keeping failures longer than successes) — not part of "simple defaults," a
  reasonable future refinement.

## How to verify when built

- A mapping with more `TaskRuns` rows than `RunRetentionMaxPerMapping` gets pruned down to exactly that
  count, oldest first; a quiet mapping under its cap is untouched even while a noisy sibling mapping is
  being pruned.
- A `TaskRuns` row older than `RunRetentionDays` is pruned regardless of count; one within the age window
  survives regardless of how many other rows exist for that mapping.
- Pruning a `TaskRuns` row deletes its `Logs` rows too — no orphaned log lines survive a pruned run.
- A currently-running run is never pruned, even if it would otherwise be the oldest/excess row.
- Both caps set together: a row violating either is pruned; a row violating neither survives.
- Both caps unset (or defaults applied): confirm the actual default behavior matches whatever default
  numbers get chosen.
- Full suite green.

## Open questions

- Exact default values for `RunRetentionDays` and `RunRetentionMaxPerMapping`.
- Pruning interval for `RunPruningService` (hourly vs. daily vs. configurable).
