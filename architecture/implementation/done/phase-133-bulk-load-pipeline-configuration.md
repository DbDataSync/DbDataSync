# Phase 133 — the Bulk Load pipeline as configuration

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md`
(resolved 2026-09-04), "Phase A". First of two; phase 134 is "initial load becomes a bulk load".

**Why this was never written until now.** The planning doc was resolved and moved to `planning/done/`,
but its phases were described as A, B and C rather than numbered — deliberately, to avoid claiming a
number before the file existed. The consequence was that nothing pointed at them: no `todo/` file, no
build-order row, and the only reference anywhere is one line in `implementation/README.md` citing the
doc as the thing that retargeted phase 101's §1 mid-implementation. Ten days of other work went past.
That is a real gap in the convention and is worth noticing: a resolved planning doc whose phases are
never written is invisible.

## What this phase will build

A second pipeline beside Change Processing, and the vocabulary to go with it. **Nothing about initial
load changes here** — that is phase 134. This phase is inert in the sense phase 100 was: reviewable on
its own, and it leaves the system behaving as it does today.

### 1. `BulkLoadConfig`

At replication level with per-mapping override, resolved through the `PipelineResolution` shape that
`ChangeProcessingConfig` already uses: a reader (defaulting to `BatchReload`), a cache, and a writer.

**Segmenting is deliberately not part of it.** It stays on the mapping as `DefaultSegmenting`, where
phase 58 put it and gave it a real editor. How a table divides is a fact about the table, not about a
pipeline — see the planning doc's decision 4. This is also why no migration of that field is needed.

**The writer is warned about, not constrained.** Reconciliation is what makes a *drifted* target
converge, but a first load into a table that was just created has nothing to remove and an upsert-only
writer is correct and cheaper there. `WriterCapability.SupportsReconciliation` is already declared, so
the picker surfaces it exactly as the Backfill form does today.

### 2. What replaces the transient overrides

`BackfillService` currently sets reader/cache/writer as **work-item Kind overrides** that exist only
for the duration of a queued item. Those become real configuration, resolved the same way everything
else is. This is mostly promoting something that already exists into something an operator can see —
today you discover what a reload will use by reading a phase doc.

`PipelineResolution`'s existing most-specific-first order (work item → mapping → replication) still
holds; the work item's transient Kind stays as the mechanism a one-off override uses, now layered over
a real configured default rather than over `ChangeProcessingConfig`.

### 3. Save-time validation

A mapping whose Bulk Load pipeline cannot be resolved — a scripted source with no `BatchReload`
equivalent, say — fails validation, naming what is missing.

**And a run-time backstop**, for the reason phase 101 already gives about intents: save-time validation
does not close this, because a reader can be swapped or a replication-level default changed afterwards.
The run-time outcome is a loud `ReadHold`, never a silent fallback. The hold itself is phase 134's;
this phase only needs the validation.

### 4. `Backfill` → `BulkLoad`, everywhere

One vocabulary for one thing, which is the design's central claim. 49 `RunKind.Backfill` usages across
44 files, plus the four places phases 107 and 108 made the word persisted.

| | today | becomes |
| --- | --- | --- |
| `RunKind.Backfill` | enum, stored as a string in `TaskRuns` + `WorkQueue` | `RunKind.BulkLoad` |
| `BackfillBatches` | table (phase 107) | `BulkLoadBatches` |
| `TaskRuns.BackfillBatchId` | indexed column (phase 107) | `BulkLoadBatchId` |
| `BackfillDegreeOfParallelism` | YAML key (phase 108) | `BulkLoadDegreeOfParallelism` |
| `RunLane.Backfill` | lane enum (phase 108) | `RunLane.BulkLoad` |
| `BackfillService`, `BackfillProgressCard`, `GET …/backfills`, `RunKindBadge`, phase 104's kind filter | | mechanical |

### No migration, and no backwards compatibility

Decided 2026-09-14: there are no serious installations yet, so this phase does **not** carry a data
migration, a schema migration, or an old-key fallback. That removes most of what made the rename
expensive, and it is worth writing down what it means mechanically rather than leaving it as a
sentiment.

- **`Migrations.Templates` is edited in place**, not appended to. It is a numbered list applied once
  each against a tracked `SchemaVersion`, so a database already past those versions will never re-run
  them. Renaming `BackfillBatches` and `BackfillBatchId` inside the existing templates is therefore
  correct *only* on a database created afterwards.
- **`RunKind` values are stored as strings.** Existing rows reading `'Backfill'` will fail
  `Enum.Parse<RunKind>` once the member is gone. No `UPDATE` is written to fix them.
- **`backfillDegreeOfParallelism` in an existing config is simply an unknown key**, ignored by the
  deserializer, and that lane silently takes its default. Nothing rejects it and nothing warns.

**The consequence, stated plainly: every existing state database and every config carrying the old key
becomes invalid, and must be recreated rather than upgraded.** That includes local dev databases, the
dev harness's, and any CI fixture that is not built fresh. It is the accepted cost, not an oversight —
but it is the thing that will surprise someone on the day, so `CONFIG.md` and the phase retrospective
should both say it.

If a real installation appears before this ships, this decision has to be revisited; it is cheap now
and expensive later, in exactly the way schema decisions usually are.

### 5. Settings UI

The Bulk Load pipeline joins Change Processing wherever pipeline settings are edited, with the same
INHERITED display the other per-mapping overrides use.

## How it will be verified

**Unit**
- `BulkLoadConfig` resolves replication → mapping independently of `ChangeProcessingConfig`, including
  a mapping that overrides one and inherits the other
- a mapping with no resolvable bulk-load reader fails validation, naming it
- a non-reconciling writer produces a warning and still saves

**State** (`DbDataSync.State.Tests`, against every engine)
- a **freshly created** database has `BulkLoadBatches`, `BulkLoadBatchId` and its index, and phase 107's
  rollup (`SegmentsSucceeded`/`Failed`/`Running` and the derived state) behaves exactly as before
- `RunLanes.LaneFor(RunKind.BulkLoad)` routes to the bulk-load lane, and `KindsFor` still pairs it with
  `Verification`

There is deliberately **no test for upgrading an existing database**, because there is deliberately no
upgrade path. Writing one would imply a guarantee this phase does not make.

**Regression** — phase 107's backfill progress card and phase 108's lane separation both still work
end to end after the rename. They are the two features most likely to break quietly, because both key
off the value being renamed.

## Decisions

- **Full rename**, chosen 2026-09-14, superseding the narrower scope the planning doc recorded on
  2026-09-04.
- **No migration and no backwards compatibility**, also 2026-09-14, on the grounds that there are no
  serious installations yet. Existing state databases and configs are recreated, not upgraded — which
  is what makes the full rename affordable, and what makes it expensive to defer.
- **Segmenting does not move.**
- **The writer is warned about, not constrained.**

## Out of scope

- **Anything about initial load.** Phase 134.
- **Moving `DefaultSegmenting`.**
- **A Bulk Load History screen.** Phase 107 built `BulkLoadBatches` and its endpoint to support one and
  deliberately shipped no UI; that is still true and still separate.

## Open questions — resolved

Both resolved during implementation; see "Implementation notes" below for the reasoning behind each.

- **Does `RunLane` stay two-valued?** Yes, renamed but not split further.
- **Whether the work-item transient Kind override is still needed** once a real configured default
  exists. Yes, kept.

## Implementation notes

- **`BulkLoadConfig` is not `required` on `ReplicationTaskConfig`**, unlike `ChangeProcessingConfig` —
  it defaults to a working pipeline so no existing replication config needs to change for this to keep
  working. `Reader` defaults to `BatchReload`; `Cache`/`Writer` default to `null` and fall through to
  `ChangeProcessingConfig`'s resolved values (`PipelineResolution.BulkLoadCache`/`BulkLoadWriter`).
- **`ThrowIfBulkLoadInvalid` validates the mapping's fully *resolved* Bulk Load pipeline
  unconditionally** — not only when a mapping overrides it — since even the replication-level default
  (a reader defaulting to `BatchReload`) can be one a given driver does not support.
- **`RunLane` stays two-valued** (`ChangeProcessing`/`BulkLoad`), renamed but not split further, despite
  `Verification` and `ReconcileDeletes` also riding the `BulkLoad` lane — this was one of the open
  questions above; resolved as "keep as-is, the name still describes what dominates the lane's usage."
- **The work-item transient Kind override mechanism (`WorkItemKinds`) is kept, not removed** — the
  other open question above — because it is still how a one-off Bulk Load trigger picks a different
  reader/cache/writer than the mapping's configured default, which the UI form still needs.
- **`BackfillBatchStore.GetRecentBackfills` was renamed to `GetRecentBulkLoads`** for full-rename
  consistency, even though the phase 134 doc (written the same day) cites the old method name — phase
  134's implementer should expect `BulkLoadBatchStore.GetRecentBulkLoads`, not `GetRecentBackfills`.
