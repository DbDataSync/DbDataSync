# Phase 10 — Batch Reload: Backfill Trigger & SPA (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/implementation-plan.md` § Backlog ("Batch reload"); design history
in `architecture/implementation/done/phase-008-work-queue-schema.md`'s "Design history." Corresponds to
what the design review's Build Order called **"Phase D — Standalone reload replications, SPA,
polish,"** with one scope clarification made explicit here: the original Build Order described the
Backfill HTTP trigger endpoint's *behavior* (in the "what changes" section) but never assigned it to a
lettered phase. It's placed here because the SPA's Backfill trigger form (this phase) has nothing to
call without it, and because it's the natural place `BatchReloadSegment`/the new writers (Phase 9) and
the queue/worker plumbing (Phase 8) actually come together into something triggerable end-to-end.

## What this phase will build

**`BackfillRequest` DTO** (engine-neutral — replication, mapping, segment descriptor, capability-
filtered Reader/Cache/Writer Kind selections; no MSSQL-specific terminology). Travels
SPA → controller → `ProcessSupervisor` → `WorkQueueStore.Enqueue`, **never** through
`ConfigRepository` — a backfill trigger must not produce a git-auto-commit, since it's a run, not a
config change.

**New endpoint**, separate from the existing bodyless `POST /api/replications/{name}/runs` (which
stays exactly as-is for Primary/Periodic/manual full-replication triggers — different request shape,
different semantics):

```
POST /api/replications/{name}/mappings/{mappingName}/backfill
Body: { readerKind?, writerKind?, cacheKind?, segments: BatchReloadSegment[] }
-> 202 Accepted { runIds: [...] }
-> 404   (replication or mapping not found)
```

Handler: validate the mapping exists; for each segment in the request, if it's an `Auto` segment,
expand it via `ISegmentExpandingReader.ExpandAutoSegmentsAsync` (one `MIN`/`MAX` round-trip — the API
process already legitimately opens driver connections for metadata browsing via `MetadataService`, so
this reuses an established precedent rather than introducing a new capability) into concrete
`RangeSegment`s; call `WorkQueueStore.Enqueue(replicationName, RunKind.Backfill, mappingName,
segmentLabel, segmentJson)` once per resulting segment; call `ProcessSupervisor.EnsureWorkerRunning`;
return every minted `RunId`. No process spawn is ever on this request's critical path — enqueueing is
a few SQLite writes, matching the "hundreds queued at once" scale the whole design was built for.

**`"segment"` options-channel injection**: the worker's `ProcessWorkItemAsync` (Phase 8, currently
ignores `WorkItem.SegmentJson`) must, when a claimed item carries one, inject it as a JSON string under
the well-known `"segment"` key into per-iteration copies of **all three** roles' options (Reader,
Cache, and Writer all need it — the writer needs it to build its scope predicate/CTE). This is the one
piece of "wiring the queue to the segment types" that has to land in `DataSync.TaskRunner`, not just
the API — everything else this phase touches is API/SPA.

**Standalone reload replications**: a replication whose `ChangeProcessing.Reader.Kind` is
`MsSqlBatchReload` and whose `Reader.Options["segments"]` holds a persisted JSON array of
`BatchReloadSegment` (parsed once per Primary pass for that mapping, iterated the same way a Backfill's
segments are). `MsSqlBatchReload` added to the capability-driven Kind pickers (see below) —
`SchedulingEvaluator`/`SchedulerService` need no changes; a `MsSqlBatchReload`-kind Primary pass is
just a Primary pass with a different reader, already handled uniformly by Phase 8's per-mapping model.

**SPA — capability-driven Kind pickers, replacing the hardcoded `driverKinds.ts`**: every Kind
`<select>` (in `OverviewPanel.tsx` and the new Backfill form below) queries Phase 9's
`GET /api/connections/{name}/capabilities` endpoint live. `driverKinds.ts`'s static arrays are retired
entirely — this is the concrete fix for the design review's "nothing should be scoped as unnecessary
because v1 is MSSQL-only" correction, which specifically called out hardcoded Kind defaults in a
backfill trigger form as the wrong pattern.

**SPA — Options textarea** (`OverviewPanel.tsx`): a small raw-JSON textarea per Reader/Writer/Cache
block for editing `Options` (parse-on-save, visible error state for invalid JSON). This is the
authoring surface for a standalone reload replication's static segment list, and incidentally closes a
pre-existing, unrelated gap: the Watermark reader's required `watermarkColumn` option has never had any
UI, since `Options` editing has never existed anywhere in the SPA before this.

**SPA — Backfill trigger form** (`RunsPanel.tsx`): a "Backfill…" button next to "Run Now," opening a
small form — mapping picker (this replication's table-mapping names), a segment-mode chooser (`Full` /
`List` + column + comma-separated values / `Range` + column + min/max / `Auto` + column + bucket
count) for a **single** segment per submission (not a batch of segments in this first SPA pass), and
Reader/Writer/Cache Kind pickers sourced from the capabilities endpoint (not hardcoded — see above).

**SPA — run history**: `TaskRunRecord`'s `runKind`/`mappingName`/`segmentLabel` fields (already in the
type since Phase 8) get a visible `RunKind` badge and segment label in the history table, so Primary
and Backfill rows are distinguishable, and a `Queued` status renders sensibly (it's a real status now,
not just `Pending`/`Running`/terminal).

## What this phase does not build

A multi-segment queue-of-runs UI (one segment per Backfill submission only); a structured
(non-JSON-textarea) Options editor; auto-computed-bucket UI beyond the bucket-count input already
covered above; run-history filtering beyond the simple badge. All explicitly deferred, consistent with
the design review's "smallest reasonable v1 UI" guidance.

## How to verify when built

- `Category=Integration` test: trigger a `Primary` run and a `Backfill` run concurrently against the
  same replication (different mappings) and assert both succeed — the direct proof of the coexistence
  requirement the whole Phase 8 foundation was built for.
- `Category=Integration` test: two concurrent `Backfill` triggers for the *same* mapping's segments
  correctly serialize (not double-processed), matching Phase 8's `WorkQueueStore`/`RunLockStore`
  guarantees, now exercised through the real HTTP endpoint instead of directly against the stores.
- `Category=Integration` test: an `Auto` segment Backfill request against a real table expands into the
  expected number of `RangeSegment` `WorkQueue` rows and all of them complete correctly.
- SPA: manual click-through creating a standalone `MsSqlBatchReload` replication (JSON-authored segment
  list) and running it end-to-end; a separate manual click-through triggering an ad-hoc backfill against
  a `MsSqlChangeTracking` replication while it's enabled/scheduled, confirming via direct SQL that the
  incremental replication's watermark and schedule are undisturbed after the backfill completes (the
  core guarantee `RunKind.Primary`-only `SetWatermark` gating exists for).
- `dotnet build` clean; `npx tsc --noEmit` clean; full existing .NET suite plus new tests green; the
  Playwright golden path suite still green (extend it, or add a sibling spec, covering the Backfill
  trigger flow through the real browser).

## Open questions to resolve during implementation

- Exact `BackfillRequest` JSON shape for the `segments` array — one segment per request per the SPA
  scope above, but the DTO itself should probably accept an array from day one (matching
  `ReaderConfig.Options["segments"]`'s array shape) so the API doesn't need a breaking change once
  multi-segment submission is built later.
- Whether `EnsureWorkerRunning`'s existing exit-race grace period (Phase 8, 5 empty polls) is
  sufficient once Backfill triggers add a second, independent source of "new work might arrive any
  moment" beyond `SchedulerService`'s own ticks — worth a dedicated concurrency test rather than
  assuming Phase 8's fix generalizes without re-checking.
