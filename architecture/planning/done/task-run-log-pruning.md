# Pruning the task run log, by age and/or row count

**Status: draft, 2026-08-30 — following up on retention, deliberately deferred everywhere it's come up
until now.**

## The ask

Prune the task run log by age (days) or total row count, both configurable by the user, with simple
defaults that work out of the box.

## Why this doc exists now

Retention has been named and deliberately deferred at every point it came up this session:
`run-metrics.md` ("Deleting a run deletes its logs, which is what an operator goes looking for after an
incident... a policy question, not an implementation one"), phase 43's verification results ("nothing
deletes a result today"), and phase 59's new timing columns just inherited the same open question. This is
that policy finally getting decided, for the core case — `TaskRuns` and its log lines.

## What gets pruned, precisely

Two tables, cascaded together — `Migrations.cs` shows `Logs` has no `ON DELETE CASCADE` from `TaskRuns`
(SQLite, no FK enforcement here), so pruning has to delete both explicitly:

- **`TaskRuns`** — one row per (task, mapping, segment) pass, per the earlier "does it track by table or
  task" answer. This is what phase 36's run-metrics and phase 59's new timing columns both live on.
- **`Logs`** — keyed by `RunId`, indexed (`IX_Logs_RunId`). A pruned `TaskRuns` row's log lines are deleted
  with it — losing them *is* the point of pruning, not a side effect to avoid.

**Explicitly out of scope**: verification results (parquet files + their index, phase 43/48). Those have
their own, separately-noted "nothing deletes a result today" gap. A verification run *is* a `TaskRuns` row
too (`RunKind.Verification`), so pruning by age/count will prune that row — but the parquet file and its
`VerificationResultStore` index entry are not touched by this, and would need their own follow-up. Worth
naming so pruning doesn't quietly leave an orphaned index entry pointing at a run that no longer exists,
even though this phase doesn't clean that side up.

## The policy: two independent caps, not a single mode

**Both age and row-count limits apply simultaneously** — whichever is hit first prunes. Not "pick one
mode": an operator might want "never keep more than 100k rows regardless of age" *and* "never keep
anything older than 90 days regardless of volume," together. Either limit can be unset (no cap on that
dimension).

- **Age**: delete `TaskRuns` (+ cascaded `Logs`) rows older than N days, by `StartedAtUtc`.
- **Row count**: **per-mapping, resolved 2026-08-30** — each (task, mapping) pair keeps at most N of its
  own `TaskRuns` rows, oldest pruned first once its own cap is exceeded. Not a global total: a global cap
  would let one noisy, frequently-running mapping crowd out a quiet one's entire history, which is worse
  than the extra query cost of scoping per mapping.
- **Simple defaults**: something that works unconfigured — e.g. 30 days, no count cap (or a generous one)
  — exact numbers are an implementation detail, not decided here.
- **A currently-running or recently-completed-but-still-relevant run is never pruned** — the cutoff only
  ever looks at completed, sufficiently old/excess rows; nothing about an in-flight run's bookkeeping is
  touched.

## Where this runs

A new periodic background service in the API, following the existing pattern (`SchedulerService`,
`RunMonitorService` — both already `BackgroundService`s with a `PeriodicTimer`). Runs on its own coarse
interval (e.g., hourly or daily — pruning isn't time-sensitive the way scheduling is), not on every
request. Fits phase 39's single-writer state model directly: the API already owns the state store, so
this is one more thing that process does to it, no new ownership question.

## Where the setting lives

**Resolved 2026-08-30: a config file**, `ApiOptions`-style (`appsettings`/environment, like
`StateDbPath`/`StatePort` today) — not a UI settings surface. Matches the existing precedent for
operational settings that aren't part of the git-backed replication config, and doesn't need a new
settings screen built just for this.

## Open questions

1. Exact default numbers for age and count.
2. Whether a failed run should be protected longer than a successful one, since it's disproportionately
   the thing an operator goes looking for after an incident — a reasonable refinement, not assumed as
   part of "simple defaults."

**Next step**: ready for an implementation phase doc — a config-file setting, a per-mapping row-count
cap plus an age cap, and a background pruning service following `SchedulerService`/`RunMonitorService`'s
existing shape.

---

# Outcome

Agreed, as `implementation/todo/phase-060-task-run-log-pruning.md`.
