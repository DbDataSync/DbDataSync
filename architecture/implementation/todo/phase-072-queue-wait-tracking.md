# Phase 72 — Queue-wait tracking, and duration redefined to exclude it (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/queue-wait-tracking.md`

## What this covers

A genuine `ClaimedAtUtc` on `TaskRuns`, distinct from the enqueue-time `StartedAtUtc` it's confused with
today; queue-wait as its own exposed figure; and "duration" (phase 36's run-metrics percentiles,
`RunsPanel`'s duration column) redefined to `EndedAtUtc - ClaimedAtUtc` instead of
`EndedAtUtc - StartedAtUtc`, since the latter has always silently included however long a run sat queued.

## 1. `TaskRuns.ClaimedAtUtc`

New nullable column, set inside `TaskRunStore.BeginRun` (`src/DataSync.State/TaskRunStore.cs:161-170`) —
the exact point a worker actually starts executing a claimed item, today only flipping `Status` to
`Running`:

```sql
ALTER TABLE TaskRuns ADD COLUMN ClaimedAtUtc TEXT NULL;
```

```csharp
public void BeginRun(Guid runId, int? pid) =>
    database.Retry(() =>
    {
        // ...
        using var cmd = database.Command(connection,
            "UPDATE TaskRuns SET Status = $status, Pid = $pid, ClaimedAtUtc = $claimedAt WHERE RunId = $runId;");
        cmd.Bind(database, "claimedAt", DateTimeOffset.UtcNow.ToString("O"));
        // ... existing bindings unchanged
    });
```

Null for a run that never reached `Running` (e.g. one that stays `Pending`/gets cancelled before a worker
claims it) — the same "absent means it never happened" shape other optional `TaskRuns` columns already
use.

`TaskRunRecord` (`DataSync.State/Models.cs`) gains the matching field; `RunsController` returns the
record directly today, so no separate DTO change needed (confirmed by phase 71's own note that this is
already true for its new fields).

## 2. Duration redefined

Every place that currently computes `EndedAtUtc - StartedAtUtc` as "duration" moves to
`EndedAtUtc - ClaimedAtUtc`:

- Phase 36's run-metrics aggregate endpoint (duration percentiles).
- `RunsPanel.tsx`'s duration column.
- Any other consumer found while implementing — audit for `StartedAtUtc` used in a duration calculation,
  not just the two named here.

A run with no `ClaimedAtUtc` (never reached `Running`) has no duration to report, the same as today's
behavior for a run with no `EndedAtUtc`.

## 3. Queue wait, exposed

`QueueWaitMs = ClaimedAtUtc - StartedAtUtc`, computed the same way the new duration is — either as a
computed API field or client-side from the two raw timestamps (implementation's call; either is fine,
consistency with how phase 71's `PreviousWatermark`/`NewWatermark` were exposed — raw values, not
pre-computed deltas — is the more consistent choice unless there's a reason to differ).

Not mandated in the UI, but the natural, low-cost home is phase 62's existing expandable per-run timing
detail in `RunsPanel` — worth adding there if it's a small addition once the data exists, per the
planning doc's own note.

## What this phase does not build

- Renaming `StartedAtUtc` itself (e.g. to `EnqueuedAtUtc`) — left as a known, separately-tracked naming
  debt. The column's *meaning* doesn't change (it already means "enqueued at"); only what's computed from
  it does.
- Any change to `WorkQueue`'s own `EnqueuedAtUtc`/`ClaimedAtUtc` — already correct, untouched.
- Backfilling `ClaimedAtUtc` for historical `TaskRuns` rows that predate this column — they simply have no
  duration/queue-wait figure available, the same as any other newly-added optional column.

## How to verify when built

- `BeginRun` sets `ClaimedAtUtc`; a run that's enqueued but never claimed has it null.
- Duration for a run with a real queue wait (e.g. deliberately delay claiming in a test) reflects only
  the execution span, not the wait — provable by comparing against the old `StartedAtUtc`-based figure
  for the same run and confirming they now differ by the queue-wait amount.
- `QueueWaitMs`/the raw timestamps are retrievable for a run with a real gap between enqueue and claim.
- Phase 36's run-metrics percentiles and `RunsPanel`'s duration column both reflect the new definition —
  a run-history entry with known queue wait shows a shorter duration than it would have under the old
  computation.
- Full suite green, including any phase-36/`RunsPanel` tests that assert specific duration values against
  fixture data — these need updating to the new computation, not just passing incidentally.

## Open questions

- Whether `QueueWaitMs` is API-computed or left as two raw timestamps for the client to diff — pick
  whichever is more consistent with phase 71's precedent.
- Whether to surface queue wait in `RunsPanel`'s existing timing detail now, or leave it API-only for a
  later follow-on (same "store first, view later" split phases 59/62 and 71 already used).
