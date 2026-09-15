# Phase 131 — Pause history, as Monitoring's third sub-tab

**Status**: Complete
**Plan reference**: `architecture/planning/todo/pause-history-ui.md`, both its original scope (phase
64's `PauseEvents`, logged but never surfaced) and its 2026-09-04 widening (one history over both the
replication-level pause and the table-mapping-level `ReadHold.Paused`, since `reset-a-mappings-
watermark-from-the-ui.md` shipped the second grain). Product direction for where it lives, confirmed
2026-09-13: a third Monitoring sub-tab, beside Current Status and Run History.

## Why

An operator could already see *that* a replication or a table was paused (`MonitoringPanel`'s hold
badges). Nothing showed *who* paused it, *when*, or *why* — `PauseEvents` had recorded exactly that
since phase 64, queryable via `TaskRunStore.GetPauseHistory`, and its own doc comment said plainly:
*"Nothing in the product calls this yet — the viewer is its own follow-up."* This phase is that
follow-up, widened to cover the table-mapping grain the way the planning doc's own addendum asked for.

## What was built

Every fact this phase's design relied on ("most of the backend already exists") was verified against
the actual files before writing code, and held up — nothing in the plan needed correcting mid-flight.

### 1. `PauseEvents` gains a nullable `MappingName` column

New migration template appended to `Migrations.cs` (after phase 124's `DeleteGuardJson` one):

```sql
ALTER TABLE PauseEvents {{addcolumn}} MappingName {{key}} NULL;
CREATE INDEX IX_PauseEvents_TaskName_MappingName ON PauseEvents(TaskName, MappingName);
```

`PauseEventRecord` (`Models.cs`) gained `string? MappingName`, inserted right after `TaskName` in the
record's parameter list (`Id, TaskName, MappingName, Action, Note, PerformedAtUtc, PerformedBy`) —
matching the column order the new endpoint's own doc comment names. `TaskRunStore.GetPauseHistory`'s
`SELECT` gained the column; its `WHERE TaskName = $name` needed no change at all, so it now returns
both grains for free, exactly as the plan predicted. Confirmed directly: `SetPaused`'s existing
`INSERT` does not name `MappingName` in its column list, and every existing row (and every future
replication-level row) gets `NULL` on all three engines without touching that statement.

### 2. `TaskRunStore.SetMappingHold`

New method, written exactly as planned: writes only a `PauseEvents` history row (the current-state half
already lives in `ChangeWatermarks` via `SetReadIntentAndHold`/`SetReadHold`), and raises no
notification — a deliberate asymmetry against `SetPaused`, not an oversight, documented on the method
itself and in "What this does not build" below.

### 3. `TableMappingsController.SetReadState` writes history only on a real boundary crossing

Added a read-before-write (`watermarks.GetReadState(...)?.Hold ?? ReadHold.None`) ahead of
`SetReadIntentAndHold`, then:

```csharp
if (request.Hold != previousHold && (request.Hold == ReadHold.Paused || previousHold == ReadHold.Paused))
    taskRunStore.SetMappingHold(replicationName, mappingName, request.Hold == ReadHold.Paused, request.Note, currentUser.Author.Name);
```

`TaskRunStore taskRunStore` was added to `TableMappingsController`'s constructor — confirmed not already
injected, as the plan said.

### 4. `SetMappingReadStateRequest` gains an optional `Note`

`public sealed record SetMappingReadStateRequest(ReadIntent Intent, ReadHold Hold, string? Note = null);`
— optional and defaulted, so `IntentHoldCell`'s quick pause/resume toggle (`togglePause`) keeps working
completely unchanged, sending no note at all.

`MappingReadStateDialog` gained a note field, but *where* it goes needed a real decision the phase doc
left implicit — see "Decisions made" below, since the dialog as written had no existing path that ever
set `Hold` to `Paused` in the first place.

### 5. `GET /api/replications/{name}/pause-history`

Added to `ReplicationsController`, exactly as specified — `[Authorize(Policies.Viewer)]`, `limit`
defaulting to 50, returning `taskRunStore.GetPauseHistory(name, limit)` directly. Deliberately **not**
gated on `ListReplications().Contains(name)` the way `Status`/`SetPaused` are — matching `History`
(the git-commit-log endpoint immediately above it in the same file), which has the identical shape and
the identical lack of that gate. An unknown replication name returns an empty list, not a 404 — the
existing convention for a list endpoint in this controller, not a new one invented for this phase.

### 6. SPA: Monitoring's third sub-tab

- `MONITORING_TABS` in `MonitoringPanel.tsx` gained the `pause-history` entry.
- New `MonitoringPauseHistoryTab()` export, reading `replicationName` off `MonitoringOutletContext`
  the same way its two siblings do.
- New `src/pages/replication-detail/PauseHistoryPanel.tsx` — a flat `.card.flush` table (`When / Scope
  / Action / Note / By`), `ErrorBanner` plus `useReplicationPauseHistory`, no live polling, no
  countdown. Modeled directly on `HistoryPanel.tsx` (the other low-traffic history screen under a
  replication) rather than `RunsPanel.tsx`.
- `App.tsx`: `<Route path="pause-history" element={<MonitoringPauseHistoryTab />} />` inside the
  existing `monitoring` route, alongside `history`.

### 7. `api/types.ts` / `api/client.ts` / `api/hooks.ts`

- `PauseEvent` type (camelCase, mirroring `PauseEventRecord`).
- `client.ts`: `replications.pauseHistory(name, limit?)`.
- `hooks.ts`: `useReplicationPauseHistory(replicationName)` — plain `useQuery`, no `refetchInterval`.
- `SetMappingReadStateRequest`'s TypeScript counterpart gained `note?: string | null`.
- Beyond the plan's own list: `useSetReplicationPaused` and `useSetMappingReadState` both now also
  invalidate `keys.replicationPauseHistory(...)` on success, so a Pause History tab left open in another
  part of the app does not keep showing a list that is missing an action just taken elsewhere.

## Decisions made, and one real design gap the plan left open

- **Where the mapping-level note field actually lives.** The plan said `MappingReadStateDialog` "gains
  a note field wired the same way the replication-level pause dialog's own note field already works,"
  but reading the dialog's actual code turned up a real gap: its main (non-recovering) view only ever
  changes `Intent`, always passing `hold: currentHold` straight through unchanged — there was, and is,
  no path inside this dialog that sets `Hold` to `Paused`. The only existing way to pause a mapping at
  all is `IntentHoldCell`'s quick toggle button beside "Manage…", which the plan explicitly says must
  keep working with no note. So "wire a note field into the dialog" by itself would have added a
  textarea with nothing that ever read it. Resolved by giving the dialog's main view its own
  Pause/Resume button (`data-testid="read-state-pause-toggle"`) beside the intent picker, using the
  *current* stored intent (not the picker's unsaved draft) so pausing never silently also changes what
  the next pass does — mirroring `togglePause`'s own use of `readState.data?.intent`. This gives the
  table-mapping grain the same "ask for a note on every pause and every resume" affordance the
  replication-level `PauseDialog` has always had, without disturbing the quick-toggle's no-note path or
  the intent picker's own `onConfirm` call.
- **`PauseEventRecord`'s field order**: `MappingName` placed immediately after `TaskName`, matching the
  order the phase doc's own §5 names for the wire shape, so the C# record and the documented DTO agree
  without a reader having to cross-reference field names against position.
- Everything else matched the plan directly: no other backend or SPA judgement calls were required.

## What this does not build

- **A third pause grain.** Replication and table-mapping remain the only two `ReadHold`/`Tasks.Paused`
  mechanisms; nothing else holds anything.
- **Notifications for a table-mapping pause.** `SetMappingHold` raises nothing — confirmed by reading
  the method, not merely asserted. Decided, not left open:
  `architecture/planning/done/follow-up-phase-131-mapping-level-pause-does-not-notify.md`.
- **Retention/pruning for `PauseEvents`.** Still untouched by `RunPruningService`; still no cap. Pause
  events remain rare, operator-initiated rows, unlike the per-pass volume `TaskRuns` pruning exists for.
- **Live polling for `PauseHistoryPanel`.** A plain `useQuery` with no `refetchInterval`, invalidated
  only by the two mutations named above — a deliberate choice, not an oversight, matching `HistoryPanel`
  rather than the live Monitoring tabs beside it.
- **Filtering or paging beyond a flat `limit`-bounded list.** No keyset cursor, no kind/status filters —
  this is a low-volume audit list, and `RunsPanel`'s machinery would be the wrong amount of engineering
  for what it typically shows.
- **Playwright coverage.** Deliberately not added, per the plan's own call — this is a low-traffic audit
  view, and phase 125 already set the precedent of flagging such a gap explicitly in its own doc rather
  than silently skipping it or padding the suite with a test that exercises little.

## How it was verified

- **`PauseStateTests.cs`** (11 tests total, 4 new): `MappingHold_RoundTrips_WithMappingNameSet` (a
  mapping-level pause then resume, both rows carrying `MappingName`); `ReplicationLevelPause_
  StillRoundTrips_WithMappingNameNull` (phase 64's own grain, unaffected by the widened schema);
  `BothGrains_ReturnInterleavedById_NotGroupedByGrain` (four calls across both grains, asserting strict
  `Id` descent rather than any grouping); the five pre-existing tests all still pass unchanged.
- **`TableMappingsControllerTests.cs`** (4 new, under "Read-state pause history"): pausing a mapping
  writes exactly one `Paused` row with its note; resuming it writes exactly one `Resumed` row;
  changing only `Intent` with `Hold` unchanged writes nothing; and a genuine `PositionExpired → None`
  recovery — driven through the same `read-state` endpoint into `PositionExpired` first, then back to
  `None` — writes nothing either, since neither transition ever touches `Paused`.
- **`ReplicationsControllerTests.cs`** (3 new, under "Pause history"): both grains come back from one
  call to the new endpoint; `limit` is honoured (5 actions in, `limit=2` returns the 2 most recent);
  an unknown replication name returns 200 with an empty list, matching `History`'s own convention
  rather than 404ing.
- Fixing these tests surfaced one real gap in the initial test-writing, not in the shipped code: the
  first draft of the new API tests used mapping sources naming a `"src"` connection that was never
  actually saved, so `SetReadState`'s watermark-key resolution (`configRepository.LoadConnection`)
  threw and every one of the five new pause-history-adjacent tests failed with a 500. Fixed by adding
  an `EnsureSourceConnection()` helper (mirroring `MappingReadStateTests.SetUpAsync`'s own documented
  workaround for this sandbox's Negotiate-auth limitation) to each affected test, saving a real
  connection row directly through `ConfigRepository` before exercising `read-state`.
- Full suite run, not just the new tests: `DbDataSync.State.Tests` (229 passed, 0 failed),
  `DbDataSync.Api.Tests` (488 passed, 23 skipped — pre-existing Windows-only certificate tests, 0
  failed), `DbDataSync.TaskRunner.Tests` (77 passed, 0 failed) — no regressions in any assembly that
  touches `TaskRunStore`, `PauseEventRecord`, or the two controllers.
- SPA: `npm run build` (`tsc -b && vite build`) clean; `npm run lint` (`oxlint`) exits 0 — the only
  warnings present are pre-existing ones in files this phase never touched (`ReconcileDeletesForm.tsx`,
  `useRunHub.ts`, `ReplicationProvisioningPanel.tsx`, `BackfillForm.tsx`, `ReplicationDetailPage.tsx`,
  `ConnectionEditPage.tsx`, `ScriptEditPage.tsx`).
- No Playwright coverage added — a deliberate choice stated above, not an oversight, and checked against
  the existing `monitoring-restructure.spec.ts`/`golden-path.spec.ts` specs that touch the Monitoring
  sub-tab bar: none of them assert an exact tab count, so the new third tab does not break them, but they
  were not re-run in this sandbox (no Playwright execution was performed as part of this phase, matching
  the plan's explicit scope).

## Open questions

None outstanding, matching the plan's own assessment — the one place the plan was less explicit than it
could have been (exactly where the mapping-level note field's affordance lives, and what it does when
activated) is resolved above rather than left open, and nothing else came up during implementation that
the plan had not already anticipated.

## References

- `architecture/planning/todo/pause-history-ui.md` — the original scope and its 2026-09-04 widening,
  both resolved above.
- `architecture/implementation/done/phase-064-pause-and-notes.md` (pause/resume + `PauseEvents`) and
  `architecture/planning/done/reset-a-mappings-watermark-from-the-ui.md` (`ReadHold`, the second grain)
  — what this phase built on.
- `src/DbDataSync.State/TaskRunStore.cs` (`SetPaused`, `GetPauseHistory`, and the new `SetMappingHold`).
- `src/DbDataSync.Api/Controllers/TableMappingsController.cs` (`SetReadState`),
  `ReplicationsController.cs` (`SetPaused`, and the new `PauseHistory` endpoint).
- `src/DbDataSync.Web/src/pages/replication-detail/MonitoringPanel.tsx`,
  `PauseHistoryPanel.tsx` (new), `App.tsx` — the sub-tab convention this phase's SPA work follows.
- `src/DbDataSync.Web/src/components/MappingReadStateDialog.tsx` — the dialog's new Pause/Resume
  affordance, and the gap in the original plan it resolves.
