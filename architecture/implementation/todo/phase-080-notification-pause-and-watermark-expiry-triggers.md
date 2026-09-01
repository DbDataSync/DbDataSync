# Phase 80 — Notifications: pause and watermark-expiry triggers

**Status**: Not started. **Depends on phase 77** (`architecture/implementation/todo/phase-077-notification-core-and-run-failures.md`)
— the `Notifications` table, API, and per-user cursor. If phase 77 hasn't shipped yet when this is
picked up, build it first (same dispatch is fine; this phase is small once that pipeline exists, per
the planning doc's own estimate).

**Plan reference**: `architecture/planning/done/notifications.md` — third of four slices (email delivery
and the latency trigger are the remaining two, neither built or numbered yet — re-check
`implementation/todo/`/`done/` before assigning either).

## The gap

Phase 77 builds the notification pipeline and wires exactly one producer (run failure). Two more
straightforward producers were scoped in planning but not built:

- **Replication paused** — `PauseEvents.Action = 'Paused'` (`Migrations.cs:372-380`, phase 64). The row
  already carries `TaskName`, `Note`, `PerformedBy` — everything a notification needs to render, no new
  data to compute.
- **Watermark/position expired** — `PositionExpiredException`, currently caught once, at
  `RunExecutor.cs:300`. The exception already carries the mapping and position detail.

## What to build

### Pause producer

Wherever `PauseEvents` rows are written for `Action = 'Paused'` (`TaskRunStore.SetPaused` or wherever
that insert actually lives — confirm the exact call site before assuming), insert a `Notifications` row
alongside it, in the same transaction if that store already wraps the `PauseEvents` insert in one (match
whatever atomicity guarantee already exists there rather than adding a second, separate write that could
land without its `PauseEvents` counterpart, or vice versa). **Only `Paused`, not `Resumed`** — a
resume isn't something anyone needs proactively told about; state this explicitly rather than silently
notifying on both and letting it go unnoticed as noise later.

### Watermark-expiry producer

At `RunExecutor.cs:300`'s existing `catch (PositionExpiredException ex)` block, insert a
`Notifications` row using the exception's own `TableName`/`PreviousWatermark`/`CurrentFloor` (or
whatever its actual property names are — reread `PositionExpiredException`'s definition before assuming)
so the notification's message can say specifically which mapping's position expired and by how much,
rather than a generic "something expired" — the exception already carries everything needed to be
specific.

## What this phase should not do

- Build phase 77 beyond what's needed to unblock this phase, if it hasn't shipped yet — implement it as
  written in its own phase doc, not a reduced version bent to fit this one.
- Email delivery for either trigger — a separate, not-yet-numbered phase (re-check
  `implementation/todo/`/`done/` for the next free number at write time).
- The latency trigger — also a separate, not-yet-numbered phase.
- Any UI beyond what phase 77 already built (the bell/badge) — these producers feed the same feed, no
  new surface needed.

## How to verify

- A test asserting pausing a replication produces exactly one `Notifications` row, and resuming it
  produces none.
- A test asserting a `PositionExpiredException` during a run produces exactly one `Notifications` row
  with the specific mapping/position identified in its message.
- A test asserting neither producer duplicates a notification on a retried operation (e.g. pausing an
  already-paused replication, if that's reachable at all — check `SetPaused`'s own idempotency handling
  first).
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.
