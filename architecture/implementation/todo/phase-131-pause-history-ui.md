# Phase 131 — Pause history, as Monitoring's third sub-tab

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/todo/pause-history-ui.md`, both its original scope (phase
64's `PauseEvents`, logged but never surfaced) and its 2026-09-04 widening (one history over both the
replication-level pause and the table-mapping-level `ReadHold.Paused`, since `reset-a-mappings-
watermark-from-the-ui.md` shipped the second grain). Product direction for where it lives, confirmed
2026-09-13: a third Monitoring sub-tab, beside Current Status and Run History.

## Why

An operator can already see *that* a replication or a table is paused (`MonitoringPanel`'s hold
badges). Nothing shows *who* paused it, *when*, or *why* — `PauseEvents` has recorded exactly that
since phase 64, queryable via `TaskRunStore.GetPauseHistory`, and its own doc comment says plainly:
*"Nothing in the product calls this yet — the viewer is its own follow-up."* This phase is that
follow-up, widened to cover the table-mapping grain the way the planning doc's own addendum asks for.

## What's already there — most of the backend

Verified directly, not assumed from the planning doc's age:

- `PauseEvents` (`Migrations.cs:372`) exists, keyed by `TaskName`, one row per pause/resume, with
  `Note`/`PerformedAtUtc`/`PerformedBy`.
- `TaskRunStore.SetPaused` (the replication grain) already writes it, in the same transaction as the
  `Tasks.Paused` flag, and already raises a notification on the pause transition.
- `TaskRunStore.GetPauseHistory(taskName, limit)` already reads it, most-recent-first, tested
  (`PauseStateTests.cs`). Nothing calls it yet.
- The table-mapping grain (`ReadHold.Paused`, from `reset-a-mappings-watermark-from-the-ui.md`) is
  fully built — `TableMappingsController.SetReadState` sets it via `ChangeWatermarkStore` — but writes
  **no history at all**. A mapping-level pause today is exactly as invisible after the fact as a
  replication-level one was before phase 64.

So the actual gap is narrower than "build a pause history feature": widen the one existing table and
its one existing read method to cover the second grain, add the write phase 64 never had a second
grain to write for, and build the one screen both feed.

## What this builds

### 1. `PauseEvents` gains a nullable `MappingName` column

```sql
-- Widens phase 64's PauseEvents to the table-mapping grain (ReadHold.Paused) alongside the
-- replication grain it already covered. NULL means the replication itself — every existing row.
-- Nullable, no backfill: the same additive shape every migration since phase 72 has used here.
ALTER TABLE PauseEvents {{addcolumn}} MappingName {{key}} NULL;
CREATE INDEX IX_PauseEvents_TaskName_MappingName ON PauseEvents(TaskName, MappingName);
```

`PauseEventRecord` (`Models.cs:266`) gains `string? MappingName`. `TaskRunStore.GetPauseHistory`'s
`SELECT` gains the column; its `WHERE TaskName = $name` already has no mapping filter, so **it already
returns both grains once the column exists** — the "one table, one screen" shape the planning doc
argued for falls out of the existing query, not a new one.

**`TaskRunStore.SetPaused`'s existing `INSERT` needs no change.** It doesn't name `MappingName` in its
column list; every SQL engine this project targets leaves an unlisted nullable column at its default
(`NULL`) on insert. Confirmed by reading the statement, not assumed — this is exactly the kind of
"does this actually still work" question worth checking rather than trusting when widening a table an
existing write already targets.

### 2. `TaskRunStore.SetMappingHold` — the write phase 64 never needed a second grain for

New method, `TaskRunStore.cs`, mirroring `SetPaused`'s doc-comment framing but simpler — the
mapping-level hold's *current state* already lives in `ChangeWatermarks` (phase 100/101), so this
writes only the history row, not a second copy of current state:

```csharp
public void SetMappingHold(string taskName, string mappingName, bool held, string? note, string performedBy) =>
    database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, """
            INSERT INTO PauseEvents (TaskName, MappingName, Action, Note, PerformedAtUtc, PerformedBy)
            VALUES ($name, $mapping, $action, $note, $now, $by);
            """);
        cmd.Bind(database, "name", taskName);
        cmd.Bind(database, "mapping", mappingName);
        cmd.Bind(database, "action", held ? PauseActions.Paused : PauseActions.Resumed);
        cmd.Bind(database, "note", (object?)note ?? DBNull.Value);
        cmd.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Bind(database, "by", performedBy);
        cmd.ExecuteNonQuery();
    });
```

No `Tasks`-table-equivalent upsert (there is no second state table to keep in sync — see above), and
deliberately **no notification**. Phase 64's `NotifyIfNewlyPaused` is a considered, asymmetric design
(pauses notify, resumes don't) for the replication grain; extending notifications to the mapping grain
too is a real product decision this phase does not make — see "What this does not build."

### 3. `TableMappingsController.SetReadState` calls it, only on a real pause/resume transition

`SetReadState` (`TableMappingsController.cs:128`) currently calls `watermarks.SetReadIntentAndHold`
directly with no read-before-write. Add one — mirroring `SetPaused`'s own *"Read before the write, so
[this] can tell a hold being newly applied from one that already was"* — then write history only when
the hold actually crosses the `Paused` boundary, not on every call (an intent-only change, or a
`PositionExpired` recovery that never touched `Paused`, writes nothing):

```csharp
var previousHold = watermarks.GetReadState(replicationName, mappingName, sourceTable)?.Hold ?? ReadHold.None;
watermarks.SetReadIntentAndHold(replicationName, mappingName, sourceTable, request.Intent, request.Hold);

if (request.Hold != previousHold && (request.Hold == ReadHold.Paused || previousHold == ReadHold.Paused))
    taskRunStore.SetMappingHold(replicationName, mappingName, request.Hold == ReadHold.Paused, request.Note, currentUser.Author.Name);
```

Requires adding `TaskRunStore taskRunStore` to `TableMappingsController`'s constructor — not injected
today, confirmed by reading its full parameter list. `CurrentUser currentUser` already is, the same
service `ReplicationsController.SetPaused` already uses for `PerformedBy`.

### 4. `SetMappingReadStateRequest` gains a note

```csharp
public sealed record SetMappingReadStateRequest(ReadIntent Intent, ReadHold Hold, string? Note = null);
```

Optional and defaulted, so the quick pause/resume toggle button in `MonitoringPanel.tsx`'s
`IntentHoldCell` (`togglePause`) keeps working unchanged with no note — matching the planning doc's own
framing that recording *why* is what makes the history worth reading, not a requirement to act at all.
`MappingReadStateDialog` (the "Manage…" dialog, which already has form state for the rest of this
request) gains a note field wired the same way the replication-level pause dialog's own note field
already works, so both grains offer the same affordance rather than one being a second-class citizen
of the other.

### 5. `GET /api/replications/{name}/pause-history`

New endpoint, `ReplicationsController.cs` (which already injects `taskRunStore` for `SetPaused`):

```csharp
[Authorize(Policies.Viewer)]
[HttpGet("{name}/pause-history")]
public ActionResult<IReadOnlyList<PauseEventRecord>> PauseHistory(string name, [FromQuery] int limit = 50) =>
    Ok(taskRunStore.GetPauseHistory(name, limit));
```

`PauseEventRecord` returned directly — a plain, already-public shape (Id, TaskName, MappingName,
Action, Note, PerformedAtUtc, PerformedBy) with nothing sensitive, the same pattern several existing
endpoints already use for their own `...Record` types.

### 6. SPA — Monitoring's third sub-tab

`MonitoringPanel.tsx`'s `MONITORING_TABS` gains a third entry:

```ts
const MONITORING_TABS: SubTab[] = [
  { path: null, label: 'Current Status', testId: 'monitoring-tab-current' },
  { path: 'history', label: 'Run History', testId: 'monitoring-tab-history' },
  { path: 'pause-history', label: 'Pause History', testId: 'monitoring-tab-pause-history' },
]
```

A new `MonitoringPauseHistoryTab()` export beside `MonitoringCurrentStatusTab`/
`MonitoringRunHistoryTab`, reading `replicationName` off the same `MonitoringOutletContext`, rendering
a new `PauseHistoryPanel` (new file, `pages/replication-detail/PauseHistoryPanel.tsx`). Wired in
`App.tsx` the same way `history` is: `<Route path="pause-history" element={<MonitoringPauseHistoryTab />} />`
inside the existing `monitoring` route.

`PauseHistoryPanel` is deliberately simpler than `RunsPanel` — this is a low-volume audit list (human
pause actions, not per-pass rows), not a live-polled operational view. A flat table, `ErrorBanner` +
`useReplicationPauseHistory(replicationName)` (new hook, plain `GET`, no live hub subscription), one
`limit` (matching the endpoint's own default of 50, no cursor paging):

| When | Scope | Action | Note | By |
| --- | --- | --- | --- | --- |
| `PerformedAtUtc`, localized | "Replication" when `mappingName` is null, else the mapping name | `Paused`/`Resumed` badge | `Note` or an em-dash | `PerformedBy` |

No live refresh countdown — a screen of past events doesn't go stale the way lag or a running pass
does; a plain manual-refresh affordance (matching `HistoryPanel`'s own posture, the other low-traffic
history screen under this replication) is enough.

### 7. `api/types.ts` / `api/client.ts` / `api/hooks.ts`

- `PauseEvent` type mirroring `PauseEventRecord` (camelCase, per this codebase's existing
  System.Text.Json-default-to-camelCase convention every other DTO already follows).
- `client.ts`: `pauseHistory: (replicationName: string, limit?: number) => ...`.
- `hooks.ts`: `useReplicationPauseHistory(replicationName: string)`.
- `SetMappingReadStateRequest`'s TypeScript counterpart gains the optional `note` field.

## What this does not build

- **A third pause grain.** Replication and table-mapping are the two `ReadHold`/`Tasks.Paused` this
  phase widens `PauseEvents` to cover — nothing else holds anything today.
- **Notifications for a table-mapping pause.** Phase 64's pause-notifies/resume-doesn't asymmetry is a
  considered decision for the replication grain; whether the mapping grain should notify at all, and on
  which transition, is a separate product question this phase does not answer. `SetMappingHold` raises
  nothing.
- **Retention/pruning for `PauseEvents`.** Checked: `RunPruningService` doesn't touch this table today,
  and this phase doesn't add a cap. Pause events are rare, operator-initiated actions, not a per-pass
  row like `TaskRuns` — the volume concern `RunRetentionDays` exists for doesn't apply the same way. A
  known, deliberate gap, not an oversight; worth a cap only if real usage ever suggests one.
- **Live polling / a hub subscription for the new panel.** See "SPA," above.
- **Filtering or paging beyond a flat, most-recent-`limit` list.** `RunsPanel`'s server-side
  kind/status filters and keyset paging exist because run history is high-volume; pause history isn't,
  and building that machinery for a screen that will typically show a handful of rows is the wrong
  amount of engineering for what this is.

## How to verify when built

- **`PauseStateTests.cs`** (extends the existing suite, no server needed): a mapping-level
  `SetMappingHold(true, ...)` then `(false, ...)` round-trips through `GetPauseHistory` with
  `MappingName` set; a replication-level `SetPaused` call still round-trips with `MappingName` null
  (the existing tests, re-run against the widened schema, prove this without a new test); a task with
  both grains' events returns them interleaved by `Id`, matching `GetPauseHistory`'s existing ordering
  contract, not grouped by grain.
- **`TableMappingsControllerTests`** (or sibling): `SetReadState` with `Hold: Paused` writes exactly one
  `PauseEvents` row for that mapping; toggling it back to `None` writes a `Resumed` row; changing only
  `Intent` with `Hold` unchanged writes nothing; a `PositionExpired` → `None` recovery (no `Paused`
  crossed) writes nothing.
- **A new `PauseHistoryEndpointTests`** (or added to `ReplicationsControllerTests`): the endpoint
  returns both grains for a replication that has both, `limit` is honoured, an unknown replication name
  behaves consistently with this controller's other endpoints (check the existing 404/empty convention
  rather than inventing one).
- SPA: `npm run build`/`lint` clean. No new Playwright coverage planned — this is a low-traffic audit
  view, not a golden-path interaction, and phase 125 already set the precedent for flagging that choice
  explicitly rather than silently skipping it.

## Open questions

None outstanding — the product direction (a Monitoring sub-tab) resolves the planning doc's own open
question, and every mechanism this phase needs either already exists (verified above) or is a small,
precedented addition to something that does.

## References

- `architecture/planning/todo/pause-history-ui.md` — the original scope and its 2026-09-04 widening,
  both resolved above.
- `architecture/implementation/done/phase-064-pause-and-notes.md` (pause/resume + `PauseEvents`) and
  `architecture/planning/done/reset-a-mappings-watermark-from-the-ui.md` (`ReadHold`, the second grain)
  — what this phase builds on.
- `src/DbDataSync.State/TaskRunStore.cs` (`SetPaused`, `GetPauseHistory`) — the methods this phase
  extends and reuses.
- `src/DbDataSync.Api/Controllers/TableMappingsController.cs` (`SetReadState`),
  `ReplicationsController.cs` (`SetPaused`, the new endpoint's home) — the two call sites.
- `src/DbDataSync.Web/src/pages/replication-detail/MonitoringPanel.tsx`, `tabs.tsx`, `App.tsx` — the
  sub-tab convention this phase's SPA work follows exactly.
