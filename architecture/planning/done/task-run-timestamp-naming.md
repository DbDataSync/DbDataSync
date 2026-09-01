# Fixing `TaskRuns`' timestamp names to match what they actually record

**Status: resolved — ready for an implementation phase doc.**

## The ask

Verbatim: "Fix all of the names. I don't want weird discrepancies. Claimed at should be based on
claim, started at should be based on when work started, the durations should be queue time or wait
time and a processing time."

## What phase 72 shipped, and why it's wrong

Phase 72 added `TaskRuns.ClaimedAtUtc`, written inside `TaskRunStore.BeginRun` — the call a worker
makes once it has already claimed an item and is about to execute it. So the column named
`ClaimedAtUtc` on `TaskRuns` actually records **execution start**, not the claim. `WorkQueue` already
has its own `ClaimedAtUtc`, written in `WorkQueueStore.TryClaimNext` — the genuine claim moment, earlier
than `BeginRun` by however long the worker took to acquire the run lock and get going. Same name, two
tables, two different real moments. That's the discrepancy.

`TaskRuns.StartedAtUtc` has a matching problem, older and previously left as known debt (see phase 72's
"What this phase did not build," and this doc's predecessor's "Out of scope"): it's written by
`WorkQueueStore.Enqueue`, at the moment work is queued, not at the moment a worker starts it. So
`StartedAtUtc` means "enqueued," and `ClaimedAtUtc` means "started" — both misnamed, in a way that
happens to be internally consistent with each other by accident, not by design.

## The fix

Three timestamps on `TaskRuns`, each written at the moment its name says, mirroring `WorkQueue`'s own
`EnqueuedAtUtc` → `ClaimedAtUtc` → (implicitly) running:

| Column          | Written by                                    | Moment                          |
|-----------------|------------------------------------------------|----------------------------------|
| `EnqueuedAtUtc` | `WorkQueueStore.Enqueue`                       | Work is queued                  |
| `ClaimedAtUtc`  | `WorkQueueStore.TryClaimNext`                  | A worker claims the item        |
| `StartedAtUtc`  | `TaskRunStore.BeginRun`                        | The worker starts executing it  |
| `EndedAtUtc`    | `TaskRunStore.CompleteRun` (unchanged)          | The run finishes                |

`EnqueuedAtUtc` is a genuinely new column — but backfillable, unlike phase 72's `ClaimedAtUtc` was:
every existing row's current `StartedAtUtc` value **is** its enqueue time (that's the whole bug), so the
migration can copy it forward instead of leaving history null. `StartedAtUtc` keeps its physical column
name but changes what's written into it and when — no rename needed, no dialect-specific
`RENAME COLUMN`/`sp_rename` machinery to build. `ClaimedAtUtc` keeps its column too; only the write site
moves, from `BeginRun` to `TryClaimNext`.

`TryClaimNext`'s claim `UPDATE` on `WorkQueue` needs a sibling `UPDATE TaskRuns SET ClaimedAtUtc = $now
WHERE RunId = $runId` in the same transaction — `TryClaimNext` doesn't wrap its update in one today (it
uses two separate `database.Retry` calls: select, then a conditional update). Worth wrapping both writes
in a transaction at that point, for the same reason `BeginRun`'s single-statement `UPDATE` and
`Enqueue`'s transaction exist: a run should never be `Claimed` in one table without a claim time in the
other.

## Durations: exactly two, honestly named

- **Queue time / wait time** = `StartedAtUtc - EnqueuedAtUtc`. How long the item sat queued before a
  worker started it — claim latency folded in, since nobody asked for a third figure and claim-to-start
  is normally sub-millisecond.
- **Processing time** = `EndedAtUtc - StartedAtUtc`. The run itself, replacing what phase 72 called
  "duration" (which it had computed as `EndedAtUtc - ClaimedAtUtc` — the right idea, wrong column, and a
  name generic enough to mean anything).

Whichever consumer exposes these — `RunMetricsStore`'s percentiles, `RunsPanel`'s duration column and
tooltip — should call them "processing time" and "queue time"/"wait time" rather than "duration," so the
label matches phase 72's own intent without still being the vague word that let the first mismatch go
unnoticed.

## Consumers that need to move from `StartedAtUtc`'s old meaning to `EnqueuedAtUtc`

Phase 72's own audit found every place that reads `StartedAtUtc` for "when was this asked for" rather
than "when did it start" — these need to switch columns, not rewording, since the meaning they want is
now `EnqueuedAtUtc`:

- `TaskRunStore.PruneRuns`/`DoomedRuns` — ranks retention by "when work was asked for."
- `RunMetricsStore`'s window selection — "which runs belong to the last 24 hours."
- `SchedulingEvaluator` — due-ness from "has it been N seconds since we last asked for this."
- `RunsPanel`'s row timestamp (`clock(r.startedAtUtc)`) — "when a run entered the system."

Everywhere else phase 72 audited (`GetRecentlyEndedRuns`, `ReadLastCompletedPass`, `RunMonitorService`)
reads `EndedAtUtc` or is unaffected.

## Out of scope

- A queue-time/wait-time figure in the metrics card's percentiles (phase 72 left the equivalent
  unbuilt too — still nobody's asked for it).
- Backfilling `ClaimedAtUtc` history — phase 72's reasoning holds: there's nothing to backfill it from,
  since no prior column ever recorded the true claim moment.
- Any change to `WorkQueue`'s own columns, which are already correctly named and timed.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-073-task-run-timestamp-naming.md`.
