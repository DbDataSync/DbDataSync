# Phase 9 — Durable Work Queue & Per-Mapping Run Model

**Status**: Complete
**Plan reference**: `architecture/implementation-plan.md` § Phase 8 backlog ("Batch reload"); design
history in the plan file used for this feature's design review (four passes, three user-driven
architectural corrections — see "Design history" below).

## What was built

This phase delivers the foundational rework that batch reload's design required — the state-layer
schema and the entire orchestration model — unifying what an earlier draft plan called "Phase A"
(schema/lock-model rework) and "Phase C" (the worker/queue rework) into one coherent, shippable unit,
since they turned out to be too tightly coupled to land independently without a broken intermediate
state. Batch reload's own new pieces (segment types, the two new writers, the Backfill trigger
endpoint, SPA) are **not** part of this phase — see "What's not built yet" below.

**State layer (`DataSync.State`)**:
- `RunKind` enum (`Primary | Backfill`). **Primary is now scoped per table mapping, not per
  replication** — a "Primary pass" is "this mapping's next incremental pass," not "this replication's
  next pass over every mapping." This was the single biggest structural decision this phase, and goes
  beyond what was originally drafted: it directly fixes, for ordinary incremental sync, the same
  head-of-line problem that motivated Backfill's per-mapping design — one slow or locked mapping no
  longer blocks every other mapping's schedule.
- `RunLocks` → composite key `(TaskName, RunKind, MappingName)`, no sentinel value needed (every row
  is genuinely mapping-scoped now).
- `TaskRuns` gains `RunKind`, `MappingName`, `SegmentLabel` columns, plus a new `Queued` status.
- New `WorkQueue` table — a durable, SQLite-backed cross-process queue. The API (handling triggers and
  scheduled due-ness) and the TaskRunner worker process it spawns communicate exclusively through
  `DataSync.State`, same as always — this is a table, not a new IPC mechanism.
- New `WorkQueueStore`: `Enqueue` (idempotent while an equivalent item is in-flight; writes the
  corresponding `TaskRuns` row at `Queued` in the same transaction, so a backlog is visible in run
  history immediately), `TryClaimNext` (two-step select-then-conditional-update, mirroring
  `RunLockStore.TryAcquire`'s existing `ON CONFLICT DO NOTHING` idiom; a `NOT EXISTS` pre-filter skips
  any mapping that already has another Claimed/Running item), `MarkRunning`/`MarkDone`/`MarkFailed`,
  `ReleaseClaim` (gives a claim back to Pending on a lost `RunLocks` race), `TryCancelPending`,
  `HasOutstandingWork`.
- `TaskRunStore` gains `BeginRun` (UPDATE-based Queued→Running transition — there's no INSERT-based
  "start a run" method anymore; every run's row now originates from `WorkQueueStore.Enqueue`),
  `GetLastPrimaryStartByMapping` (one batched query per replication per tick — not one query per
  mapping — for `SchedulerService`'s due-ness check at scale), `GetMappingRunHistory`,
  `GetActiveRuns`, `GetRecentlyEndedRuns` (see the RunMonitorService bug below).

**Orchestration (`DataSync.TaskRunner`, `DataSync.Api`)**:
- `RunExecutor` is now queue-driven: `ExecuteWorkerAsync(taskName, degreeOfParallelism, ct)` runs a
  producer loop (claims from `WorkQueueStore`) feeding a bounded `System.Threading.Channels.Channel`,
  drained by `degreeOfParallelism` concurrent consumers — not a fixed-collection fan-out
  (`Parallel.ForEach`/`Task.WhenAll` over an up-front list), since a replication's backlog can grow
  continuously while being drained. The worker exits once the queue is confirmed empty for its task
  (with a grace period — see the race condition below) and is re-spawned on demand.
- `ProcessSupervisor` is now tracked per replication name, not per run:
  `EnsureWorkerRunning(taskName)` (idempotent — no-op if a live worker is already tracked) and
  `TriggerReplication(name)` (enqueues a Primary pass for every mapping, then ensures a worker) replace
  the old one-process-per-triggered-run `TriggerRunAsync`. `CancelRun` cancels a still-Pending item
  directly (cheap); a Claimed/Running item has no per-item cancellation lever yet, so the fallback is
  killing the whole worker process (documented, accepted v1 limitation, same spirit as this method's
  previous version).
- `SchedulerService` evaluates Continuous due-ness **per mapping** (one batched query per replication
  per tick) and Periodic due-ness per replication as before (anchored to the most recent of all its
  mappings' last Primary starts), batch-enqueueing whichever mappings are due.
- `RunMonitorService` no longer uses `Process.HasExited` at all (meaningless once one process backs
  many concurrently-active `RunId`s) — it polls `TaskRuns.Status` transitions instead. See the fix
  below for why this needed two detection paths, not one.
- API contract change: `POST /api/replications/{name}/runs` now enqueues one Primary pass **per table
  mapping** and returns `{ runIds: [...] }` (plural) instead of a single `runId` — there is no more
  single "the run" for a whole replication. `AlreadyRunning` is no longer a trigger-rejection outcome;
  enqueueing is idempotent per mapping instead.

## Design history

This is the fourth design pass for the batch-reload feature area. Three prior drafts were reviewed and
rejected by the user, each surfacing a real, specific architectural problem — this phase is the
foundational piece of the accumulated result (full segment/writer/SPA design lives in the plan file
used for review, to be implemented in a later phase):

1. A segment's scope predicate can't live only inside a MERGE's `WHEN` clause (doesn't limit what the
   engine scans, risking Halloween-problem-adjacent, expensive plans) — resolved via a CTE-scoped
   target in the design for a later phase's writer.
2. A simple delete+insert writer is needed, not just MERGE-based options — added to that same
   later-phase design.
3. **Processes must do their own internal parallelism and looping, not be spawned-and-torn-down per
   run — hundreds of mappings, with backfills continuously queued for many of them, ruled out both
   one-process-per-trigger and a naive bounded `Parallel.ForEach`.** This is what this phase actually
   delivers: the durable `WorkQueue` + bounded producer/consumer worker model.
4. Nothing should be scoped as "unnecessary because v1 is MSSQL-only" — reflected in this phase by
   keeping every new abstraction (`RunKind`, the lock model, the queue) engine-neutral; the
   capability-discovery endpoint that replaces the SPA's hardcoded Kind lists is scoped to a later
   phase alongside the driver-specific pieces it's meant to expose.

## Two real bugs found while getting the Playwright suite green again

Both were found via `01mplay-workqueue` runs failing test 5 ("trigger a run and watch it complete
live") and diagnosed via a direct manual repro (a hand-run API instance driven with `curl`, bypassing
the browser entirely) rather than iterating blind through the browser test.

1. **Log lines could go missing for a run that had already completed.** `LogWriter.Flush()` was only
   called once, at the very end of `ExecuteWorkerAsync`'s entire lifetime — but a single `TaskRuns` row
   can now go terminal (and stop being polled) long before the *process* itself exits, since the same
   process immediately moves on to its next claimed item instead of tearing down. A poller could see
   `Status=Succeeded` before that run's log lines were durably in the `Logs` table. Fixed by flushing
   per-item, immediately after each item's final log line and before its `CompleteRun` call — so a
   terminal status is never visible before the logs explaining it are.

2. **A worker exiting right as new work arrives could strand that work indefinitely.**
   `ProcessSupervisor.EnsureWorkerRunning` is a no-op when a tracked worker process is still alive —
   deliberately cheap, so a "Run Now" click or scheduler tick doesn't pay a process-spawn cost when a
   worker is already looping. But the original `ProduceAsync` loop exited the instant it observed an
   empty queue, with no grace period — so a worker that happened to finish draining at almost exactly
   the moment a new item was enqueued could exit a moment *before* claiming it, and since a manual
   trigger doesn't correspond to anything `SchedulerService` would notice as newly "due," nothing else
   would ever re-trigger a worker for that stranded item. This is precisely what test 5 hit: the
   scheduler auto-triggered and completed a run for the replication's one mapping (visible in the
   captured `run-history-table` snapshot as `Succeeded, 2, 2` *before* the test even clicked "Run
   Now"), that worker exited immediately afterward, and the test's own manual trigger's item then sat
   unclaimed. Fixed with a grace period: the producer loop now requires `EmptyPollsBeforeExit` (5)
   *consecutive* empty polls, roughly 5 seconds of confirmed-empty queue, before actually exiting —
   this narrows the race to needing an enqueue to land during that specific window rather than at any
   moment, without fully eliminating it structurally. A complete fix (e.g. a worker heartbeat
   `EnsureWorkerRunning` could check against, not just OS process liveness) is real follow-on work.

   A second, related gap surfaced by the same investigation: `RunMonitorService`'s original
   `_watching`-based completion detection could miss a run *entirely* if its whole lifecycle (Queued →
   Running → terminal) completed between two 1-second polling ticks — a run that's never once observed
   as active never triggers a `runCompleted` broadcast. This wasn't possible in the old
   one-process-per-run model (a process's `Process.HasExited` transition reliably outlives the actual
   work, since process teardown itself takes measurable time), but is a real possibility now that a
   claimed unit of work can complete in well under a second. Fixed by adding
   `TaskRunStore.GetRecentlyEndedRuns(sinceUtc)` — each tick, `RunMonitorService` also checks for runs
   that ended within a trailing window, independent of whether they were ever caught as "active," and
   notifies any not-yet-notified ones (tracked via an in-process `_notified` set).

Both fixes were verified by three consecutive full Playwright runs, all green, after being unable to
reproduce the failure via direct REST/curl (which doesn't exercise `RunMonitorService`'s polling
cadence at all — the SPA's live-log viewer depends on it, but a synchronous poll doesn't).

## How this was verified

Full solution rebuild (clean), the complete existing suite adapted to the new signatures (81→103
non-integration tests, 19 integration tests — all green, including three new dedicated test files:
`WorkQueueStoreTests.cs` for claim/enqueue mechanics — concurrent-claim safety, per-mapping
serialization via the `NOT EXISTS` pre-filter, idempotent enqueue — and updated `RunLockStoreTests.cs`/
`TaskRunStoreTests.cs`/`RunExecutorTests.cs` for the per-mapping model), the two integration tests from
Phase 7 (`RunLifecycleIntegrationTests`, `ConcurrentRunsIntegrationTests`, `CrossInstanceEndToEndTests`)
updated for the new `{runIds: [...]}` trigger response shape, and three consecutive full Playwright
golden-path runs (7/7 passing each time) confirming the fixes above hold up under real
browser-driven timing, not just synchronous test harnesses.

## Decisions made this phase

- **Connections are opened fresh per unit of work, not pooled across mappings in an app-level cache.**
  The old `RunExecutor` cached one connection per `(role, ConnectionName)` and reused it sequentially
  across every mapping in one invocation — a pattern that doesn't compose with independent,
  potentially-concurrent units of work. `Microsoft.Data.SqlClient`'s own connection pooling (already
  present) provides the reuse efficiency the manual cache was chasing, without the
  shared-mutable-connection hazard. (The plan's more refined "one long-lived connection per consumer
  slot, reused across many items" is a further optimization, not yet built — flagged in "What's not
  built yet.")
- **`ExecuteWorkerAsync`'s own exit code no longer reflects individual item outcomes.** Since one
  process can now process many independent units of work with independent success/failure, "the
  process's exit code" stopped being a meaningful aggregate; per-item outcomes live entirely in
  `TaskRuns`/`WorkQueue`, and the process exit code only distinguishes "the worker itself failed to
  even start" (e.g., a config load failure before any item could be claimed) from "the worker ran and
  drained."
- **`--run-id` dropped from the TaskRunner CLI**, replaced by `--degree-of-parallelism` (default 4). A
  single externally-supplied run id stopped being meaningful once one process mints many.

## What's not built yet

Everything specific to batch reload itself — the segment type hierarchy, the two new writers
(CTE-scoped reconciling MERGE, delete+insert), the capability-discovery endpoint, the Backfill trigger
endpoint and CLI surface, and all SPA work (Options editor, Backfill trigger form, `RunKind` badges in
run history) — is designed (see the plan file referenced above) but not implemented. This phase is
purely the foundation those pieces are built on.

## Notes / things to revisit later

- The exit-race fix (5-second grace period) narrows but does not eliminate the "worker exits just as
  new work arrives" race. A robust fix needs some form of worker heartbeat that `EnsureWorkerRunning`
  can check against, not just OS process liveness — real follow-on work, not built here.
- `RunMonitorService`'s `_notified`/`_lastLogId` in-memory sets grow unboundedly for the lifetime of
  the API process. Acceptable for now; a very long-lived instance processing a large number of runs
  would eventually want these pruned or replaced with a persisted "notified" flag.
- Per-consumer-slot connection reuse (vs. today's open-fresh-per-item) remains a real, flagged
  optimization for a future pass, along with the temp-table-cleanup discipline it would require (a
  reused connection's session-scoped `#Staging_{guid}` tables must be explicitly dropped, since
  connection disposal no longer does it implicitly).
