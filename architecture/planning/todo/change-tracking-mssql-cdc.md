# Change tracking — SQL Server CDC, alongside the Change Tracking we already have

**Status: proposal, not agreed.** Read `change-tracking-strategies.md` first.

SQL Server is the only engine here that already has a change-tracking reader
(`MsSqlChangeTrackingReader`, phase 3, hardened in phase 12). This document is about the *other*
mechanism SQL Server offers, and about why building it is worth doing even though CT already works.

## CDC is not an upgrade to Change Tracking. It is a different tool.

This needs saying first, because the names invite the wrong conclusion.

| | Change Tracking (built) | CDC (proposed) |
| --- | --- | --- |
| what it records | that a row changed, and its key | every change, with all column values |
| history | **net change only** — one row per key since the version | every intermediate version, in order |
| old values | no | yes, with `'all update old'` |
| position | version number (bigint) | LSN (`binary(10)`) |
| how it is populated | synchronously, in the transaction | asynchronously, by a SQL Agent job reading the log |
| source cost | write-path overhead | log-reader job, plus change-table storage |
| availability | Standard and up, Azure SQL DB | needs SQL Agent — historically not on Azure SQL DB |

**For replicating a table to a mirror, CT's net-change semantics are better, not worse.** A row updated
50 times between passes yields one row from CT and 50 from CDC, and the target only needs the final
state. Anyone assuming CDC is the more advanced choice will make their replication 50× more expensive
for no benefit.

CDC earns its place where the *history itself* is the payload: an audit target, a slowly-changing
dimension that needs intermediate versions, or a target that needs before-images to compute a delta.

So this should ship as a **second reader Kind alongside the first**, chosen deliberately — exactly as
`MsSqlMerge` and `MsSqlDeleteInsert` coexist because they suit different targets.

## The mechanism

```sql
DECLARE @from binary(10) = sys.fn_cdc_increment_lsn(@storedLsn);
DECLARE @to   binary(10) = sys.fn_cdc_get_max_lsn();

SELECT __$start_lsn, __$seqval, __$operation, __$update_mask, *
FROM   cdc.fn_cdc_get_all_changes_dbo_Orders(@from, @to, N'all');
```

A `SELECT`, so the bounded-window pattern from the strategies doc applies unchanged, and the reader is
the same shape as the one that already exists.

- `__$operation`: `1` delete, `2` insert, `3` update-before, `4` update-after. With `N'all'` only `1`,
  `2`, `4` appear; `N'all update old'` adds `3`. Mapping to `ChangeOperation` is direct.
- `__$start_lsn` ordered with `__$seqval` gives change order within a transaction.
- **The watermark is the LSN**, stored as a hex string. `ReadResult.NewWatermark` is already an opaque
  string, so nothing in the state store changes — the same point the strategies doc makes generally.

### Two boundary details that are easy to get wrong

- **`sys.fn_cdc_increment_lsn`.** `fn_cdc_get_all_changes` is inclusive of `@from`. Passing the stored
  LSN unchanged re-reads the last pass's final change every time. Harmless (writers upsert) but it
  makes every pass look like it found work, which is exactly the kind of thing that makes a monitoring
  graph lie.
- **`fn_cdc_get_max_lsn()` returns NULL** when the capture job has not run yet, or is stopped. Treating
  NULL as "no changes" is right; treating it as a valid position is not. A stopped capture job is a
  silent-failure mode worth detecting and reporting, because from the reader's side it is
  indistinguishable from a quiet source.

## Where CDC is genuinely better: read consistency

Phase 12 spent real effort on a bug that CDC does not have.

The Change Tracking reader joins `CHANGETABLE` to the base table to get current values. Between the
two, the base table can move — 28,000 anomalous rows out of 312,000 under load — which is why the
reader gained an opt-in snapshot-isolation mode, and why that mode then leaked isolation level across
pooled connections and needed `RestoreDefaultIsolationLevelAsync`.

**CDC has no such join.** The change table already holds the column values as of the change, harvested
from the log. There is nothing to race against, no snapshot transaction, and no error 3952 to handle.

That is a substantive argument for CDC beyond the audit use case, and it is one only this codebase can
make — it is the direct result of a bug already paid for. For a high-write table where the CT reader
currently needs snapshot isolation to be correct, CDC removes the problem rather than mitigating it.

## Retention and position-expired

`sys.sp_cdc_add_job @job_type = N'cleanup'` prunes change tables past a retention window (default 3
days). A stored LSN older than `sys.fn_cdc_get_min_lsn('dbo_Orders')` cannot be served.

The check is the exact analogue of the `CHANGE_TRACKING_MIN_VALID_VERSION` check the CT reader already
does, and the message should read the same way. This is the argument for hoisting
`PositionExpiredException` into the abstractions rather than writing a second bespoke `throw`: two
readers in the same driver with the same failure and two different messages is how a support burden
starts.

## Enablement

```sql
EXEC sys.sp_cdc_enable_db;
EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'Orders',
     @role_name = NULL, @supports_net_changes = 1;
```

`@supports_net_changes = 1` requires a primary key and creates `fn_cdc_get_net_changes_*` alongside the
all-changes function. **Worth doing** — it gives CT's collapsing behaviour with CDC's join-free
consistency, which for the replication use case is the best of both. The reader should prefer net
changes when the capture instance supports it and fall back to all-changes when it does not, and should
say which it used in the run log.

That combination — net changes, no base-table join — is arguably the best change-tracking mode
available on SQL Server for this tool's purpose, and it is not the one currently built.

## Managed SQL Server

Azure SQL Managed Instance has SQL Agent and supports CDC. Azure SQL Database gained CDC support
(without Agent, driven internally) on newer service tiers. Neither is universal, so the reader must
degrade honestly: if `sys.sp_cdc_enable_db` is unavailable, say so at configuration time rather than
failing at the first run.

## Suggested scope for a phase

Small, because the driver, the pipeline and the position handling all exist:

- `MsSqlCdcReader` (Kind `MsSqlCdc`), `DetectsDeletes = true`, **not** `ISegmentExpandingReader` — a log
  position has no meaningful answer to "bucket 3 of 4", and the strategies doc's rule applies.
- prefer `fn_cdc_get_net_changes_*`, fall back to `fn_cdc_get_all_changes_*`
- LSN watermark as hex; `fn_cdc_increment_lsn` on the lower bound
- min-LSN check raising the shared `PositionExpiredException`
- a capture-job health check surfaced the way phase 19's connection test is
- integration tests against the existing `datasync-mssql-source` container, which already has Agent

The one piece of genuinely new shared machinery is `PositionExpiredException` and whatever
`RunExecutor` does with it — which is the right thing to build here, on the engine where both sides of
the comparison already exist, rather than for the first time on Postgres.

## Open questions

- **Should a position-expired failure auto-enqueue a reload?** The work queue and the backfill
  machinery both exist, so it is a small step from "tell the operator" to "fix it". It is also a large
  automatic operation to trigger on its own. Probably: surface it as a one-click action rather than
  doing it silently.
- **Does anyone need `'all update old'`?** Before-images are the thing CDC uniquely offers and nothing
  in DataSync consumes them today. They would be a row-transform input — which is a scripting question,
  and a reason to sequence this after the scripting host rather than before.
