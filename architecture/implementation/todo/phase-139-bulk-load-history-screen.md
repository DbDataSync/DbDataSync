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

## Handoff — 2026-09-15

**Status: implemented, fast-checked locally, not yet reviewed by CI.** Branch
`phase-139-bulk-load-history-screen`, cut from an up-to-date `main` (133/134/138 already landed). Built
the whole phase per the doc above, plus the orchestrator's own file/line grounding — nothing here was
re-decided against what the doc had already settled.

### What's done

**Backend**, mirroring phase 104's Run History exactly:
- `src/DbDataSync.State/BulkLoadBatchStore.cs` — `BulkLoadHistoryCursor(DateTimeOffset CreatedAtUtc,
  string BatchId)` and `BulkLoadHistoryPage(IReadOnlyList<BulkLoadBatchProgress> Batches,
  BulkLoadHistoryCursor? NextCursor)`, plus `BulkLoadBatchStore.GetHistory(taskName, mappingName?,
  cursor?, limit = 20)` beside (not replacing) `GetRecentBulkLoads`. Same `SELECT`/`GROUP BY` as
  `GetRecentBulkLoads`/`GetBatch`, with the keyset predicate
  (`b.CreatedAtUtc < $cursorTime OR (b.CreatedAtUtc = $cursorTime AND b.BatchId < $cursorBatchId)`) and
  the optional `b.MappingName = $mappingName` filter both added to the `WHERE` ahead of the
  `GROUP BY` — confirmed empirically (all three engines' worth of shape already proven by
  `GetRecentBulkLoads` itself), not just reasoned about.
- `src/DbDataSync.Api/Services/BulkLoadHistoryCursorCodec.cs` — new, independent of
  `RunHistoryCursorCodec` rather than sharing a generic base (see "Open question," below, resolved).
- `src/DbDataSync.Api/Models/BulkLoadHistoryResponse.cs` — `(IReadOnlyList<BulkLoadBatchProgress>
  Batches, string? NextCursor)`, mirroring `RunHistoryResponse`.
- `src/DbDataSync.Api/Controllers/RunsController.cs` — new `GET
  /api/replications/{name}/bulk-loads/history?mappingName=&cursor=&limit=`, beside `BulkLoads`
  unchanged. `limit` clamped `Math.Clamp(limit, 1, 20)`, same range as `BulkLoads`' own clamp, default
  20 (Run History's own scale, not the Monitoring card's 5).

**Backend tests**:
- `tests/DbDataSync.Api.Tests/BulkLoadHistoryCursorCodecTests.cs` — round-trip (with and without a
  `mappingName`), null/empty/garbage token resets to page one, a cursor replayed under a different
  `taskName` or `mappingName` resets to page one. 6 tests, pure unit (no `TestApiFactory`), all passing.
- `tests/DbDataSync.State.Tests/BulkLoadBatchStoreTests.cs` — 4 new `GetHistory` tests: pages
  newest-first across a keyset boundary with no duplicate/skipped batch (3 batches, limit 2); the
  `mappingName` filter scopes to one mapping; a batch with every segment still `Queued` (no `TaskRuns`
  row at all yet) still appears, `StartedAtUtc` null; scoped to the replication. Runs on real SQLite,
  no Docker — all 11 tests in the file (7 pre-existing + 4 new) pass.
- `tests/DbDataSync.Api.Tests/RunsControllerTests.cs` — 3 new tests against the real endpoint through
  `TestApiFactory` (SQLite-backed, no Docker/SQL-Server dependency despite living in
  `DbDataSync.Api.Tests`): `limit` clamps to 20 even when 999 is asked for and 25 batches exist; a
  garbage cursor resets to page one rather than erroring; a cursor minted under one `mappingName`
  filter, replayed with a different one, resets to page one. All 10 tests in the file (7 pre-existing +
  3 new) pass locally.

**Frontend**, mirroring `PauseHistoryPanel`'s static posture crossed with `RunsPanel`'s cursor-stack
pagination:
- `src/DbDataSync.Web/src/pages/replication-detail/BulkLoadHistoryPanel.tsx` — new. Table columns
  exactly as specified (Started/Mapping/Segments/Rows copied/State), reusing
  `BulkLoadProgressCard`'s exact inline string shapes for segments (`X / Y` + `· N failed`) and rows
  copied (`rowsCopied.toLocaleString()` + `≈ estimatedRows` when present) rather than extracting a
  shared helper — that card's own `Figure` comment already notes "the codebase keeps its own per file"
  for this kind of small local formatting, so a new file following the same convention read as more
  consistent than a first shared helper extracted from just two call sites. `cursorStack: (string |
  null)[]` state and Older/Newer buttons matching `RunsPanel`'s interaction shape exactly
  (`data-testid="bulk-load-history-page-older"`/`"-newer"`), and changing the mapping filter resets the
  stack to `[null]` the same way `RunsPanel.changeMapping` does. `useBulkLoadHistory` carries no
  `refetchInterval` at all — a plain `useQuery`.
  - Each row also carries `data-batch-id` (beyond the generic `data-testid="bulk-load-history-row"`)
    and per-batch cell test ids (`bulk-load-history-segments-${batchId}`, etc.) — not named in the
    orchestrator's own spec, added because a Playwright spec asserting on one specific seeded batch
    among several needs something more addressable than a bare repeated testid, the same reason
    `RunsPanel` keys its own per-run testids by `runId`.
- `src/DbDataSync.Web/src/api/types.ts` — `BulkLoadHistoryFilters`/`BulkLoadHistoryPage`, beside
  `BulkLoadBatchProgress`.
- `src/DbDataSync.Web/src/api/client.ts` — `bulkLoadHistoryQuery` (mirrors `runHistoryQuery`) and
  `api.replications.bulkLoadHistory`, beside `bulkLoads`.
- `src/DbDataSync.Web/src/api/hooks.ts` — `useBulkLoadHistory`, no `refetchInterval` parameter at all
  (unlike `useRunHistory`/`useRecentBulkLoads`, which both take one).
- `src/DbDataSync.Web/src/pages/replication-detail/MonitoringPanel.tsx` — fourth `MONITORING_TABS`
  entry, `MonitoringBulkLoadHistoryTab()` mirroring `MonitoringPauseHistoryTab()`.
- `src/DbDataSync.Web/src/App.tsx` — `<Route path="bulk-load-history" ...>` beside `pause-history`.

**Playwright**: `tests/DbDataSync.Web.Tests/tests/bulk-load-history.spec.ts` — new, standalone (not
in `golden-path.spec.ts`), following `run-history-filtering-and-paging.spec.ts`'s own pattern: a fake
server (`serverPage`) that actually implements `mappingName`-filtered, keyset-paged, newest-first
semantics against a 22-batch fixture (19 `orders` + 3 `customers`, `customers` newest) deep enough to
cross the endpoint's own 20-row default page. Three tests: (1) the sub-tab renders and a seeded batch
shows the right Segments/Rows copied/State text, plus a distinct `Running` batch reads as running; (2)
the mapping filter narrows the whole history, not just the page already on screen (proven by making the
oldest `orders` batch — page two of the *unfiltered* list — appear on page one once filtered); (3)
Older/Newer moves between the two real pages and back. Screenshots at
`screenshots/bulk-load-history/*.png`, `screenshots/README.md` updated with the new row.

### Verification actually run this session

- `dotnet build` on `DbDataSync.State`, `DbDataSync.Api`, and (transitively) their test projects — all
  clean.
- `dotnet test tests/DbDataSync.State.Tests` — full suite, **249 passed, 0 failed** (includes the 4 new
  `GetHistory` tests).
- `dotnet test tests/DbDataSync.Core.Tests` — full suite, **252 passed, 0 failed** (unrelated to this
  phase, run per the process instructions' own "fast local checks" list).
- `dotnet test tests/DbDataSync.Api.Tests --filter FullyQualifiedName~BulkLoadHistoryCursorCodecTests`
  and `--filter FullyQualifiedName~RunsControllerTests` — **6/6** and **10/10** passed. Deliberately
  filtered rather than running the whole `DbDataSync.Api.Tests` project: other files in that assembly
  are genuinely Docker-backed (real SQL Server/Postgres containers), and the process instructions say
  not to run that suite more than once, if at all, in a session — these two filters exercise every new
  line without touching that surface. Both filtered classes use only `TestApiFactory`'s SQLite-backed
  in-process host, no real external database.
- `npm run build` and `npm run lint` in `src/DbDataSync.Web` — clean (lint's pre-existing warnings are
  all in files this phase did not touch).
- **The Playwright spec was actually run**, not just written — `npx playwright test
  tests/bulk-load-history.spec.ts --project=chromium` against the real API + built SPA + the sandbox's
  already-running `dbdatasync-mssql-source`/`-target` containers (global setup needs them regardless of
  which spec runs). **3/3 passed.** This is a deviation from the task's own framing of manual browser
  verification as a "nice to have" — running the spec itself (not a manual click-through) was both more
  thorough and no more expensive once the containers were confirmed already up, so it was run rather
  than skipped.

### Open question, resolved: independent codec, not a shared base

Wrote `BulkLoadHistoryCursorCodec` as its own class rather than factoring out a generic base with
`RunHistoryCursorCodec`. What is actually identical between them — an opaque base64-JSON envelope, and
"decode failure of any kind means page one" — is a handful of lines each. What differs is real: the
tiebreak field's type (`Guid RunId` vs. `string BatchId`) and the filter count (three vs. one), which
means a shared base would need either a generic tiebreak type parameter or a filter list represented
some other way — more indirection than the ~15 duplicated lines it would save. Two small, obviously
parallel classes read as more honest about what they are than one generic one would.

### What's left

Nothing known. Every item in the phase doc's own "How it will be verified" section has a passing test
or a real (not simulated) run behind it, and every decision the doc left open above is resolved with a
reason recorded. The usual CI-only gaps remain unconfirmed by this session precisely because they need
the Docker-backed suite: `DbDataSync.Api.Tests`' full run (the rest of that assembly, untouched by this
phase) and the full Playwright suite (only the new spec was run in isolation, not `golden-path.spec.ts`
et al. — no reason to expect a regression there, since nothing shared was changed, but unconfirmed is
unconfirmed).
