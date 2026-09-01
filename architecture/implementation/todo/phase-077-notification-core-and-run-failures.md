# Phase 77 — Notifications: core pipeline, and the run-failure trigger

**Status**: Not started.
**Plan reference**: `architecture/planning/done/notifications.md` — first of four slices; email delivery
(phase 78, per that doc's provisional numbering — re-check for collisions at write time), the
pause/watermark-expiry producers, and the latency trigger are separate, not-yet-written follow-ons.

## The gap

No notification/alerting mechanism exists. The only "push" today is SignalR events to a client already
watching a run's page live (`RunMonitorService.cs:60-83`) — nobody not looking is ever told anything.
Real multi-user auth already exists (`CurrentUser.Id`, `UserStore` with `Email` already on every user
record), so this phase can key directly to a real user identity rather than inventing one.

## What to build

### Schema

- **`Notifications`** — global, append-only, one row per notification, monotonically increasing `Id`
  (mirror `Logs`' shape — `LogWriter.cs:112-124` is the idiom: `WHERE Id > $sinceId ORDER BY Id`).
  Columns at minimum: `Id`, `CreatedAtUtc`, a kind/category (this phase only ever writes one kind — run
  failure — but the column should already distinguish kinds, since phases 79/80 add more without a
  schema change), and enough context to render a useful message (`TaskName`, `RunId` at minimum for a
  run-failure row).
- **`NotificationReadState(UserId, LastSeenNotificationId)`** — one row per user, the per-user cursor.

### API

- `GET /api/notifications?sinceId=N` — everything newer than the cursor, or everything if `sinceId` is
  omitted (initial load).
- A way to advance the cursor (mark-seen) — `POST`/`PUT` against `NotificationReadState`, keyed to
  `CurrentUser.Id`.
- **Auth-disabled mode**: when `CurrentUser.Id` is null (`DataSync:Auth:Disabled=true`), no cursor is
  persisted — every notification shows as unread, always. State this explicitly in the API rather than
  letting a null user id silently no-op the cursor write.

### Cleanup

Fold into `RunPruningService.PruneAsync` (`RunPruningService.cs:22-70`) — it already prunes `TaskRuns`/
`Logs` and `ChangeCheckHistory` (phase 75) in one sweep; add `Notifications` as a third. Check the
growth rate first (one row per notable event, not per tick — likely fine sharing `RunRetentionDays`
unlike phase 75's `ChangeCheckHistory`, which needed its own knob because it wrote every 5 seconds; this
table shouldn't have that problem, but confirm rather than assume).

### SPA

Minimal: a bell/badge with an unread count is enough to prove the pipeline end to end. A full
notification-center page is not required by this phase — a reasonable next increment once the pipeline
is proven, not a blocker to shipping this one.

### The run-failure producer

Wherever `TaskRuns.Status` is finalized to `Failed` (`TaskRunStore.CompleteRun` or equivalent), insert a
`Notifications` row. This is the one producer this phase builds, chosen because it's the highest-value,
lowest-noise trigger and proves schema → API → cursor → cleanup → a real write end to end before phases
79/80 add the other three.

## What this phase should not do

- Email delivery — phase 78. This phase is in-app only.
- The pause, watermark-expiry, or latency producers — phases 79/80.
- A full notification-center UI beyond the bell/badge.
- Per-notification-kind user preferences (e.g. "mute run failures for replication X") — not asked for,
  don't build ahead of a request for it.

## How to verify

- A test asserting a failed run produces exactly one `Notifications` row, with enough context to
  identify which replication/run failed.
- A test asserting `GET .../notifications?sinceId=N` returns only rows newer than `N`, and omitting
  `sinceId` returns everything.
- A test asserting two users' cursors are independent — one marking seen doesn't affect the other's
  unread count.
- A test asserting the auth-disabled path never 500s and never silently drops the notification (it just
  doesn't personalize read state).
- A test confirming `RunPruningService` purges aged `Notifications` rows on its existing tick.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
