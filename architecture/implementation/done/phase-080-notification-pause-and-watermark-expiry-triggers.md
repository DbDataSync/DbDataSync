# Phase 80 — Notifications: pause and watermark-expiry triggers

**Status**: Done — shipped in `74c6c3e`. See Outcome. **Depended on phase 77** (`architecture/implementation/done/phase-077-notification-core-and-run-failures.md`)
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

---

## Outcome

**Shipped** in `74c6c3e`, one commit, on top of phase 77 (`6f4bbbe..8daa252`), built in the same
dispatch as that phase and immediately after it.

### What was built

Both producers, in `TaskRunStore`, each inside the transaction that already records the event it
announces:

- **Pause** — `SetPaused` reads `Tasks.Paused` at the top of its existing transaction and writes a
  `ReplicationPaused` notification alongside the `Tasks` update and the `PauseEvents` insert. The
  message names the replication, who held it and the note they left.
- **Watermark expiry** — `CompleteRun` writes a `PositionExpired` notification instead of a
  `RunFailed` one when `failureKind` is `RunFailureKinds.PositionExpired`, carrying
  `PositionExpiredException`'s own message and the mapping the run belonged to.

No schema change, no new API, no SPA change — which is what phase 77's `Kind` column and the shared
`NotificationStore.Insert` door existed for.

### Deviations from the doc

**The watermark-expiry producer is in `CompleteRun`, not in `RunExecutor`'s `catch` block.** The doc
named `RunExecutor.cs:300` specifically. Four reasons it went in the store instead, in descending
order of weight:

1. **It would have been a second notification, not the only one.** Phase 77's producer already fires
   for every run that ends `Failed`, and a position expiry *is* a run ending `Failed` — `RunExecutor`'s
   catch block calls `CompleteRun(…, RunStatus.Failed, …, RunFailureKinds.PositionExpired)` and always
   has. Writing a second row at the catch site would have announced one event twice, and suppressing
   the first would have meant a suppression mechanism existing solely to undo a producer placement.
   Keying off `FailureKind` inside the one producer makes it one row that is more specific, rather than
   two rows that overlap.
2. **`RunExecutor` cannot write state directly.** Phase 39 made the API the state store's sole writer;
   a worker reaches it through `IRunnerState`. A notification from the catch block would have needed a
   new interface method, a new `StateProtocol` request record, a new endpoint, a new
   `JournalOperation`, and a `JournalRecovery` case — plumbing whose only purpose would be to move a
   string that `CompleteRun` is already being handed on the very next line.
3. **Atomicity, which this doc asks for by name in its pause section.** A separate write from the
   worker could land without its `CompleteRun` counterpart, or the reverse. In the store it is one
   transaction, on the same reasoning the doc applies to `PauseEvents`.
4. **Journal replay gets it for free.** A worker that could not reach the owner journals its
   completion, including the `FailureKind`; recovery replays it through `CompleteRun` and the
   notification is produced then. A catch-block producer would have missed that case entirely unless a
   journal operation were added for it too.

**Nothing is lost in specificity, which was the doc's actual requirement.** The concern was a message
that says which mapping and which position expired rather than "something expired". The exception's
properties are `TableName`, `StoredPosition`, `OldestAvailable` and `Mechanism` (not
`PreviousWatermark`/`CurrentFloor`, as the doc guessed — reread as instructed), and its own message is
built from all four. `RunExecutor` already passes that message as `errorSummary`, and the `TaskRuns`
row supplies `MappingName`. So the notification names the mapping, the table, the mechanism, the
expired position and the oldest surviving one — every field the doc asked for, without reassembling
them into a worse sentence than the exception already wrote.

### Judgment calls

- **Only `Paused`, never `Resumed`** — as instructed, and stated in the code rather than left as an
  unexplained `if`, so "why don't resumes notify" has an answer where somebody will look for it.
- **The pause producer fires on the transition, not on the write.** `SetPaused` has no idempotency
  handling — the doc asked this to be checked first, and it was: `ReplicationsController.SetPaused`
  does not short-circuit a redundant pause, and `SetPaused` deliberately writes a second `PauseEvents`
  row for it, because re-pausing with a new note is a real act and the history is an audit trail. A
  notification is not an audit trail. Re-pausing an already-held replication now keeps its `PauseEvents`
  row and produces no second notification: "this is paused" is not news twice, and announcing it again
  would make one stuck replication look like a spreading outage. This matches phase 77's run-failure
  producer exactly, which was already transition-based for the same reason.
- **The pause notification has no `MappingName` and no `RunId`.** A pause is about the replication.
  Populating either would make the row link somewhere it does not belong; both columns were made
  nullable in phase 77 anticipating precisely this kind.
- **A note-less pause does not render a dangling separator.** Small, but the message is the whole UI.

### How it was verified

- `NotificationStoreTests` gained 6 (`Category!=Integration`): a pause producing one row naming who
  and why; a note-less pause reading as a sentence; a resume producing nothing while both
  `PauseEvents` rows survive; a re-pause announcing once while keeping its second `PauseEvents` row;
  an expiry as its own kind naming table and both positions; and the two kinds being distinguishable
  without reading the message.
- `NotificationsEndpointTests.WatermarkExpiry` (1) — placed in `DataSync.Api.Tests` rather than
  `DataSync.State.Tests` because it uses the **real** `PositionExpiredException`, which the state test
  project does not reference. The point is that the exception's own wording survives into the
  notification; a hand-copied message string would only have proved that a string was copied. The
  failure is recorded exactly as `RunExecutor`'s catch block records it — the same simulation
  `ResyncTests` already uses for this failure kind.
- Full suite green: `Category!=Integration` **934 passed**, `Category=Integration` **201 passed**, 0
  failures in either. No frontend change was made or needed — these producers feed the bell phase 77
  built — so `tsc -b`/SPA build were unchanged from that phase's clean run.

### Notes

`ConcurrentRunsIntegrationTests.TriggeringManyReplicationsAtOnce_AllSucceed_WithNoStateStoreContentionFailures`
failed twice during this work and was investigated rather than retried away, because a test with
"StateStoreContention" in its name failing right after a transaction was added to `CompleteRun` is not
something to wave through. It is unrelated: the deadlock is in the test's own `DisposeAsync`, on
`ALTER DATABASE … SET SINGLE_USER WITH ROLLBACK IMMEDIATE` / `DROP DATABASE` against the shared SQL
Server container, contending with other integration assemblies running in parallel. The test body's
assertions passed; only teardown threw, and it passes 3/3 when its assembly runs alone. Pre-existing
infrastructure flakiness in a file this phase has no business touching, recorded here so the next
person who sees it does not have to re-derive that.

Three kinds now exist and the feed still has no consumer that switches on `Kind` — the bell renders
`Message`. That remains deliberate. The column is for the notification centre that filters and
icon-codes, and phase 77's argument holds: a UI that switched on kind would have to be taught each new
one.

The two remaining slices from `architecture/planning/done/notifications.md` are unbuilt and unnumbered:
SMTP delivery, and the latency trigger (CDC-only lag plus a worker heartbeat that does not exist yet).
Per that doc's own closing note, neither number is reserved until its phase doc is committed.
