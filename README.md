# DbDataSync

[![NuGet](https://img.shields.io/nuget/v/DbDataSync.svg?label=NuGet)](https://www.nuget.org/packages/DbDataSync)

DbDataSync is a cross-database replication tool. You define a replication in a web UI — the source
table, the target table, column mappings, a schedule, and how changes are processed — and DbDataSync
keeps the target in sync. It does a full initial load, then applies incremental changes on a schedule
or on demand.

v1 supports MSSQL → MSSQL. See `architecture/planning/done/overview.md` for the broader ambition and
`architecture/detailed-design.md` for the full system design.

## Install

```sh
dotnet tool install -g DbDataSync
dbdatasync setup
```

`setup` walks you through picking a config folder, adding connections, and setting up secrets. When
you're done, it starts DbDataSync for you. This needs the
[.NET 10 runtime](https://dotnet.microsoft.com/download) or SDK.

The command above installs DbDataSync into your own user profile. That's fine for trying it out, but
not for running it as a service or for a deployment more than one person uses. For a Windows or
systemd service, a machine-wide install, or running in a container, see
[docs/install.md](docs/install.md). [CONFIG.md](CONFIG.md) lists every flag and environment variable.
The package is also on [nuget.org](https://www.nuget.org/packages/DbDataSync).

Once DbDataSync is running, open the URL it prints (`http://localhost:5080` by default) and continue
with the walkthrough below. The UI is the same no matter how you started it.

## Your first replication

Everything below happens in the UI.

1. **Connections.** Add a `SQL Auth` connection for each of your two SQL Server instances.

   ![New connection form](https://raw.githubusercontent.com/DbDataSync/DbDataSync/main/screenshots/golden-path/02-connection-form.png)

2. **Replications → New Replication.** Name the replication and pick its source and target connection
   and database. Leave the schedule set to `Continuous` for now.

   ![Replication overview: source, target, and schedule](https://raw.githubusercontent.com/DbDataSync/DbDataSync/main/screenshots/golden-path/04-replication-endpoints.png)

3. Open the replication, then go to **Table Mappings → New Table Mapping**. Pick the source and
   target table using the pickers. DbDataSync suggests column mappings automatically for columns with
   matching names.

   ![Table mapping with auto-mapped columns](https://raw.githubusercontent.com/DbDataSync/DbDataSync/main/screenshots/golden-path/05-table-mapping-form.png)

4. **Runs → Run Now.** Watch the log and status update in real time.

   ![A live run in progress](https://raw.githubusercontent.com/DbDataSync/DbDataSync/main/screenshots/golden-path/07-live-run-in-progress.png)

5. Open the **Version Control** tab to see the git log of every config change. DbDataSync commits
   automatically every time you save, so this log also works as an audit trail.

   ![Config history — every save is an auto-commit](https://raw.githubusercontent.com/DbDataSync/DbDataSync/main/screenshots/golden-path/10-config-history.png)

## Backfilling a table

Incremental sync only applies changes made at the source. If the target drifts for some other
reason — a bad deploy, a manual edit, a mapping that was wrong for a while — use a backfill instead.
Go to **Runs → Backfill…**. It re-reads the source and makes the target match it.

![Queuing a backfill](https://raw.githubusercontent.com/DbDataSync/DbDataSync/main/screenshots/golden-path/11-backfill-form.png)

A backfill applies to one table mapping, and optionally to just one segment of it: a list of values, a
range, an even split of a column's range into buckets, or a custom segmenting strategy (below). Each
segment runs as its own independently queued run. A backfill never advances the incremental watermark,
so it can run against a live, scheduled replication without disturbing the ongoing sync. It shows up
in the same run history as the regular sync, tagged `BACKFILL`.

![A backfill run alongside the regular sync in run history](https://raw.githubusercontent.com/DbDataSync/DbDataSync/main/screenshots/golden-path/13-run-history-with-backfill.png)

Each mapping has a *Default reload segmenting* setting, in its own editor, that states how it divides
for a reload. The Backfill form opens pre-filled with that default, and a scheduled `BatchReload` pass
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
which candidates start ticked in the Backfill checklist, and, for a mapping whose default is a
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

## Where the state store lives

The state database holds run history, the work queue, watermarks, users, and sessions. It's **SQLite
by default**, a file next to the config repo — you don't need to configure anything for this.

It can also run on SQL Server or PostgreSQL, if you'd rather it lived on infrastructure you already
operate and back up:

| setting (under `DbDataSync:`) | default | meaning |
| --- | --- | --- |
| `StateEngine` | `Sqlite` | `Sqlite`, `MsSql` or `Postgres` |
| `StateConnectionString` | — | how to reach that server. Required unless the engine is SQLite |
| `StateDbPath` | `<repo>/state.db` | the SQLite file. Ignored by the other two |

The schema is created automatically the first time the store is opened, on any of the three engines,
and all three behave the same way.

> **There is no migration between engines.** If you point an existing deployment at a different
> engine, it starts with an empty state store — nothing is moved over. Choose the engine when you
> first set up the deployment.

If `StateEngine` is set to a value DbDataSync doesn't recognize, it falls back to SQLite instead of
refusing to start.

## Run history retention

Every pass writes a `TaskRuns` row and its log lines to the state database, so a continuous
replication of a busy table produces rows indefinitely. Two independent caps, both applied by an
hourly sweep inside the API process, keep that in check:

| setting (under `DbDataSync:`) | default | meaning |
| --- | --- | --- |
| `RunRetentionDays` | 90 | finished runs older than this are deleted |
| `RunRetentionMaxPerMapping` | 1000 | only the most recent N finished runs per table mapping are kept |
| `RunPruningIntervalMinutes` | 60 | how often the sweep runs |

A run that exceeds either cap is pruned, along with its log lines. The count cap applies separately to
each table mapping, so a busy mapping can't push a quiet mapping's history out. **A run that hasn't
finished is never pruned**, regardless of its age.

Set a cap to `0` to turn it off. If you leave a cap unset, DbDataSync uses its default — that does not
mean "keep everything."

> Verification results are not pruned by this process. A verification run's `TaskRuns` row is deleted,
> but its parquet result file and index entry are kept.

## Building from source

To build DbDataSync from source, run the dev harness, or contribute, see
[docs/development.md](docs/development.md). It covers the dev loop, the test suite, and the
repository layout.
