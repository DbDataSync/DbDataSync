# Getting started

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · **Getting started** · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · [State database](state-database.md) · [Building from source](development.md)

This walks through setting up your first replication. If you haven't installed DbDataSync yet, see
[Install](install.md) first. Everything below happens in the web UI, once DbDataSync is running.

## Reading these docs in the console

Everything in this documentation is also in the web console: the book icon on the left rail (**Docs**) opens the same pages,
for the version of DbDataSync that is running — no network needed, so it works on a machine that cannot reach GitHub, and it
cannot describe a different release than the one you have. It is there for every signed-in user, not only administrators.

## Your first replication

1. **Connections.** Add a `SQL Auth` connection for each of your two SQL Server instances.

   ![New connection form](../screenshots/golden-path/02-connection-form.png)

2. **Replications → New Replication.** Name the replication and pick its source and target connection
   and database. Leave the schedule set to `Continuous` for now.

   ![Replication overview: source, target, and schedule](../screenshots/golden-path/04-replication-endpoints.png)

3. Open the replication, then go to **Table Mappings → New Table Mapping**. Pick the source and
   target table using the pickers. DbDataSync suggests column mappings automatically for columns with
   matching names.

   ![Table mapping with auto-mapped columns](../screenshots/golden-path/05-table-mapping-form.png)

4. **Runs → Run Now.** Watch the log and status update in real time.

   ![A live run in progress](../screenshots/golden-path/07-live-run-in-progress.png)

5. Open the **Version Control** tab to see the git log of every config change. DbDataSync commits
   automatically every time you save, so this log also works as an audit trail.

   ![Config history — every save is an auto-commit](../screenshots/golden-path/10-config-history.png)

## Bulk loading a table

Incremental sync only applies changes made at the source. If the target drifts for some other
reason — a bad deploy, a manual edit, a mapping that was wrong for a while — use a bulk load instead.
Go to **Runs → Bulk Load…**. It re-reads the source and makes the target match it.

![Queuing a bulk load](../screenshots/golden-path/11-bulk-load-form.png)

A bulk load applies to one table mapping, and optionally to just one segment of it: a list of values, a
range, an even split of a column's range into buckets, or a custom segmenting strategy (below). Each
segment runs as its own independently queued run. A bulk load never advances the incremental watermark,
so it can run against a live, scheduled replication without disturbing the ongoing sync. It shows up
in the same run history as the regular sync, tagged `BULKLOAD`.

![A bulk load run alongside the regular sync in run history](../screenshots/golden-path/13-run-history-with-bulk-load.png)

Each mapping has a *Default reload segmenting* setting, in its own editor, that states how it divides
for a reload. The Bulk Load form opens pre-filled with that default, and a scheduled `BatchReload` pass
uses it too. Leaving it empty means the whole table, unsegmented.

### Custom segmenting strategies

`Auto` splits a column's value range into evenly sized buckets. That works until the boundaries need
to mean something — a calendar month, say, which isn't a fixed number of days. For that, a replication
can define named strategies, under **Overview → Settings**, that propose segments a different way.
There are four ways to author one:

- **DuckDB SQL** — runs against an ephemeral in-memory DuckDB and touches neither database. Good for
  a table whose write pattern you already know:

  ```sql
  SELECT strftime(d, '%Y-%m')       AS label,
         d                          AS range_start,
         d + INTERVAL 1 MONTH       AS range_end,
         d >= current_date - INTERVAL 3 MONTH AS selected
  FROM generate_series(DATE '2020-01-01', current_date, INTERVAL 1 MONTH) AS t(d)
  ```

- **Source SQL** / **Target SQL** — the same four columns, queried from the live source or from a
  control table on the target.
- **C#** — a bound `ISegmentingStrategy`, handed both connections and the source's metadata.

`label` names the segment in run history. `range_start`/`range_end` are half-open. `selected` decides
which candidates start ticked in the Bulk Load checklist, and, for a mapping whose default is a
strategy, which segments a scheduled pass reloads on its own. A strategy such as "the last three
months" is re-evaluated against the current date every time it runs, so it needs no separate
scheduling logic.

If a mapping's default strategy queries a real database, that query runs on every scheduled pass. The
mapping editor notes this next to the picker. Whether that's acceptable depends on your tables, so
DbDataSync leaves that judgment to you.

The reader, staging, and writer pickers — here and under **Overview → Settings** — only list what the
connection's driver actually supports. This includes which readers can be segmented, and which writers
*reconcile*: removing target rows the source no longer has, rather than only inserting and updating.

A replication can also be a standalone reload instead of an incremental sync. Set its reader to
`MsSqlBatchReload` and give each mapping a *Default reload segmenting* list; it re-reads that list on
its normal schedule.

> **Breaking change.** The `segments` reader option — a JSON array typed directly into the reader's
> settings — is no longer read. Segmenting now lives on the table mapping, where it has a proper
> editor. A config that still has that option behaves as though it had none (full table, unsegmented)
> until you fill in the mapping's *Default reload segmenting*. There is no automatic conversion between
> the two.

## Next: Replication concepts

The walkthrough above used the defaults. See [Replication concepts](replication-concepts.md) for the
full picture — every reader kind, how a first-ever load actually runs, and delete reconciliation.
