# Phase 139 — Bulk Load History, as Monitoring's fourth sub-tab

**Status**: Planned, not started.
**Plan reference**: none — found during the same 2026-09-14 audit as phase 138 (see
`architecture/implementation/README.md`'s 2026-09-14 update). Closes a gap phase 107 named explicitly
and phase 133 reaffirmed the same day this doc was written: *"A Bulk Load History screen. Phase 107
built `BulkLoadBatches` and its endpoint to support one and deliberately shipped no UI; that is still
true and still separate."*

**Depends on phase 134 landing first.** 133 has since landed (2026-09-14, same day this doc was
originally drafted against pre-rename names — corrected below to match what actually shipped) and did
the full rename this doc originally anticipated: `BackfillBatches` → `BulkLoadBatches`,
`BackfillBatchProgress` → `BulkLoadBatchProgress`, `GetRecentBackfills` → `GetRecentBulkLoads`,
`BackfillProgressCard` → `BulkLoadProgressCard`, and the endpoint to
`GET /api/replications/{name}/bulk-loads` (hyphenated — confirmed against `RunsController.BulkLoads`
directly, not guessed). 134 is still pending; do not start this phase until it is in `done/` — the
volume argument in "Why now," below, is what 134 specifically changes.

## Why

`BulkLoadBatchStore.GetRecentBulkLoads` already rolls a batch's segments up into exactly the shape a
history screen needs — `SegmentsSucceeded`/`Failed`/`Running`, `RowsCopied`,
`EstimatedRows`/`EstimateCaveat`, a derived `State` — and its own doc comment says plainly: *"The
Monitoring card reads only the first; the wider list is what a future Batch Load History screen is
for, which is why this takes a limit rather than returning one."* The `GET
.../bulk-loads?limit=` endpoint (`RunsController.BulkLoads`) already exists for exactly this. Nothing
has built the screen.

**Why now, not whenever**: phase 134 makes *every initial load* a bulk load, not just an operator-
triggered manual reload. Batch volume is about to go from "occasional, human-initiated" to "one per
mapping's first run, automatically" — the moment 134 ships, this becomes a materially more useful (and
more necessary) screen than it would have been when phase 107 first deferred it.

## What this phase will build

### 1. A fourth Monitoring sub-tab, mirroring phase 131's own precedent exactly

`MonitoringPanel.tsx`'s `SubTab` list goes from three to four:

```tsx
{ path: null, label: 'Current Status', testId: 'monitoring-tab-current' },
{ path: 'history', label: 'Run History', testId: 'monitoring-tab-history' },
{ path: 'pause-history', label: 'Pause History', testId: 'monitoring-tab-pause-history' },
{ path: 'bulk-load-history', label: 'Bulk Load History', testId: 'monitoring-tab-bulk-load-history' },
```

The same shape phase 131 used to go from two sub-tabs to three for Pause History — nothing about that
precedent needs reinventing.

### 2. Backend: real keyset pagination, mirroring phase 104's `RunHistoryCursorCodec`

**Decided 2026-09-14, over the simpler "just raise the clamp" alternative**: phase 134's volume change
means a flat `limit` (today clamped 1–20) will not age well. Build the same shape phase 104 already
proved for Run History:

- A new `BulkLoadHistoryCursor(DateTimeOffset CreatedAtUtc, string BatchId)` keyset (batches don't have
  a single monotonic id the way `TaskRuns.RunId`/`EnqueuedAtUtc` does, but `CreatedAtUtc` + `BatchId` —
  already both selected, already both in the `ORDER BY` — is an equally valid keyset pair).
- `BulkLoadBatchStore.GetHistory(taskName, mappingName, cursor, limit)` — a new method beside (not
  replacing) `GetRecentBulkLoads`, which the Monitoring card keeps using unchanged (it only ever wants
  the newest one, no cursor, no mapping filter).
- A new `BulkLoadHistoryCursorCodec`, structurally identical to `RunHistoryCursorCodec` (opaque
  base64-JSON envelope, carries the filter it was issued under — here just `mappingName` rather than
  `kind`/`status` — and a filter mismatch resets to page one rather than erroring).
- `GET /api/replications/{name}/bulk-loads/history?mappingName=&cursor=&limit=` on `RunsController`,
  beside the existing `.../bulk-loads` endpoint (`RunsController.BulkLoads`), not replacing it.

### 3. Mapping filter

**Decided 2026-09-14**: batches already carry `MappingName` (`BulkLoadBatchProgress.MappingName`,
present since phase 107). A replication with many tables would otherwise mix every table's reload
history into one list. A `mappingName` query param, same shape as Run History's phase 104 filter —
optional, a dropdown of the replication's own mapping names (already fetched for other panels; no new
endpoint needed to populate it).

### 4. `BulkLoadHistoryPanel.tsx`, mirroring `PauseHistoryPanel`'s shape, `RunsPanel`'s pagination

**Decided 2026-09-14**: static, not live-polled — `PauseHistoryPanel`'s "low-volume audit list" posture,
not `RunsPanel`'s countdown-and-live-refresh one. The existing Monitoring → Current Status card
(`BulkLoadProgressCard`, phase 107/133) stays the one live view of an in-flight load; this screen is a
"what happened" log an operator reloads manually, same as Pause History.

A flat table (`PauseHistoryPanel`'s `grid-head`/`grid-row` shape), one row per batch:

| column | source |
| --- | --- |
| Started | `StartedAtUtc` (or `CreatedAtUtc` if never started — a batch whose segments are all still `Queued`) |
| Mapping | `MappingName` |
| Segments | `SegmentsSucceeded / SegmentCount`, `· k failed` when `SegmentsFailed > 0` — same shape `BulkLoadProgressCard` already renders |
| Rows copied | `RowsCopied`, `≈ EstimatedRows` beside it when present, `EstimateCaveat` as a hint |
| State | `Running` / `Completed` / `CompletedWithFailures`, same badge coloring precedent as `BulkLoadState` gets elsewhere |

Pagination: the same keyset cursor-stack "Older"/"Newer" pair `RunsPanel.tsx` already implements
(`cursorStack: (string | null)[]`, "Older" pushes `nextCursor`, "Newer" pops) — reused as the
interaction pattern, not the component (this screen has no live-polling half to gate on page number the
way `RunsPanel`'s does).

### 5. `types.ts` / `client.ts` / `hooks.ts`

`BulkLoadHistoryPage` (`{ batches: BulkLoadBatchProgress[]; nextCursor: string | null }`, matching
`RunHistoryPage`'s own shape), `api.replications.bulkLoadHistory`, `useBulkLoadHistory` — a plain
`useQuery`, no polling interval (per "static," above).

## How it will be verified

**Backend**
- `BulkLoadHistoryCursorCodecTests`, mirroring `RunHistoryCursorCodecTests` — round-trip, filter
  mismatch resets to page one, malformed token resets to page one.
- `BulkLoadBatchStoreTests` — `GetHistory` pages correctly newest-first across a keyset boundary
  (create more batches than one page holds, confirm the second page picks up exactly where the first
  left off, no duplicate/skipped row); `mappingName` filter scopes correctly; a batch with no started
  segments yet still appears (ordered by `CreatedAtUtc`, not `StartedAtUtc`).
- `RunsController` integration test for the new endpoint — clamps `limit`, 400s or resets on an invalid
  cursor rather than throwing.

**Frontend**
- `npm run build` / `npm run lint` clean.
- Playwright: a new, small, standalone spec (not inserted into `golden-path.spec.ts`, same reasoning
  phase 125 gave for `ReconcileConfigCard` — this screen's own history is easy to seed and check in
  isolation) — the sub-tab renders, a seeded batch appears with the right columns, the mapping filter
  narrows the list, "Older"/"Newer" moves between pages.

## Decisions

- **Real keyset pagination, not a bumped flat limit** — phase 134's volume change is the reason; see
  "Why now," above. Confirmed with the user 2026-09-14.
- **Static list, no live polling** — confirmed with the user 2026-09-14; the existing progress card
  stays the one live view.
- **Mapping filter included in v1** — confirmed with the user 2026-09-14.
- **`GetRecentBulkLoads`/the existing `.../bulk-loads` endpoint stay as they are**, a new method and a
  new endpoint are added beside them rather than generalizing the existing one to take an optional
  cursor/mapping filter — the Monitoring card's own contract (newest one, no filter, no cursor) doesn't
  change shape just because a second consumer with different needs now exists.

## What this does not build

- Any change to `BulkLoadProgressCard`/the Monitoring → Current Status live view.
- Cancelling a batch, or any other batch-level action — this is a read-only history, same posture Run
  History and Pause History both already take.
- A `State` filter (Running/Completed/CompletedWithFailures) — only `mappingName` is in scope; a status
  filter can follow the same pattern later if it turns out to be wanted, the same way phase 104 added
  `status` to Run History alongside `kind`/`mappingName` rather than all three needing to ship together.

## Open questions to resolve during implementation

- **Exact keyset tiebreak when two batches share the same `CreatedAtUtc`.** `BatchId` as the secondary
  sort key almost certainly resolves it (same shape `RunId` does for Run History), but worth confirming
  against `BulkLoadBatchStore`'s actual `INSERT` — `CreatedAtUtc` is stamped in application code, not a
  database-generated monotonic value, so two batches created within the same tick are possible in a way
  Run History's UUID `RunId` tiebreak doesn't need to think about as carefully.
- **Whether `RunHistoryCursorCodec` and the new `BulkLoadHistoryCursorCodec` should share a generic
  base** rather than being two structurally-identical, independently-written classes — a real
  reuse-vs-duplication call, better made once the second implementation exists side by side with the
  first rather than guessed at from the plan.
