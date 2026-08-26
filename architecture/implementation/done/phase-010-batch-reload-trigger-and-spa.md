# Phase 10 — Batch Reload: Backfill Trigger & SPA

**Status**: Complete
**Plan reference**: `architecture/implementation-plan.md` § Backlog ("Batch reload"); design history
in `architecture/implementation/done/phase-008-work-queue-schema.md`'s "Design history"; the driver
pieces this phase wires up are described in
`architecture/implementation/done/phase-009-batch-reload-writers-segments.md`. Corresponds to what the
design review's Build Order called **"Phase D — Standalone reload replications, SPA, polish,"** plus
the Backfill HTTP trigger endpoint, whose behaviour that Build Order described but never assigned to a
lettered phase. This document was written as a plan before implementation and rewritten as a
retrospective after it, per `architecture/implementation/README.md`.

## What was built

This is the phase where Phase 8's queue, Phase 9's segments and writers, and the SPA come together
into something an operator can actually trigger. Batch reload is now a complete, usable feature.

**Per-item pipeline overrides (`DataSync.State`, migration 2)**. `WorkQueue` gains nullable
`ReaderKind`/`CacheKind`/`WriterKind` columns, surfaced as a `WorkItemKinds` record on `WorkItem` and
an optional argument to `WorkQueueStore.Enqueue`. See "Design gap found" below — this was not in the
plan and turned out to be load-bearing.

**`BackfillRequest` + `BackfillService` + `POST /api/replications/{name}/mappings/{mappingName}/backfill`**.
Engine-neutral throughout: Kinds are strings the caller got from the capabilities endpoint, and
segments are structural descriptors, with no SQL and no MSSQL terminology anywhere in the request. The
handler validates the mapping, expands any `Auto` segment against the real source (one `MIN`/`MAX`
round-trip, reusing the precedent that the API legitimately opens driver connections for metadata
browsing), enqueues one item per resulting segment, and nudges a worker into existence — never on the
request's critical path. `202` with one `RunId` per segment; `404` for an unknown replication or
mapping; `400` for a request that can't produce runnable work. It never touches `ConfigRepository`, so
a backfill produces no git commit — it's a run, not a configuration change.

**`DriverConnectionFactory`**, extracted from `MetadataService` so credential resolution and driver
connection opening exist in one place rather than being copied into `BackfillService` as a third
variant.

**Worker segment wiring (`DataSync.TaskRunner`)**. `RunExecutor` now resolves each work item's
pipeline (item override ?? replication config), determines what to read as either the item's own
segment, a standalone reload replication's configured `segments` list, or "no segment", and injects the
segment as JSON under `SegmentSerializer.SegmentOptionKey` into per-iteration copies of **all three**
roles' options — the reader to scope what it reads, the writer to know which target rows the reload is
accountable for. The configured options dictionaries are never mutated; they're shared across every
item the worker processes.

**`IStagingProvider.CleanupAsync`**. A single pass can now stage more than once (a standalone reload
iterating its segments), so staging can no longer rely on connection teardown to clean up after it.
Implemented for MSSQL as a `DROP TABLE IF EXISTS` of the session temp table, called in a `finally` per
segment. This also removes the blocker on the per-consumer-slot connection reuse that
`phase-008-work-queue-schema.md` flagged as a future optimization.

**Standalone reload replications**. A replication whose reader is `MsSqlBatchReload` with a
`segments` reader option re-reads those segments on its ordinary schedule, iterated within one Primary
pass per mapping. `SchedulingEvaluator`/`SchedulerService` needed no changes, exactly as the plan
predicted. `Auto` entries in that list are expanded by the worker at run time rather than being
resolved when the config was saved — the source's value range moves as the table does.

**SPA**:
- `driverKinds.ts` is **deleted**. Every Kind picker — replication settings, the new backfill form, and
  the defaults a newly-created replication starts with — now comes from
  `GET /api/connections/{name}/capabilities`, annotated with the capability that distinguishes the
  choices (`segmentable`, `reconciling`, `upsert-only`). `useReplicationCapabilities` resolves readers
  against the replication's source connection and staging/writers against its target, since that's
  where each actually runs.
- `JsonOptionsEditor` — a raw-JSON textarea per reader/staging/writer `Options` block, with invalid
  JSON reported upward so Save is blocked rather than silently saving the last valid value. This is the
  authoring surface for a standalone reload's segment list, and incidentally closes a pre-existing gap:
  the Watermark reader's required `watermarkColumn` option has never had any UI.
- `BackfillForm` — mapping picker, segment-mode chooser (Full/List/Range/Auto with the fields each mode
  needs), and capability-driven Kind pickers whose defaults are chosen *by capability* (a segmentable
  reader, a reconciling writer), not by name. Warns when a selected writer is upsert-only, since that
  silently changes what a reload means.
- Run history gains Kind / Mapping / Segment columns and a `RunKindBadge`, so a replication's
  incremental passes and its reloads are distinguishable at a glance.

## Design gap found: the queue had nowhere to put a backfill's pipeline

The plan had `BackfillRequest` carrying Reader/Cache/Writer Kinds, but nothing downstream could
receive them — `WorkQueue` (Phase 8) carries a segment and nothing else, and the worker read its
pipeline solely from the replication's config. That makes a backfill of an incrementally-synced
replication meaningless: it would run `MsSqlChangeTracking`, which reports *changes since a watermark*
rather than a segment's rows, and `MsSqlMerge`, which never removes anything.

So which pipeline to run is a property of the unit of work, not of the replication, and the queue
needed to say so. Resolved with migration 2 adding three nullable columns rather than by overloading
`SegmentJson` into a general payload blob (which would have made the column's name a lie) — NULL means
"use the replication's configured pipeline", which is every Primary item. This is scope the plan didn't
anticipate; it's recorded here rather than split into its own phase because backfill is not triggerable
at all without it.

## Other decisions made during implementation

- **`SegmentLabel` is `Describe()` for every backfill segment, including `Full`.** Phase 8's schema
  comment anticipated reusing the `''` no-segment sentinel for a Full-mode backfill. `''` is stored as
  NULL in `TaskRuns.SegmentLabel`, which would render a full reload's history row with a blank segment
  column — indistinguishable from an incremental pass at a glance. `"full"` costs nothing (the
  uniqueness index already separates the two by `RunKind`) and reads correctly.
- **`Auto` is expanded in two different places, deliberately.** A backfill expands at *enqueue* time so
  each bucket becomes its own queue row, schedulable and retryable independently. A standalone reload
  replication expands at *run* time, because its trigger is a scheduler tick that knows nothing about
  segments and its segment list is config, not a request. Both go through the same
  `ISegmentExpandingReader`.
- **The SPA now requires a connection to exist before a replication can be created.** Previously the
  new-replication form seeded Kinds from the hardcoded list. With that list gone there is no driver to
  ask until a connection exists, so Create is disabled with an explanation. The golden path already
  creates connections first, and "configure a pipeline before knowing the engine" was never meaningful.
- **Backfill request validation rejects rather than queues.** A segment naming a column that doesn't
  exist can only ever fail, and the caller is synchronously present to be told — enqueueing it would
  turn an immediate `400` into a failed run discovered later.

## Real bugs found

1. **`npx tsc --noEmit` — the SPA type-check this project has been running since Phase 6, and which
   this phase's own plan listed as a verification step — checks nothing.** The root `tsconfig.json` is
   a solution-style file with `"files": []` and two project references, so the command type-checks an
   empty file list and exits 0. The real check is `tsc -b` (what `npm run build` runs).
2. **Found immediately by actually running `tsc -b`: `StatusBadge`'s `classByStatus` was missing
   `Queued`**, a `Record<RunStatus, string>` that hadn't compiled since Phase 8 added the status. At
   runtime a queued run rendered its badge with `class="badge undefined"`. Fixed, and `Queued` now
   renders as a real status — which this phase needed anyway, since a backlog of queued segments is
   the normal state after an Auto backfill.
3. **`StateDatabaseTests.ReopeningExistingDatabase_DoesNotRerunMigrations` asserted `user_version == 1`**,
   so adding any migration broke it for the wrong reason. Rewritten to compare the version after
   reopening against the version after the first open, which is what the test was actually about.

## How this was verified

- **Non-integration: 140 tests** (up from 137). New: `WorkQueueStore` round-tripping Kind overrides and
  distinguishing different segments of one mapping from repeat enqueues of the same one.
- **Integration: 42 tests** (up from 33).
  - `RunExecutorIntegrationTests` (worker level, real MSSQL): a backfill running its own reader and
    writer over a range segment repairs target drift the incremental sync structurally cannot see,
    leaves out-of-segment rows alone, **and leaves the incremental watermark byte-identical** — the
    core guarantee `RunKind.Primary`-only `SetWatermark` gating exists for. Plus a standalone reload
    replication iterating its configured segments within one pass, and expanding an `Auto` segment
    against the live source.
  - `BackfillIntegrationTests` (HTTP level, real spawned worker, real MSSQL): Primary and Backfill
    triggered concurrently against one replication both succeed and the Backfill row carries its kind,
    mapping and segment label; two identical backfill triggers collapse into one run rather than
    reloading the segment twice; an `Auto` backfill expands into one run per bucket with distinct
    labels and every row landing exactly once; and the 404/400 rejection paths.
- **Playwright: 10 tests** (up from 7), all green. New: triggering a backfill through the real browser
  to repair a row deleted from the target behind the replication's back (asserting the Kind pickers
  defaulted by capability, and that history shows the `Backfill` badge and segment label); a following
  "Run Now" reading **0 rows**, which is the browser-observable proof the watermark was undisturbed;
  and the settings panel offering live driver capabilities and refusing to save invalid options JSON.
- `dotnet build` clean (0 warnings); `tsc -b` clean; `oxlint` back to its pre-existing two warnings.

## What's explicitly not built

Multi-segment submission in the SPA (the API takes an array from day one, so this is additive); a
structured, non-JSON Options editor; run-history filtering beyond the badge; live-watching more than
one run at a time (unchanged from Phase 8 — the panel watches the first RunId, and an `Auto` backfill
now makes that limitation more visible than it was). Cancelling an already-claimed backfill segment
still falls back to killing the whole worker, unchanged from Phase 8.

## Notes / things to revisit later

- Phase 8's `EnsureWorkerRunning` exit race (5 empty polls) now has a second independent source of
  "new work might arrive any moment" in backfill triggers. The concurrency tests above exercise it and
  did not reproduce a stranding, but this is still the narrowed-not-eliminated race Phase 8 documented;
  a worker heartbeat remains the real fix.
- `useReplicationCapabilities` falls back to the first configured connection when a replication has no
  mappings yet. With one registered driver that is always the right answer; with several it could offer
  Kinds the replication's actual connections don't support. The Kind picker already marks a configured
  Kind the driver doesn't advertise, so this degrades visibly rather than silently, but it wants
  revisiting when a second driver exists.
- Per-consumer-slot connection reuse is now unblocked by `CleanupAsync` and remains unimplemented.
