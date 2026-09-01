# Tracking queue-wait time, and a naming problem it surfaces

**Status: draft, 2026-08-31 — one real design fork, not yet decided.**

## What's there today, confirmed by reading the code

- `WorkQueue` already has both `EnqueuedAtUtc` and `ClaimedAtUtc`, and rows persist after completion
  (`MarkDone` only flips `Status`, nothing deletes a `WorkQueue` row) — the raw data for queue-wait time
  exists and isn't discarded. Nothing computes or exposes the delta anywhere, though: no `QueueLatency`
  field, no API surface, no UI.
- **`TaskRuns.StartedAtUtc` is actually set at enqueue time, not at execution start.**
  `WorkQueueStore.Enqueue` inserts the `TaskRuns` row with `Status = Queued` and
  `StartedAtUtc = DateTimeOffset.UtcNow` right then, before any worker has claimed anything. `BeginRun`
  (called when a worker actually starts the item) flips `Status` to `Running` but never touches
  `StartedAtUtc`. So today's run "duration" — `EndedAtUtc - StartedAtUtc`, what phase 36's run-metrics
  computes and `RunsPanel` displays — silently includes however long the item sat queued. The column name
  promises something the value doesn't deliver.

## The fix, in shape

- **New `TaskRuns.ClaimedAtUtc`**, set inside `BeginRun` — the genuine "a worker started executing this"
  timestamp, named to match `WorkQueue.ClaimedAtUtc` for consistency.
- **`QueueWaitMs = ClaimedAtUtc - StartedAtUtc`**, computable and exposable once that column exists.

## The one real fork: does "duration" get redefined?

- **(A) Purely additive.** Add `ClaimedAtUtc` and a queue-wait figure; leave `StartedAtUtc` and every
  existing "duration" computation (phase 36's percentiles, `RunsPanel`'s duration column) exactly as they
  are today — silently inclusive of queue wait, unchanged. Simplest, no risk to an already-shipped metric,
  but leaves the misleading part of the problem in place: "duration" still isn't what it sounds like.
- **(B) Redefine duration to exclude queue wait.** `EndedAtUtc - ClaimedAtUtc` becomes the "how long did
  the run itself take" figure phase 36/`RunsPanel` show; `StartedAtUtc`'s role becomes "when this was
  queued" (arguably deserves its own rename too, though that's a much larger touch — see below); queue
  wait becomes its own explicit, separate number. More honest, but **changes the meaning of an
  already-shipped, presumably-relied-on metric** (phase 36's duration percentiles) without anyone having
  asked for that specifically — a real product decision, not an implementation detail this doc should
  settle on its own.

**Not decided here.** (B) is the more correct fix for the naming problem this investigation surfaced, but
redefining "duration" is bigger than "add queue-wait tracking," and deserves an explicit yes rather than
being bundled in as a side effect.

## Out of scope either way

- Renaming the `StartedAtUtc` column itself (to something like `EnqueuedAtUtc`) — a much larger touch
  (every reader of that column, migrations, API contracts) than this doc is scoping. Worth its own,
  separate consideration if (B) is chosen and the half-fixed naming bothers whoever picks this up.
- Any change to `WorkQueue` itself — its `EnqueuedAtUtc`/`ClaimedAtUtc` are already correctly named and
  already correct; this is purely about giving `TaskRuns` the same honesty `WorkQueue` already has.

## Open questions

1. (A) or (B) above.
2. If (B): whether `StartedAtUtc` gets renamed in the same pass or left as a known, separately-tracked
   naming debt.
3. Where queue-wait shows in the UI, if anywhere beyond being queryable — not asked for explicitly.

**Next step**: resolve question 1, then this is ready for an implementation phase doc.
