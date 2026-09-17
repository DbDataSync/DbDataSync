# Replication concepts

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · [Getting started](getting-started.md) · **Replication concepts** · [Drivers and libraries](drivers-and-libraries.md) · [State database](state-database.md) · [Building from source](development.md)

[Getting started](getting-started.md) walks through setting up a replication end to end. This page is
the reference for *how* it actually behaves once it's running: how DbDataSync detects changes at a
source, how an on-demand or first-ever reload works, how it notices a row was deleted when its ordinary
sync mechanism can't see deletes, and how it checks a target actually matches its source.

## The four kinds of run

Everything DbDataSync's worker does is one of four `RunKind`s, and they split across two independent
lanes so a long reload can never starve the latency-sensitive incremental sync of a worker slot:

| Kind | Lane | Does |
| --- | --- | --- |
| **Primary** | Change Processing | The ongoing incremental sync — one per table mapping, on its own schedule. The only kind that ever advances a mapping's watermark. |
| **BulkLoad** | Bulk Load | An on-demand or first-time full/segmented reload of one mapping — "Backfill" in the UI. Never advances the watermark. |
| **ReconcileDeletes** | Bulk Load | A cheap, key-only sweep that removes target rows whose key vanished from the source. Never inserts, updates, or advances the watermark. |
| **Verification** | Bulk Load | A source-vs-target comparison. Writes a report, never a row. |

The rest of this page covers each in turn.

## Change processing

A mapping's **Change Processing** pipeline is a reader, a staging provider, and a writer — configured at
the replication level, overridable per mapping. The reader is what actually detects changes at the
source; which reader you pick, and what it requires, is the single biggest decision behind how a
replication behaves.

### Reader kinds

| Kind | Mechanism | Detects deletes? | Setup | When to use |
| --- | --- | --- | --- | --- |
| `MsSqlChangeTracking` | SQL Server **Change Tracking** — the *net* change since a version, one row per key | Yes | Enable Change Tracking on the database and table (an operator's own DBA task — DbDataSync only ever queries it, never enables it) | The recommended default for SQL Server: simpler to enable than CDC, and its net-change semantics match what mirroring wants |
| `MsSqlCdc` | SQL Server **Change Data Capture** — every intermediate change harvested from the transaction log | Yes | Enable CDC capture on the table | Reach for this over Change Tracking specifically for **read consistency under concurrent writes** — Change Tracking's own reader joins live table data with its change list, and the two can move against each other under load; CDC's reader needs no such join. Not an "upgrade" to Change Tracking — a different trade |
| `TriggerAudit` | A trigger-maintained shadow audit table, engine-neutral | Yes | DbDataSync generates the trigger/table DDL; an operator previews and applies it | Any engine with no native change-tracking metadata — the one mechanism that reaches SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, and anything ODBC/JDBC-reachable identically. Costs every write to the table, forever — that trade is worth naming to an operator before they turn it on |
| `Watermark` | A monotonic column (an `updated_at`, a version) — reads rows where it exceeds the stored position | **No** | A monotonic watermark column on the table | Any engine, when the table has such a column and either never deletes rows or deletes are handled separately by [reconciliation](#reconciliation-delete-detection) below |
| `BatchReload` | A full (or segmented) table read — not incremental | N/A (a reload always converges) | None beyond read access | A standalone reload replication, or as the mechanism a [Bulk Load](#bulk-loading-backfill) actually runs under |
| `OracleFlashback` | Oracle **Flashback Version Query** — `VERSIONS BETWEEN SCN` reads a table's own committed row history directly. No shadow table, no trigger | Yes | None beyond ordinary `SELECT`, `FLASHBACK` privilege (only if the connection doesn't own the table), and `EXECUTE` on `DBMS_FLASHBACK` | Oracle sources, when the per-write trigger cost isn't wanted. Bounded by the source's undo retention — a replication paused longer than that needs a reload to catch back up, the same way any log-based mechanism's history can expire |
| `ScriptedQuery` | An operator-supplied SQL script | Depends on the script | Write the script | A change-tracking mechanism DbDataSync has no built-in driver for — Postgres logical replication slots, Oracle LogMiner — as an escape hatch |
| `DuckDbQuery` | An operator-written query against DuckDB's own scanners | No — not incremental | Write the query | A query-first source: Parquet, CSV, an S3 glob, an attached database — the query *is* the configuration |

PostgreSQL has no CDC-equivalent today — its options are `Watermark`, `TriggerAudit`, and `BatchReload`,
same as any generic engine reached through a [descriptor driver](drivers-and-libraries.md#descriptor-drivers).
MySQL/MariaDB's options are the same three — the binlog-based native alternative is still open work, see
`architecture/planning/todo/change-tracking-mysql.md`. Oracle adds `OracleFlashback` to that same set as
its own native option; LogMiner is still open work, see `architecture/planning/todo/change-tracking-oracle.md`.

**MySQL/MariaDB's `Watermark` reader has one real, unresolved gap worth knowing before combining it with
a per-pass row cap**: the bounded read that caps a `Primary` pass at N rows relies on the engine
supporting a tie-safe row limit (`WITH TIES` or equivalent), which MySQL and MariaDB have no working
form of. A capped pass that ends exactly on a tie can skip a sibling row sharing the boundary value until
a later pass happens not to land on that same tie — real, not theoretical, and not specific to any one
schema. Every other built-in driver's `Watermark` reader is tie-safe; MySQL/MariaDB's is the one
exception.

### Read intent

A mapping's next `Primary` pass resolves one of four **read intents** — what it's being asked to do,
distinct from what it's currently doing:

| Intent | Means |
| --- | --- |
| `InitialLoad` | Start from scratch. The default for a brand-new mapping — see [Bulk loading](#bulk-loading-backfill) for what this actually triggers. |
| `Changes` | Ordinary incremental read from the stored position. What every mapping settles into after its first successful pass. |
| `ChangesFromEarliest` | Replay everything the feed still retains, without a full table load. Not every reader can honor this — the `Watermark` reader can't, since for it the feed *is* the table and this would just be a full load under another name. |
| `ChangesFromLatest` | Skip to now — adopt the current position without reading any backlog. |

Set on a replication (with a per-mapping override) as **Default read intent**, in the same
inherit/override shape every other pipeline setting uses. Setting a mapping to either `Changes...` value
is how an operator asserts it must never full-load on its own — useful for a brand-new mapping that
should never see rows older than its feed.

A mapping's next pass can also be **held** from running at all, for a reason distinct from its intent:

| Hold | Means |
| --- | --- |
| `None` | Nothing is holding it back. |
| `PositionExpired` | The source discarded the change history a pass needed — reload to recover. |
| `Paused` | An operator stopped this one table specifically. |
| `Loading` | An initial load is running and nothing durable backs a position yet — see below. |

Holds show up on the Monitoring tab — a mapping stuck at `Loading` reads as "loading," not as silently
idle.

### Why some readers' first pass becomes a Bulk Load

An initial load is only correct if the change feed's position is captured **before** the table is read —
capture position, run the load, persist the position, switch to `Changes`. Get that ordering backwards
and every change made during a multi-hour load is lost silently: the load succeeds, the row counts look
right, and the rows in between are simply never seen again.

Readers that can report their current position **without reading a row** (`MsSqlChangeTracking`,
`Watermark`, `TriggerAudit`) do exactly that on a mapping's first-ever pass: capture the position, stash
it, set `ReadHold.Loading`, and hand the actual table read to the [Bulk Load](#bulk-loading-backfill)
pipeline as a normal `RunKind.BulkLoad`. Only once every segment of that load finishes does the captured
position become the mapping's live watermark and its intent flip to `Changes`. A reader with no honest
answer to "what's my position" (`BatchReload`, `DuckDbQuery`) has nothing to capture — its first pass
just is the reload, with no separate position step.

### Scheduling

A replication runs **Continuous** (a worker loops: one `Primary` pass per due mapping, then waits
`FrequencySeconds` before checking again) or **Periodic** (a cron expression instead of a fixed
interval). A continuous worker that finds nothing to do for `IdleTimeoutSeconds` (default 60) exits — an
idle replication costs nothing between passes, and the timeout exists so that emptying the queue for a
fraction of a second after every pass doesn't respawn the process several times a minute.

### Staging and writers

Staging moves a batch of changes into a form the writer can apply efficiently — `MsSqlStagingTable`
(a real SQL Server bulk copy) or the portable `StagingTable` (batched parameterized inserts, works
anywhere including ODBC/JDBC).

| Writer | Reconciles? | Does |
| --- | --- | --- |
| `MsSqlMerge` | No | A single `MERGE` — inserts, updates, applies explicit deletes the change set names. A target row the change set simply doesn't mention is left alone: exactly right for an incremental feed. |
| `Snapshot` | No — can't | Appends every staged row, timestamped. No comparison, no delete — for a target whose whole job is keeping what used to be there. |
| `Scd2` | No — deliberately not | The historized writer: keeps every version of every key rather than just the current one, via `DS_ValidFrom`/`DS_ValidTo`/`DS_IsCurrent`. Needs a reader that reports deletes to ever close a version for a deleted key — paired with one that doesn't, a deleted source row just stays current forever, which the writer picker says plainly. |
| `MsSqlMergeReconcile` / `MergeReconcile` | **Yes** | Inserts, updates, *and* deletes target rows in scope but absent from the change set — what lets a `BatchReload` converge. |
| `MsSqlDeleteInsert` / `DeleteInsert` | **Yes** | Delete everything in scope, insert the staged set. No primary key needed — nothing is joined row by row. The portable option: what makes `Watermark` mode useful even on an engine with no delete-aware change source, when paired with a segmented reload. |

`SupportsReconciliation` is what the reader/writer pickers use to show which writers reconcile —
covered in [Getting started](getting-started.md).

## Bulk loading (backfill)

**"Backfill" in the UI is `RunKind.BulkLoad` internally** — the same operation, one name change partway
through the project's history that never touched the docs. [Getting started](getting-started.md#backfilling-a-table)
already covers the operator-facing side — segments, custom segmenting strategies, the Monitoring card.
This section covers the mechanics behind it.

### What triggers one

Two paths, both funneling through the same enqueue logic:

1. **An operator's own backfill** (`Runs → Backfill…`) — an explicit segment list, submitted once.
2. **A mapping's own first-ever pass**, for a [position-capturing reader](#why-some-readers-first-pass-becomes-a-bulk-load) —
   segmented exactly like a scheduled reload of that mapping (its own *Default reload segmenting*, empty
   meaning the whole table), with no operator in the loop.

Two identical operator-triggered requests for the same segment collapse into one run. The auto-triggered
path is different on purpose: it never collapses into someone else's unrelated request for the same
segment — it fails cleanly and retries on the mapping's own next scheduled pass instead, so two genuinely
different reasons for wanting the same data never get silently merged.

A Bulk Load **never advances the incremental watermark**, regardless of which of the two paths started
it — it lives entirely on its own lane, so it can run for hours without disturbing an ongoing sync.

### Its own pipeline

Bulk Load has its own reader/staging/writer configuration, separate from Change Processing, with the
same replication-level-plus-per-mapping-override shape as every other pipeline setting:

- **Reader** defaults to `BatchReload` — every driver is expected to offer a whole-table-read Kind.
- **Staging/Writer** default to `null`, meaning *inherit whatever Change Processing uses* — a reload
  into the same target table naturally uses the same write mechanism unless told otherwise (typically an
  upsert-only writer is picked deliberately for a genuinely first load, since there's nothing yet to
  reconcile away).

A [descriptor driver](drivers-and-libraries.md#descriptor-drivers) can be a Bulk Load source too, subject
to the same provisioning caveat that page documents.

### Segmenting

| Segment | Means |
| --- | --- |
| `Full` | The whole table, unsegmented. |
| `List` | Rows matching one of a list of values in one column. |
| `Range` | Rows where a column falls in `[min, max)` — half-open, so consecutive ranges tile without gaps or overlaps. |
| `Auto` | Split a column's actual value range into N even buckets — resolved against the live source at reload time, never persisted as itself. |
| `Custom` | Run a named, authored segmenting strategy and use whichever candidates it flags as selected — re-evaluated against live data every time, never frozen as a stored list. |

A table mapping's own **Default reload segmenting** (empty meaning `Full`) is what a scheduled
`BatchReload` replication re-reads on its normal cadence, what an auto-triggered initial load segments
by, and what the Backfill form opens pre-filled with — an operator can still edit it per-request. Custom
segmenting strategies — DuckDB SQL, Source SQL, Target SQL, or a bound C# strategy — are fully documented
in [Getting started](getting-started.md#custom-segmenting-strategies).

### Progress and history

A batch is `Running` while any segment is still queued or in flight, `Completed` once every segment
succeeded, `CompletedWithFailures` once every segment finished and at least one didn't. The Monitoring
tab's live card shows rows copied against a one-time catalog-statistics estimate of the whole table (not
a live count — reads that estimate once, at enqueue) and a segment count; **Monitoring → Bulk Load
History** lists every past batch, filterable by mapping.

## Reconciliation (delete detection)

A mapping on `Watermark`, or any reader that can't see deletes, notices inserts and updates through its
ordinary `Primary` pass but **never notices a row was removed at the source** — it's simply never
selected again. The only fix without reconciliation is a full reload through a reconciling writer, which
converges the target but moves every column of every row just to catch a handful of absences.

**Reconciliation is a separate, narrower sweep for exactly that gap** — read only the source's primary
keys (cheap, a covering-index scan), and delete the target rows whose key is absent from that set, within
the segment's scope. It never inserts or updates, and never touches content drift; a mapping that also
needs to repair rows that changed behind the replication's back still wants an ordinary reconciling Bulk
Load. The two are complementary, not substitutes for each other.

### The mechanism

Always the same reader/staging pair — `KeyReconcile` reads only mapped key columns, staged the ordinary
way — paired with one of two writers depending on the mapping's own writer:

- **`KeyReconcileDelete`** — deletes target rows in scope whose key the source scan didn't produce.
- **`KeyReconcileScd2Close`** — the SCD2-safe version: instead of deleting, **closes** the open version
  (`DS_ValidTo`/`DS_IsCurrent`) of every key absent from the source, matched by the *natural* key rather
  than the surrogate one. Used automatically when the mapping's own writer is `Scd2`.

Before either commits, a **delete guard** checks how much of the segment it's about to remove:

| Guard | Behavior |
| --- | --- |
| `none` | No check — every computed delete applies. |
| `ratio` (default, max `0.5`) | Refuses (rolls back) if the fraction of the segment being deleted exceeds the limit. |

The guard exists because a key-diff sweep's failure mode is silent and total — a source pointed at the
wrong database, a filter that stopped matching, and a genuinely deleted table all look identical to "the
whole segment is gone" from the writer's side, and a committed delete has no second chance to notice. An
empty scope (nothing matched) always passes — that's not a runaway delete, it's nothing to sweep. An
on-demand sweep can override the configured guard for one run.

### Configuration and scheduling

Reconciliation is **opt-in**, per replication with a per-mapping override — a **Delete reconciliation**
toggle, off by default. Once enabled, it runs on its own schedule (the same Continuous/Periodic shape as
Change Processing, evaluated independently), an **after-change** trigger, or both:

| After-change strategy | Means |
| --- | --- |
| `none` (default) | Cadence only. |
| `any` | Any row read by a successful `Primary` pass since the last sweep is enough to trigger another one — still floored by the configured cadence. |

An operator can also trigger a sweep on demand (`Runs → Reconcile deletes…`), with the same
pre-filled-from-default segment picker the Backfill form uses — no reader/staging/writer choice to make,
since the pair is always fixed.

## Verification

A **Verification** run compares a mapping's source and target using one or more checks — `RowCount`,
`Sum`, a hand-written `Sql` check, or a script-generated pair of statements — grouped by declared
columns, with a difference threshold (a fraction of the larger side) so ordinary replication lag doesn't
paint everything red. For a historized target (`Scd2`/`Snapshot`), a check can compare against only the
target's *current* rows rather than its full history. It's operator-triggered only — nothing runs it on
a schedule today — and its result is written as a parquet file rather than as rows anywhere, indexed so
it can be found and paged back through the API.

## Next: Drivers and libraries

Which readers, writers, and staging providers are actually offered depends on the mapping's driver — see
[Drivers and libraries](drivers-and-libraries.md) for the built-in three and how to add another engine.
