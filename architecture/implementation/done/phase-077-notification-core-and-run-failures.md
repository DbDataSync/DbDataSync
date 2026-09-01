# Phase 77 — Notifications: core pipeline, and the run-failure trigger

**Status**: Done — shipped in `6f4bbbe..45a067d`. See Outcome.
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

---

## Outcome

**Shipped** in `6f4bbbe..45a067d`, in two commits: the feed, its cursor, the API, the sweep and the
producer; then the bell.

### What was built

`Notifications` and `NotificationReadState` (`Migrations.cs`, one migration), `NotificationStore`
(`src/DataSync.State/NotificationStore.cs`), `NotificationsController`
(`GET /api/notifications?sinceId=`, `POST /api/notifications/seen`), a third delete in
`RunPruningService.PruneAsync`, and `NotificationBell` in the SPA's shell.

The feed is `Logs`' shape, as the doc asked: `Id`-ordered, append-only, `WHERE Id > $sinceId`. The
cursor is one row per user holding one number, so an unread count is a comparison rather than a
per-notification read flag.

### Deviations from the doc

**The run-failure producer is inside `TaskRunStore.CompleteRun`, not wired at a call site.** The doc
said "wherever `TaskRuns.Status` is finalized to `Failed` (`TaskRunStore.CompleteRun` or equivalent)",
which reads either way; the store won. There are six paths that complete a run — a remote worker
through the state channel, a locally-hosted runner, `ProcessSupervisor` cancelling, reaping an orphan
and reaping a stopped process, and `JournalRecovery` replaying a completion a worker could not deliver
— and every one of them ends in this method. Wiring the producer at the call sites would have been
five chances to miss one and a sixth waiting in whatever phase adds the next completion path. It also
puts the notification inside the same transaction as the failure it announces, which is the atomicity
phase 80's own pause section asks for by name.

`CompleteRun` gained a transaction and a read it did not have. It was a bare `UPDATE`; it now reads
`TaskName`, `MappingName` and the current `Status` first, updates, and inserts the notification, all
in one transaction. The read is not only for context — see below.

**Notifications share `RunRetentionDays`.** The doc asked for the growth rate to be checked rather
than assumed, given phase 75 needed its own knob. Checked, and sharing is right: `ChangeCheckHistory`
writes one row per scheduler tick per source group, 17,280 a day whether or not anything happens,
which is what broke the 90-day window. This table writes one row per notable event, and every kind of
event it announces is bounded by something `RunRetentionDays` already governs — a run failure cannot
outnumber runs, and a pause is an operator action. A feed that outlived the run history explaining it
would be a feed full of sentences about rows nobody can look up. No new knob.

### Judgment calls

- **The producer fires on the transition into `Failed`, not on the write.** This is why the read
  happens before the update. A completion can legitimately arrive twice — journal recovery replaying
  an entry whose original write did land, or the supervisor reaping a process whose own failure report
  was already in flight — and a second announcement of one failure is noise a reader cannot tell from
  a second failure. A run already `Failed` produces nothing. This was cheaper and stronger than the
  `Logs`-style nullable `DedupeKey` with a partial unique index that was the first sketch: the state
  transition *is* the idempotency key, and it needs no column, no index and no caller to remember it.
- **Columns: `Kind`, `CreatedAtUtc`, `TaskName`, `MappingName`, `RunId`, `Message`.** `Kind` is a
  category, not a message, and is text rather than an enum ordinal so a row written by a newer build
  reads back as itself; this phase writes exactly one value into it and phase 80 adds two without a
  migration. The three context columns are nullable because not every kind has all three — a paused
  replication has no run and no mapping.
- **`Message` is stored, not recomposed at read time.** The context that explains a failure lives in
  rows pruned on their own schedule, so a feed that rebuilt its sentences by joining back to `TaskRuns`
  would say progressively less about older events precisely as they became harder to remember. A
  notification is a record of what was said, at the moment it was said. The context columns sit beside
  it so a later notification centre can link to the subject without parsing the sentence.
- **`MarkSeen` is monotonic, enforced in the store.** Two tabs polling one feed report different
  high-water marks, and the staler one arriving second must not resurrect what the newer already
  cleared. In the store rather than the caller because every caller would otherwise have to get it
  right.
- **`sinceId` and the cursor are deliberately different questions.** `sinceId` is "what have you not
  sent me yet", which a polling client advances every few seconds; the cursor is "what has this person
  acknowledged", which only a deliberate mark-seen moves. Deriving the unread count from `sinceId`
  would clear the badge for anyone who merely left a tab open.
- **Pruning leaves cursors alone.** A cursor left naming an Id that no longer exists is exactly right:
  it means "nothing unread", which is what somebody who read everything should still see afterwards.
- **The bell opens a short list, and opening it marks everything seen.** The doc allowed a bare badge.
  A badge with no way to see what it counts is a dead end, and the list is a dozen lines; ten entries,
  no filtering, no paging, no per-kind preferences. Opening as the dismissal, rather than a separate
  control, because a badge that survives being read is one people learn to ignore.
- **The endpoint is viewer-readable.** The feed says a replication failed or was paused, which the run
  history and the pause history already tell a viewer. Marking your own notifications read is not an
  administrative act.

### The auth-disabled mode, stated rather than degraded

With `DataSync:Auth:Disabled=true` there is no `CurrentUser.Id`, so no cursor is persisted and every
notification reads as unread, permanently, for everyone. Three places say so rather than leaving it to
be discovered:

- `NotificationFeed.Personalized` is `false` in the payload.
- `POST /api/notifications/seen` returns **200** with that flag, not a 500 and not a 401. A null user
  id is a configuration this deployment chose. A silent success would have been worse than an error —
  the client would read it as "dismissed" and the count would come straight back.
- The bell does not send the request at all in that mode, and the dropdown says why the count cannot
  be cleared.

### How it was verified

- `NotificationStoreTests` (9, `Category!=Integration`) — one row for a failed run naming replication,
  mapping and run; nothing for a succeeded one; **one** row for a completion arriving twice;
  `sinceId` filtering including the empty tail; two users' cursors independent; a cursor that refuses
  to move backwards; both prune windows; and cursors surviving a prune that deletes what they point at.
- `NotificationsEndpointTests` (6) — split by factory, because the interesting half cannot be asserted
  against one of them. `WithAuthentication` (`AuthenticatedApiFactory`): two signed-in people, one
  feed, Alice's dismissal leaving Bob's badge alone; a viewer able to read and to mark seen.
  `WithoutAuthentication` (`TestApiFactory`, which sets `Auth:Disabled=true`): the feed served and
  flagged unpersonalized with the notification still present, mark-seen returning 200 and not
  clearing, and `sinceId` filtering over HTTP.
- `Pruning.TheExistingSweep_AlsoAgesOutNotifications` — the claim that justifies this table having no
  background service of its own, and the one that would quietly stop being true if the third delete
  were dropped.
- `StateDatabaseTests.Constructing_CreatesSchema` gained the two table names — the existing guard that
  makes a new table a deliberate act.
- Cross-engine coverage came free: `CrossEngineStateTests` runs the whole migration set against
  SQLite, Postgres and SQL Server and passed first time on the new tables, which is what confirms
  `{{identity:Id}}`/`{{key}}`/`{{int}}` were spelled right for all three.
- Full suite green: `Category!=Integration` **927 passed**, `Category=Integration` **201 passed**, 0
  failures in either. `tsc -b` clean and the SPA build clean, for the bell.

### Notes

`NotificationStore.Insert` is `internal static`, taking somebody else's connection and transaction, and
is the only way a row gets in. Deliberately not a public "notify about anything": every producer this
project has is a store already writing the underlying event, and a public entry point would invite a
second, unatomic write from a caller with nothing to be atomic with. Phase 80's two producers go
through the same door.

Nothing reads `Kind` yet. The bell renders `Message` and ignores it, on purpose — a UI that switched on
kind would have to be taught each new one, and the column exists for a later notification centre that
wants to filter or icon-code, not for this phase.

The three-table sweep is now the third claim resting on `RunPruningService.PruneAsync` being public.
That is still the right shape, but it is worth noticing that the method's signature grows a parameter
every time a table joins it; a fourth would be the moment to pass a retention policy object instead.
