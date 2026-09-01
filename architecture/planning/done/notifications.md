# Notifications: a global feed, a per-user read cursor, email delivery

**Status: resolved — design ready, decomposed into phases. First phase (core + run-failure trigger)
ready for an implementation phase doc; the rest are follow-ons, listed but not yet written up.**

## What's there today, confirmed by reading the code

- **Real multi-user auth exists and is usable now.** `DataSync:Auth` config
  (`AuthOptions.cs:7-49`), `UserStore` (`UserRecord(Id, DisplayName, Email, Role, Enabled,
  CreatedAtUtc)`), `CurrentUser.Id` resolved per request from the signed-in principal
  (`CurrentUser.cs:16-59`). `Email` already exists on every user record — the SMTP delivery half doesn't
  need a new field, just a sender. One caveat: when `DataSync:Auth:Disabled=true` there is no signed-in
  identity (`CurrentUser.Id` is null); the per-user cursor needs a defined fallback for that mode (no
  personalization — everyone sees everything unread forever — rather than an error).
- **No notification/alerting mechanism exists at all.** The only "push" today is SignalR events
  (`runCompleted`/`logLine`) to a client already watching a run's page live
  (`RunMonitorService.cs:60-83`) — that's tailing an open UI, not a durable inbox. Nobody is told
  anything if they're not looking.
- **The idiom to match**: `Logs`' `Id`-ordered, `WHERE Id > $sinceId` shape (`LogWriter.cs:112-124`).
  `RunMonitorService` keeps a cursor per run, but in-memory and ephemeral — nothing today persists a
  per-consumer read position. That part of this feature is new but should look like `Logs`' shape, not
  invent a different one.
- **The cleanup idiom to match**: `RunPruningService` (`RunPruningService.cs:22-70`) — single
  `BackgroundService`, config-driven age retention, runs immediately then on a `PeriodicTimer`, already
  prunes two unrelated tables (`TaskRuns`/`Logs`, and now `ChangeCheckHistory` per phase 75) in one sweep.
  Add a third table here rather than a new service — matches its own stated single-writer rationale
  (phase 39: the API is the state store's sole writer).

## Shape, per the resolved design

**One global, append-only `Notifications` table** — every notification everyone can potentially see,
one row each, monotonically increasing `Id` (mirrors `Logs`). Not per-user rows; broadcast semantics, as
asked for ("global notification list").

**One `NotificationReadState(UserId, LastSeenNotificationId)` row per user** — the per-user cursor.
`GET /api/notifications?sinceId=N` returns everything newer; a `POST` (or similar) advances the cursor.
When auth is disabled and `CurrentUser.Id` is null: no cursor persisted, every notification always shows
as unread system-wide — stated explicitly rather than silently degrading.

**Auto-cleanup**: age-based, folded into `RunPruningService.PruneAsync`'s existing sweep, same
`DataSync:RunRetentionDays`-style knob unless growth analysis says otherwise (same judgment call phase
75 made for `ChangeCheckHistory` — check row-growth rate before assuming the shared knob fits; a
broadcast table growing at "one row per notable event" is a very different rate from "one row per tick,"
so it may well share the run-retention window fine where the tick-driven table didn't).

**Delivery**: two independent surfaces per notification row — in-app (the feed above, always) and email
(opt-in per user, sent through plain SMTP config: host/port/credentials/from-address, matching how
source/target connections are already configured rather than a third-party provider account). A user
with no email preference set gets in-app only; email is additive, not a replacement channel.

## Triggers, resolved

Four, each a genuinely different signal, each needing its own producer wired at a different point in
the code:

1. **Run failure** — a `TaskRuns` row ending `Status = Failed`. Straightforward: the same place
   `CompleteRun` already records the outcome. **This is the one the first phase builds**, to prove the
   whole pipeline (schema → API → cursor → cleanup → at least one real producer) end to end before
   building the other three on top of it.
2. **Replication paused** — `PauseEvents.Action = 'Paused'` (`Migrations.cs:372-380`, already exists,
   phase 64). A producer here is nearly free — the row already carries `TaskName`, `Note`, `PerformedBy`.
3. **Watermark/position expired** — `PositionExpiredException`, currently caught once, in
   `RunExecutor.cs:300`. Straightforward producer site; the exception already carries the mapping and
   position detail a notification would want to show.
4. **Latency** — the one genuinely underspecified request, and it turned out to already have a blocked,
   unresolved design doc: `architecture/planning/todo/run-lag.md`. Its own conclusion: "lag" is not one
   number — it's a version count for Change Tracking, bytes for a Postgres replication slot, undefined
   for batch reload, and a *real, comparable duration* only for CDC (`sys.fn_cdc_map_lsn_to_time`) and
   for Watermark mode when the watermark column happens to be a timestamp. That doc's own "next step" is
   "build phases 32 and 34, then come back" — phase 32 (CDC) is done; phase 34 (Postgres logical
   replication) is not, and is still sitting in `implementation/todo/phase-034-postgres-logical-
   replication.md`. **This phase's latency trigger is scoped to CDC only**, reusing
   `sys.fn_cdc_map_lsn_to_time` to produce a real time-based lag and comparing it against a
   config-defined threshold — not the general per-reader "lag capability" abstraction `run-lag.md`
   sketches, which stays blocked on phase 34 as that doc already says. The second half of what was
   asked — **worker check-in latency** — doesn't exist as a signal at all today: worker liveness is
   currently OS-process-only (`ProcessSupervisor`'s `HasExited` checks, no heartbeat timestamp anywhere),
   a gap `RunExecutor.cs`'s own comments already flag as unbuilt follow-on work near
   `EmptyPollsBeforeExit`. Building this trigger means building a real heartbeat first — a worker
   stamping "last seen" somewhere the API can read on a timer, most naturally piggybacked on
   `ProduceAsync`'s existing `PollInterval` loop (`RunExecutor.cs:142-181`) — genuinely new
   infrastructure, not just a new consumer of something that exists.

## Phase decomposition

Given the size, this is not one phase:

- **Phase 77 (this one, ready now)**: core schema (`Notifications`, `NotificationReadState`), API
  (`GET .../notifications?sinceId=`, mark-seen), cleanup wired into `RunPruningService`, a minimal SPA
  surface (a bell/badge with unread count is enough — a full notification-center page is not required to
  prove the pipeline), and the run-failure producer. No email yet.
- **SMTP delivery (follow-on, not yet numbered/written)**: config surface, a send path, per-user
  opt-in, wired to whatever producers exist by the time it's built.
- **Phase 80 (`implementation/todo/phase-080-notification-pause-and-watermark-expiry-triggers.md`,
  written)**: the pause and watermark-expiry producers.
- **The latency trigger (follow-on, not yet numbered/written)**: CDC-only source/target lag, and the
  new worker heartbeat mechanism for check-in latency. The larger of the follow-ons; may want its own
  split once scoped in detail.

(Phase numbers were provisional when this doc was first written and one collision already happened —
the original "phase 78" guess for email delivery collided with unrelated release-automation work that
took that slot first, and the original "phase 79" guess for the pause/watermark-expiry producers
collided with other concurrent work claiming that number before this doc's phase doc was committed.
That producers phase was written and committed as phase 80 instead. Going forward: a phase number isn't
reserved until its doc is committed — don't treat an uncommitted assignment as claimed.)

## What this phase should not do

- Build the general `run-lag.md` reader-capability abstraction — stays blocked on phase 34, unaffected
  by this feature choosing a narrower, CDC-only path for its own latency trigger.
- Build email delivery — phase 78.
- Build the pause/watermark-expiry/latency producers — phases 79/80.

**Next step**: write `implementation/todo/phase-0NN-notification-core-and-run-failures.md` (renumber
against both `todo/` and `done/` at write time) for the first slice above.

---

# Outcome

Agreed. First slice to become an implementation phase doc next; phases 78-80 above are recorded as
follow-on work, not yet written up in detail.
