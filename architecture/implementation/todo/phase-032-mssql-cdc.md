# Phase 32 — SQL Server CDC, and position-expired recovery (planned)

**Status**: Planned, not started
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
