# Phase 32 — SQL Server CDC, and position-expired recovery

**Status**: Done
**Plan reference**: `architecture/planning/done/change-tracking-mssql-cdc.md`, and the shared machinery
identified in `architecture/planning/done/change-tracking-strategies.md`.

## Why this one first

`change-tracking-strategies.md` recommends it, and the reason is not that CDC is the most wanted
mechanism — it is that this is the only engine where **both sides of the comparison already exist**.
The driver is built, the Docker container has SQL Agent, and `MsSqlChangeTrackingReader` is right there
to measure a second reader against. Every other log-based reader in the planning set needs a new
provider, a new container, or a driver that does not exist yet.

So the shared machinery gets built here, once, against something that can be tested — rather than for
the first time on Postgres, where a replication slot's failure modes would be competing for attention.

## Part 1 — `MsSqlCdcReader`

CDC is **not an upgrade to Change Tracking**, and the phase doc should keep saying so, because the
names invite the opposite conclusion. CT reports *net change since a version* — one row per key. CDC
reports *every intermediate change*. For replicating a table to a mirror, CT's semantics are better:
a row updated fifty times between passes is one row from CT and fifty from CDC, and the target only
needs the final state.

CDC earns its place where the history is the payload (an audit target, a slowly-changing dimension)
and — the argument only this codebase can make — where **read consistency** matters.

### The read-consistency argument

Phase 12 spent real effort on a bug CDC does not have. The CT reader joins `CHANGETABLE` to the base
table for current values, and between the two the base table can move: 28,000 anomalous rows out of
312,000 under load. That produced an opt-in snapshot-isolation mode, which then leaked isolation level
across pooled connections and needed `RestoreDefaultIsolationLevelAsync`, and an error-3952 path that
only surfaces on the first `ReadAsync`.

**CDC has no such join.** The change table already holds the column values as of the change, harvested
from the log. Nothing to race, no snapshot transaction, no 3952.

### Prefer net changes

```sql
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'Orders',
     @role_name = NULL, @supports_net_changes = 1;
```

`@supports_net_changes = 1` needs a primary key and creates `fn_cdc_get_net_changes_*` beside the
all-changes function. The reader should **prefer net changes and fall back to all changes**, and log
which it used — that combination (CT's collapsing, CDC's join-free consistency) is arguably the best
change-tracking mode available on SQL Server for this tool's purpose, and it is not the one built.

### The statement

```sql
DECLARE @from binary(10) = sys.fn_cdc_increment_lsn(@storedLsn);
DECLARE @to   binary(10) = sys.fn_cdc_get_max_lsn();

SELECT __$start_lsn, __$seqval, __$operation, *
FROM   cdc.fn_cdc_get_net_changes_dbo_Orders(@from, @to, N'all');
```

Bounded-window, exactly as `MsSqlChangeTrackingReader` already is with `@previousVersion`/`@targetVersion`.

`__$operation`: 1 delete, 2 insert, 4 update (3 is update-before, only with `'all update old'`). The
watermark is the LSN as a hex string — `ReadResult.NewWatermark` is already an opaque string, so
nothing in the state store changes.

Two boundary details that are easy to get wrong and are the reason this needs tests rather than care:

- **`sys.fn_cdc_increment_lsn` on the lower bound.** `fn_cdc_get_*_changes` is inclusive of `@from`.
  Passing the stored LSN unchanged re-reads the last pass's final change every time — harmless, because
  writers upsert, but it makes every pass look like it found work, which is exactly the thing that
  makes a monitoring graph lie.
- **`fn_cdc_get_max_lsn()` returns NULL** when the capture job has not run or is stopped. Treating NULL
  as "no changes" is right; treating it as a position is not. A stopped capture job is
  indistinguishable from a quiet source from the reader's side, and is worth detecting and reporting.

## Part 2 — `PositionExpiredException`, shared

Every log-based mechanism retains history for a window, and a stored position older than that window
cannot be served. `MsSqlChangeTrackingReader` already handles its own case and throws:

> Change Tracking history for 'dbo.Orders' no longer covers watermark '41' (minimum valid version is
> 68). A full resync is required — clear the stored watermark for this table.

CDC's version is `sys.fn_cdc_get_min_lsn('dbo_Orders')` — the exact analogue. Writing a second bespoke
`throw` in the same driver, with a differently-worded message, is how a support burden starts. So:

```csharp
public sealed class PositionExpiredException(
    string tableName, string storedPosition, string oldestAvailable, string mechanism) : Exception(…);
```

in `DataSync.Drivers.Abstractions`, thrown by any reader, caught by `RunExecutor`, and surfaced as a
**distinct run outcome** rather than a generic failure — because "the source discarded what I needed"
has a specific fix and a generic failure does not.

The CT reader is moved onto it, so there is one message and one shape from the day there are two.

### The recovery is offered, not performed

The work queue and the backfill machinery both exist, so enqueuing a reload is a small step from
reporting the problem. It is also a large automatic operation to trigger on its own — a full reload of
a table that fell behind could be hours of work nobody asked for.

So: the run fails with the distinct outcome, and the Runs tab offers a one-click **Resync** that
enqueues the reload the operator would otherwise construct by hand. Visible, deliberate, one click.

## What this phase does not build

`'all update old'` before-images. They are the thing CDC uniquely offers and nothing consumes them
today; they would be a row-transform input, which is a scripting question and a reason to want this
*after* phases 23–24 rather than before.

Any change to `MsSqlChangeTrackingReader` beyond moving it onto the shared exception. CT stays the
default and stays better for the mirroring case.

## How to verify when built

- `Category=Integration` against the existing `datasync-mssql-source` container, which has Agent:
  enable CDC on a table, insert/update/delete, and read each operation back with the right
  `ChangeOperation`.
- Net changes preferred where available; all-changes used and logged where not.
- `fn_cdc_increment_lsn` on the lower bound asserted by reading twice with no intervening change and
  getting **zero** rows the second time — the assertion that catches the off-by-one.
- A stopped capture job reported as such rather than as a quiet source.
- `PositionExpiredException`: raised by CDC below the min LSN, raised by CT below the min valid
  version, and both producing the same distinct run outcome.
- Playwright: a position-expired run showing the Resync action, and clicking it enqueuing a reload.
- Full suite green.

## Open questions

- **Azure SQL.** Managed Instance has Agent and supports CDC; Azure SQL Database gained it on newer
  tiers without Agent. Neither is universal, so enablement must degrade honestly — say so at
  configuration time rather than failing at the first run.
- **Capture-job health** is a second thing to surface next to reachability (phase 19). It may belong
  with `planning/todo/run-metrics-and-monitoring.md` rather than here.

---

# Retrospective

Both parts built. The shared exception took an afternoon; the reader took the rest, and almost all of
that was boundary conditions that no amount of reading the documentation would have produced.

## The premise of the phase was half wrong

"The Docker container has SQL Agent" is what put CDC first, and it is true in the sense that the image
ships Agent — stopped. `sp_cdc_enable_table` succeeds without it, and then nothing is ever captured:
`fn_cdc_get_max_lsn()` stays null forever. One environment variable in `docker-compose.yml` and CI, and
worth recording because the failure is silent and looks like a quiet source.

## Four boundary bugs, all found by running it

The plan named one of these. The tests found four.

- **`sys.fn_cdc_increment_lsn` on the lower bound** — the one the plan warned about. Pinned by reading
  twice with no intervening change and asserting zero rows the second time.
- **A fresh capture instance's floor can be *ahead* of the database's max LSN.** Max is what the
  capture job has scanned; the floor is set when the job processes the enable. Storing the max as a
  first pass's watermark left a position every later pass read as expired history — a replication that
  reported data loss the moment it was set up. The full load now waits for the floor to exist and
  stores the later of the two, and says so plainly if the floor never appears rather than storing a
  position it will regret.
- **`fn_cdc_get_min_lsn` answers with all zeroes, not null**, in the window between enabling a table
  and the job reaching it. Read as a position it sorts below every real LSN, so every stored watermark
  looked expired. Normalised to null in the catalog, so the one caller cannot get it wrong.
- **`__$seqval` does not exist on net changes.** It orders changes within a transaction, and net
  changes has already collapsed them. Ordering by it unconditionally was an "Invalid column name" on
  the mode this reader *prefers* — so the preferred path was the broken one, which is the worst way
  round for something a unit test cannot see.

## A column, not a status

`PositionExpiredException` needed a distinct run outcome. A new `RunStatus` member was the obvious
shape and the wrong one: a position-expired run **is** a failed run, and giving it its own status
would have quietly dropped it out of every "how many failed" count in the app — the metrics card, the
Runs filter, the history query. What is different is the remedy, so a nullable `FailureKind` column
names the remedy and leaves the status alone.

## Resync does the half an operator would forget

The plan said the Runs tab should offer the reload. It offers the reload *and* clears the stored
watermark, because a reload against an expired position leaves the next incremental pass failing
exactly as before — and clearing a watermark is not something anybody thinks of while looking at a
failed run. Cleared first, so a crash in between leaves a replication that reads from the beginning
(slow, correct) rather than one that reloads and then fails again.

Offered, not performed, as the plan insisted: it is refused on any other failure, with a reason. A
Resync button on every failed run makes a full reload the general-purpose retry.

## A test-infrastructure bug that looked fixed once

`sp_cdc_enable_table` deadlocks against the running capture job's own msdb work often enough to matter
under a full suite run, and it **catches the 1205 and re-raises its own error with the original quoted
in the text**. Matching on `SqlError.Number` therefore never retried, and the suite passed once by
luck before failing again — which is exactly how a flake gets declared fixed. The fixture matches the
message.

The product deliberately does not retry: there the same failure reaches the operator with the server's
own "Rerun the transaction", which is honest, and retrying a DDL apply on somebody's behalf is a
separate decision from this phase.

## Verification

- `MsSqlCdcStatementTests` (11) — the incremented lower bound, both function names, the operation
  ordinal, per-function ordering, transforms aliased back, and the three operation codes plus the two
  that cannot arrive.
- `MsSqlCdcProvisioningTests` (6) — a fresh database enabling in the right order, net changes asked
  for when the table can support it, a table with no key enabled anyway with a warning rather than
  refused, an existing instance without net changes reported rather than recreated, and a quote in a
  name escaped for a string literal.
- `MsSqlCdcReaderTests` (8, integration) — every operation as itself including a delete carrying its
  values, the off-by-one, three updates collapsing to one row, the all-changes fallback returning both
  intermediate updates, an expired position, a table nobody captured, and a column added after capture
  reported against the mapping rather than the change table.
- `MsSqlChangeTrackingReaderTests` gained the same expiry assertion, which is the point of the
  exception being shared.
- `ResyncTests` (5) — the endpoint accepting a position-expired run, clearing the watermark, refusing
  an ordinary failure with a reason, 404 for an unknown run, and the failure kind reaching the client.
- Playwright 37 — the affordance absent where nothing has expired, and the endpoint refusing an
  ordinary failure. **The positive path is not covered end to end**: nothing in that suite can expire a
  source position without waiting out a retention window, and faking one would be testing the fake.
  The reader-side expiry is covered against a real capture instance instead.
- Full suite green: 765 .NET tests, 39 Playwright.

## What was not built, as planned

`'all update old'` before-images, and any change to the Change Tracking reader beyond moving it onto
the shared exception. CT stays the default and stays the better choice for mirroring.

## Open questions

- ~~**Azure SQL.**~~ Still open, and untestable here: neither Managed Instance nor Azure SQL Database
  is available to this repo's test infrastructure. The reader degrades honestly — a table with no
  capture instance says so at read time and at preview time — but "enablement must degrade honestly at
  *configuration* time" is not proven for an engine nobody can point this at.
- ~~**Capture-job health.**~~ Partly answered: a stopped capture job is reported at read time, with the
  job name to start. Surfacing it *next to reachability* before a run fails is still the better place
  for it, and still belongs with run monitoring rather than here.
- **New**: the capture instance's floor being unknown for a window after enablement is handled by
  waiting up to 30 seconds. That is right for a first pass and wrong for a pass that hits it under
  load; if a deployment ever sees it, the answer is probably to fail fast and let the scheduler retry
  rather than to hold a worker.
