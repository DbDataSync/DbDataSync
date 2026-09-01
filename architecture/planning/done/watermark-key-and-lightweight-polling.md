# Watermark key fixes, and a lightweight polling gate for CDC/Change Tracking

**Status: resolved — ready for implementation phase docs.**

Three related issues, found while explaining how watermarks are tracked today.

## 1. `WatermarkKey.Build`'s ambiguous string format

`src/DataSync.Core/../WatermarkKey.cs` (used from `RunExecutor`, `PreviewService`, `ResyncService`,
`JournalRecovery`):

```csharp
public static string Build(SourceTableRef source) =>
    $"{source.ConnectionName}/{source.Database}/{source.Schema}.{source.Table}";
```

The class's own doc comment already names the problem: a schema literally called `a.b` with table `c`
keys identically to schema `a` with table `b.c`. Left deliberately, because fixing the *format* by
itself would silently reinterpret every stored key on upgrade — a certain full resync of every
replication, against a collision that was, at the time, purely theoretical.

**Fix**: build the qualified name through the source's own `SqlDialect.QuoteIdentifier`/`QualifyTable`
(`src/DataSync.Core/Sql/SqlDialect.cs:35,47`) instead of raw interpolation. A quoted identifier can't
contain its own closing quote unescaped, so `"a.b".c` and `a."b.c"` are distinguishable strings — the
ambiguity is gone by construction, not by convention.

This changes the stored format for every existing row, which is the resync-on-upgrade cost the
original comment flagged — except issue 2 (below) is *also* changing the key's shape, so this is the
moment to accept that cost once, deliberately, rather than twice.

## 2. Two mappings, one source table, colliding watermarks

`ChangeWatermarks` is keyed on `(TaskName, SourceTable)` — the table, not the mapping. A task can have
any number of table mappings (one YAML file per mapping, `ConfigRepository.ListTableMappings`), and
nothing stops two of them from pointing at the same physical source table. Confirmed two distinct ways
that goes wrong today:

- **Different reader kinds.** One mapping reads the table via Change Tracking (numeric version
  watermark), another via the generic Watermark reader (arbitrary column value). Same key, incompatible
  formats — whichever ran last overwrites the other's watermark with a value the first reader can't
  parse.
- **Same reader, different options.** `WatermarkReader`'s `watermarkColumn` is a per-mapping option
  (`src/DataSync.Drivers.Generic/WatermarkReader.cs:34`) — two mappings using the *same* reader kind but
  different watermark columns still collide, silently, with no parse error to surface it. This is worse
  than the first case, not better: it doesn't crash, it just tracks the wrong position.

Note what's *not* a bug: two mappings sharing CDC or Change Tracking on the same table, with the same
options, sharing one stored position is actually correct — both are asking about the same physical
change stream, and the database-wide version/LSN has no per-mapping meaning to begin with. The problem
is specifically incompatible or divergent watermark *semantics* landing in the same row, not sharing in
general.

**Resolved**: key by mapping name, not reader kind. `ChangeWatermarks` gains a `MappingName` column,
included in the primary key alongside `TaskName` (source table folds into the value/audit trail rather
than the key, or stays — implementation phase decides based on what else reads it; see below). This
closes both cases above unconditionally, without needing to reason per-reader about which options affect
watermark semantics — a mapping's own identity already disambiguates anything about how it's configured.
The cost is that two mappings sharing CDC/Change Tracking on the same table no longer share one physical
row; their values converge to the same answer anyway (same version stream), so this is bookkeeping
duplication, not a correctness or meaningful storage cost.

This is also a breaking change to the stored key shape, same as issue 1 — one migration, one resync
cost, covering both.

## 3. A lightweight polling gate for CDC and Change Tracking

Today, every mapping is scheduled and dispatched purely on elapsed time
(`SchedulingEvaluator.IsDue`) — a source database with 30 CDC-tracked tables gets 30 independent
`WorkItem`s, 30 connections, 30 `sys.fn_cdc_get_max_lsn()` calls, every poll cycle, even when nothing in
the database changed. CDC's own reader already short-circuits *within* one table's read (skips the
`CHANGETABLE`/`cdc.fn_cdc_get_all_changes_...` scan if the stored LSN is caught up to the database-wide
max) — but that's after already committing to open a connection and dispatch a work item, and Change
Tracking doesn't even do that much (it fetches `CHANGE_TRACKING_CURRENT_VERSION()` every pass and always
proceeds to query `CHANGETABLE` regardless of whether the version moved).

Both engines expose a genuine database-wide, cheap, non-locking counter:

- Change Tracking: `CHANGE_TRACKING_CURRENT_VERSION()` — bumps on any write to any tracked table in the
  database.
- CDC: `sys.fn_cdc_get_max_lsn()` — bumps on any write to any captured table in the database.

**What to build**: a new state table, one row per `(ConnectionName, Database)` — *not* per task or
mapping, since the signal is database-wide and shared across every replication pointed at it — storing
the last-seen value and when it was checked. Before `SchedulerService.Tick` enqueues an otherwise-due
CDC or Change-Tracking mapping, it groups due mappings by their source `(ConnectionName, Database)`,
fetches the engine's counter once per distinct group, compares against the stored value, and skips
enqueuing that group's mappings entirely if unchanged. A changed (or first-seen) value updates the
stored row and lets the normal per-mapping dispatch proceed unmodified — this gate only decides whether
to *try*, never what a mapping reads once dispatched.

This is a real architectural shift worth stating plainly: `SchedulerService` today does no source-system
I/O at all — `Tick()` reads only local config and state. This gate makes it open live source connections
on a tick, for the databases with due CDC/Change-Tracking mappings. That's the intended trade (the whole
point is to ask the source directly, cheaply, before committing to per-table work), but it means the
scheduler's failure modes change: a source that's briefly unreachable now can slow or fail that
database's gate check on a tick, where today the scheduler never talks to sources at all. The gate should
fail open (skip the check, dispatch normally) on any error reaching the source, rather than fail closed —
an unreachable gate should degrade to today's behavior, not silently stop scheduling.

**Explicitly out of scope for this phase, decided**: trigger-audit readers. `TriggerAuditReader` has no
database-wide analogue to poll — it's one shadow table per source table
(`TriggerAuditPlan`/`TriggerAuditState`), not one shared table, and the reader's own documentation
already states the cost model this would collide with: "a trigger runs inside every transaction that
touches the table, forever." Adding a row every per-table trigger also writes to would create a single
shared lock/contention point across otherwise-independent tables' writers — precisely the problem being
asked to avoid. A softer alternative exists (each engine's own async, non-locking modification-stats
view — SQL Server's `sys.dm_db_index_usage_stats`, Postgres's `pg_stat_user_tables` — used only as a
"definitely nothing changed" heuristic that fails open, never authoritative), but it needs its own
per-engine research to confirm it's safe to trust even as a heuristic, and is left for a later, separate
phase.

## Sequencing

Issues 1 and 2 both change `WatermarkKey`'s shape and the `ChangeWatermarks` table — one phase, one
migration, one resync cost paid once. Issue 3 is an independent, additive feature (a new table, a new
gate in `SchedulerService`) that doesn't depend on the key-format fix and can ship separately.

**Next step**: ready for implementation phase docs — one for issues 1+2, one for issue 3.

---

# Outcome

Agreed, as `implementation/todo/phase-074-watermark-key-granularity.md` and
`implementation/todo/phase-075-lightweight-change-polling.md`.
