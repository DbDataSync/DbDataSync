# Phase 75 — A lightweight polling gate for CDC and Change Tracking

**Status**: Not started. Depends on phase 74 (shipped, `188b799..7272905`) — `ChangeWatermarks` is now
keyed `(TaskName, MappingName, SourceTable)`, and this phase's gate reads each mapping's own row from
that table by exactly that key.

**Plan reference**: `architecture/planning/done/watermark-key-and-lightweight-polling.md`, issue 3 —
**revised below** before implementation, per three follow-up notes given after that doc was written and
before this phase was dispatched. The planning doc's shape (a shared per-database counter, fetched once
per tick, gating whether to dispatch) still holds; what changes is what the gate compares against and
what gets stored.

## The gap, restated

`SchedulerService.Tick` decides what's due purely from local config and state — it never talks to a
source system. Every due CDC or Change-Tracking mapping gets its own `WorkItem`, its own connection, its
own database-wide counter fetch (`sys.fn_cdc_get_max_lsn()` / `CHANGE_TRACKING_CURRENT_VERSION()`),
every cycle, even when nothing in that source database changed.

## Revision 1 — the gate must be keyed by source mechanism, not just database

The original design stored one row per `(ConnectionName, Database)`. That collides the moment a
database has *both* mechanisms active — one table under CDC, another under Change Tracking, same
database. CDC's max LSN is a `byte[]` position in the transaction log; Change Tracking's current version
is a monotonic `long`. Sharing one row means whichever mechanism polls second overwrites the other's
value with a number/format it doesn't understand — the exact class of bug phase 74 just fixed one layer
down, reintroduced here if not careful.

Every key in this phase — the gate's own state, the audit history in revision 2, the grouping
`SchedulerService` uses to decide which mappings share one source round-trip — is
`(ConnectionName, Database, SourceKind)`, where `SourceKind` distinguishes `Cdc` from `ChangeTracking`.
Two mappings on the same database but different mechanisms are two groups, two counter fetches, two
stored rows, never one.

## Revision 2 — track every value seen, with a timestamp, purged like everything else

Log every counter fetch, not just the most recent one: `(ConnectionName, Database, SourceKind, Value,
CheckedAtUtc)`, one row per fetch, append-only. This is an audit/history trail — "what did we see, and
when" — not the gate's comparison mechanism (see revision 3: the gate compares against each mapping's
own watermark, not a cached last-seen value, so this table is written but never read back by the gate
itself).

Purge it the way `TaskRuns`/`Logs` already are: `RunPruningService` (phase 60) age-purges on
`DataSync:RunRetentionDays` on its existing tick — extend it to also purge rows from this table older
than the same `maxAge`, rather than inventing a second retention knob or a second background service.
There's no `maxPerMapping`-equivalent concept here (this table isn't mapping-scoped), so only the
age-based half of `PruneRuns`' two criteria applies. Confirm in the implementation phase that reusing
`RunRetentionDays` for a check-history table with a very different write rate (one row per tick per
source database, versus one row per run) doesn't produce a table that outgrows what a shared retention
window was tuned for — if it does, a separate, smaller-by-default retention knob for this table alone is
a reasonable adjustment, but start by trying to share the existing one before adding a second setting
users have to learn.

## Revision 3 — the gate must compare against each mapping's own progress, not a cached "last checked" value

This is the one that would have shipped a real bug. `MsSqlChangeTrackingReader` supports bounded reads
(`BoundedRead.Read(options)` at `MsSqlChangeTrackingReader.cs:89` — a row cap on incremental passes,
opted into per mapping). A mapping draining a large backlog under a row cap advances its own stored
watermark only as far as the rows it actually processed each pass (`ReadResult.WatermarkAfterRead`,
`Bounded?.Reached ?? NewWatermark`) — not to the database's current max. It can legitimately take many
passes to catch up, with **no new writes happening at the source in between passes** — meaning the
database-wide counter does not move between those passes even though the mapping still has real,
already-known work to do.

A gate that skips dispatch whenever "the counter hasn't changed since the last time the gate checked"
would misread that as "nothing to do" and stall a mapping mid-drain, potentially for as long as the
mapping's schedule keeps getting suppressed. (CDC has no bounded-read option today —
`MsSqlCdcReader.cs` never calls `BoundedRead` — so this specific failure mode doesn't yet exist for CDC
structurally, but the gate's logic shouldn't rely on that staying true.)

**Fix**: don't cache a "last checked" value to compare against at all. Each tick, for a due group
`(ConnectionName, Database, SourceKind)`, fetch the source's counter once (and log it per revision 2),
then for *each* due mapping in that group individually, compare the freshly-fetched value against that
mapping's *own* current watermark — already sitting locally in `ChangeWatermarks` (phase 74, keyed by
`MappingName`), no source round-trip needed per mapping, just a local state read. Reuse the exact
comparison each reader already trusts rather than inventing a second one: `MsSqlCdcCatalog.Compare` for
CDC, a plain numeric compare for Change Tracking's `long` version.

- A mapping whose stored watermark is already `>=` the freshly-fetched value: skip it this tick — it's
  genuinely caught up.
- A mapping whose stored watermark is behind: dispatch it, exactly as today, gate or no gate.
- A mapping with no stored watermark yet (first pass): always dispatch — there's nothing to compare
  against, and a first pass isn't gated by this mechanism at all.

One source round-trip per `(ConnectionName, Database, SourceKind)` group per tick, however many mappings
share it — the optimization is unchanged — but the skip/dispatch decision is now correct for bounded and
unbounded readers alike, because it's answering "is this specific mapping caught up," not "did anything
change since some other point in time."

## What to build, updated for the above

### State: two tables, not one

- **Gate audit history** (revision 2): append-only, `(ConnectionName, Database, SourceKind, Value,
  CheckedAtUtc)`. Written every time the gate fetches a counter. Purged by `RunPruningService`.
- No separate "last known value" table — revision 3 removed the need for one. If the audit history table
  can cheaply answer "what was the most recent value we saw for this group" (e.g. for a status/debug
  view later), that's a property of it being append-only with a timestamp, not a second table.

### The gate, in `SchedulerService`

Before enqueuing this tick's otherwise-due CDC/Change-Tracking mappings: group by
`(ConnectionName, Database, SourceKind)`. For each group, fetch the counter once, log it (revision 2),
then decide per-mapping using each mapping's own `ChangeWatermarks` row (revision 3). Unreachable
source or any fetch error: **fail open** for that whole group — dispatch every due mapping in it
normally, exactly as `SchedulerService` behaves today without this phase. Log the failure; don't throw
out of `Tick()`; don't let one unreachable database affect any other group's gate in the same tick.

### The architectural shift, worth stating in the retrospective

`SchedulerService` does no source-system I/O today — this phase gives it its first. A tick can now be
slowed or partially failed by an unreachable source, where before it only ever touched local config and
state. Fail-open is what keeps that a resilience property rather than a regression.

### Reader-kind scoping

Only mappings using `MsSqlChangeTrackingReader` or `MsSqlCdcReader` participate, split by `SourceKind`
per revision 1. Every other reader kind is scheduled exactly as today.

## Explicitly out of scope, decided in planning

- **Trigger-audit.** No database-wide analogue exists — one shadow table per source table, not one
  shared table — and building one would create a shared lock-contention point across otherwise-
  independent tables' writers, working against the very cost a trigger already imposes. A softer,
  heuristic option (each engine's own async, non-locking modification-stats view, fail-open only) is
  possible but needs its own per-engine research; left for a later, separate phase.
- Any change to what an individual reader does once dispatched. `MsSqlCdcReader`'s own per-table
  short-circuit (skip the `CHANGETABLE`/CDC scan if its own stored LSN is already caught up to the
  database max) is unrelated to this gate and untouched by it. `MsSqlChangeTrackingReader`'s missing
  equivalent (`ReadChangesAsync` never compares `targetVersion` to `previousWatermark` before issuing the
  incremental query) is a real, separate small gap — note it in the retrospective, don't fix it here;
  this phase's scope is the shared dispatch-level gate, not per-reader efficiency once dispatched.

## How to verify

- A test with two mappings on the same `(ConnectionName, Database, SourceKind)` group, both due,
  asserting the counter is fetched once per tick, not twice.
- A test with one CDC-tracked table and one Change-Tracking-tracked table in the *same* database,
  asserting they produce two groups, two counter fetches, two independent audit rows — the collision
  revision 1 exists to prevent.
- A test reproducing revision 3's scenario directly: a Change-Tracking mapping with a row cap, mid-drain
  of a backlog across multiple passes with no new source writes between them (counter unchanged), and a
  mapping whose own watermark is already caught up in the same group — asserting the first still gets
  dispatched while the second is skipped, in the same tick.
- A test asserting a first-pass mapping (no stored watermark) is always dispatched regardless of the
  gate.
- A test asserting a counter-fetch failure enqueues its whole group normally (fail-open), doesn't throw
  out of `Tick()`, and doesn't affect another group's gate in the same tick.
- A test confirming `RunPruningService` purges aged rows from the new audit-history table on its existing
  tick, using the same retention window as `TaskRuns`/`Logs` unless revision 2's table-growth check found
  reason to give it its own.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

---

## Outcome

**Shipped** in `63e83f1..5196902`, in two commits: the audit table and its retention, then the gate.

### What was built

`ChangePollingGate` (`src/DataSync.Api/Services/ChangePollingGate.cs`) sits between
`SchedulerService`'s due-ness arithmetic and its `Enqueue` calls. It takes the tick's due mappings,
groups them by `(ConnectionName, Database, SourceKind)`, fetches one counter per group through
`IChangeCounterSource`, records it, and returns the subset that has work waiting — in the order it was
asked, which grouping would otherwise scramble.

`SchedulerService` gained one collaborator and one funnel. `TickContinuous` and `TickPeriodic` used to
enqueue inline; both now collect their due mappings and hand them to a shared `EnqueueAsync`, which is
the single place the gate is consulted. Periodic goes through it too: a cron occurrence firing over a
source nothing has written to since the last one is the same wasted pass, and it is the mode where a
hundred mappings arrive at once.

Everything else is what the doc asked for: `ChangeCheckHistory` and `ChangeCheckStore` in
`DataSync.State`, purged on `RunPruningService`'s existing tick.

### Deviations from the doc

**A second retention knob, `DataSync:ChangeCheckRetentionDays`, defaulting to 7 days.** Revision 2 asked
for `RunRetentionDays` to be tried first and for the growth to be checked against what that window was
tuned for. The check fails, by two orders of magnitude. Run history is one row per run; this is one row
per tick per source-database group, and the tick is every five seconds whether or not anything is due —
17,280 rows per group per day, so the 90-day default is about 1.5 million rows for a single group and
tens of millions for a deployment with a handful. That is not a retention policy, it is the unbounded
growth `RunRetentionDays` exists to prevent arriving through a different table. Seven days is the span
this history is actually questioned over: "was the source quiet overnight, or did the gate stop looking"
is asked about last night, never about last quarter. The doc's escape hatch, taken for the reason it was
offered.

**The column is `SourceDatabase`, not `Database`.** `DATABASE` is reserved in T-SQL, and `Migrations.cs`
is one file rendered for three engines — a name needing quotes in one of them needs them everywhere or
nowhere. Cost: a word.

Nothing else departs. No SPA change was needed and none was made; nothing in `DataSync.Web` mentions
watermarks, counters or the scheduler's dispatch decision.

### Judgment calls

- **`SourceKind` is the reader kind string, not a new enum.** `MsSqlDriverKinds.Cdc` and
  `.ChangeTracking` already are the vocabulary: they are what config states, what `WorkQueue` and
  `TaskRuns` record in `ReaderKind`, and what `MsSqlProvisioner` dispatches on. A second vocabulary for
  "which mechanism" would need translating back to the first at every boundary and would have exactly
  two values in common with it. `ChangeCounters.IsGated` is the whole of the new type information.
- **The gate is a collaborator, not more of `SchedulerService`.** The scheduler's body was due-ness
  arithmetic over local config and state; this is source I/O, grouping, per-mechanism comparison and a
  fail-open policy. Apart, the gate is testable without an API host — which is what made eight unit
  tests possible against real config and real state tables — and the scheduler still reads as the
  schedule it is.
- **The comparison helpers needed no new dependency.** `DataSync.Api` already references
  `DataSync.Drivers.MsSql`, so `MsSqlCdcCatalog.Compare`, `FromWatermark` and `GetMaxLsnAsync` were
  already in scope. One thing had to change: `MsSqlChangeTrackingReader.GetCurrentVersionAsync` was
  private and is now public. Reused rather than reimplemented deliberately — a gate issuing its own
  near-equivalent statement would be a second definition of "where has this source got to", free to
  drift from the one a pass reads against.
- **Only the counter fetch is behind an interface.** It is the one thing that talks to a source;
  everything else the gate does is arithmetic over local state, and faking any of it would have made the
  tests a rehearsal rather than a test.

### The bug this nearly shipped

The counter fetch has to `ChangeDatabase(source.Database)` first, because both readers do
(`MsSqlChangeTrackingReader.cs:66`, `MsSqlCdcReader.cs:57`). It was written without that step, on a
grep that had been truncated by `head -20` and appeared to show the readers not switching either.

Neither `fn_cdc_get_max_lsn()` nor `CHANGE_TRACKING_CURRENT_VERSION()` errors in the wrong catalog —
they answer *about* it, and in a database without the mechanism enabled the answer is null or zero. So a
gate reading the connection's default database while the reader read the mapping's would have compared
two databases against one watermark and suppressed real work, silently, in one direction only, for as
long as the two stayed out of step. It would have looked exactly like the source being quiet.

`ChangeCounterScopeTests` exists for this: one connection, one statement, two catalogs, two different
answers. It is the only test here whose subject is a fact about SQL Server rather than about this code,
and it is the only one that would catch the regression.

### The architectural shift

`SchedulerService` now does source-system I/O. Before this phase a tick touched only local config and
state and could not be slowed by anything outside the process; now an unreachable or distant source is
inside its loop. Fail-open per group is what keeps that a resilience property: a fetch failure dispatches
that group's mappings exactly as the scheduler behaved before this phase existed, logs, and leaves every
other group in the same tick still gated. Nothing throws out of the tick.

Worth stating plainly because the failure mode has changed shape rather than gone away: a source that is
*slow* rather than down still costs the tick its latency, serially, one group at a time. Nothing here
bounds that. It has not been a problem to build for — a tick is five seconds and a counter fetch is a
single scalar query — but it is the thing to look at first if ticks ever start running long, and
parallelising the per-group fetches is the obvious lever.

### A gap noted, not fixed

`MsSqlChangeTrackingReader.ReadChangesAsync` never compares `targetVersion` to `previousWatermark`
before issuing its incremental query, where `MsSqlCdcReader` does exactly that short-circuit at
`MsSqlCdcReader.cs:112`. A caught-up Change-Tracking mapping that reaches the reader therefore issues a
`CHANGETABLE` query that cannot return rows. This phase's gate makes that rarer but does not close it —
the gate is skipped for a first pass, fails open for an unreachable source, and does not run at all for
a Backfill or a manually triggered run. It is a small, separate, per-reader fix; out of scope here per
the doc, and left as such.

### How it was verified

- `ChangePollingGateTests` (8 tests, `Category!=Integration`), against real `ConfigRepository`, real
  `DriverRegistry`, real `ChangeWatermarks` and `ChangeCheckHistory`, with only the round-trip faked:
  - `TwoMappingsOnOneSourceGroup_FetchTheCounterOnce` — the optimization itself, which is invisible in
    the return value and only observable in the fetch log.
  - `CdcAndChangeTrackingInOneDatabase_AreTwoGroupsWithTwoAuditRows` — revision 1's collision, asserted
    as two fetches and two audit rows holding values in their own formats.
  - `AMappingMidDrainIsDispatched_WhileACaughtUpSiblingIsSkipped` — revision 3, directly: watermark 40
    and watermark 100 in one group against an unmoved counter of 100, in one tick, opposite outcomes.
  - `AMappingWithNoWatermarkIsAlwaysDispatched`, `AnLsnAtTheDatabaseMaximum_IsSkipped` (both directions,
    one byte apart in the low position), `ANullCounterIsNotSilence_AndDispatches`,
    `AnUngatedReaderKindIsDispatchedUntouched`.
  - `AnUnreachableSourceFailsOpenForItsOwnGroupOnly` — every mapping caught up as far as local state
    knows, so anything dispatched is dispatched because the gate could not answer. The failed group goes
    out whole; the healthy group in the same tick is still gated and still skips.
- `ChangeCheckStoreTests` (5) — append-only including the repeat reading, the two mechanisms as separate
  rows, a recorded null, and both prune windows.
- `ChangeCheckPruningTests` (2) — the claim that justifies this table having no background service of
  its own: `RunPruningService.PruneAsync` ages out both histories on one call, on independent windows.
- `RunRetentionOptionsTests` gained 2: the shorter default, and that it does not follow
  `RunRetentionDays`.
- `ChangeCounterScopeTests` (1, `Category=Integration`) — see above.
- Cross-engine coverage came free again: `CrossEngineStateTests`' 39 integration tests run the whole
  migration set against SQLite, Postgres and SQL Server, and passed first time including the new table
  and index — which is what confirmed the `SourceDatabase` rename was the right call rather than a
  guess.
- Full suite green: `Category!=Integration` **894 passed**, `Category=Integration` **201 passed**, 0
  failures in either. No frontend change, so no `tsc -b`/SPA run was required; nothing under
  `src/DataSync.Web` was touched.

### Notes

`RunPruningService.PruneAsync` is public rather than private, for the two-histories-one-sweep test. It
is the claim that would silently stop being true if the second delete were ever dropped, and there is no
`InternalsVisibleTo` in this solution to reach it otherwise.

The gate never reads `ChangeCheckHistory` back. That is worth remembering when someone later wants a
status view of "what did we last see for this source" — the table can answer it, being append-only with
a timestamp, but it will want its own index on the group columns, deliberately not added here because
this table's writes are very frequent and its only reader today is the age purge.
