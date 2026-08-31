# DataSync

Cross-database replication tool. Define a replication (source table → target table, column
mapping, schedule, change-processing settings) entirely through a web UI, and DataSync keeps the
target in sync — full initial load, then incremental change capture and apply on a schedule or on
demand.

v1 supports MSSQL → MSSQL. See `architecture/planning/done/overview.md` for the broader ambition and
`architecture/detailed-design.md` for the full system design.

## Quick start: the dev harness

To get a working environment without following the manual steps below, one command does the lot —
containers, databases, seed data, the API, the SPA, and a configured replication:

```sh
tools/dev-harness up          # tools\dev-harness up on Windows
```

Open `http://localhost:5173` and pick the `dev-sync` replication. Ctrl+C stops the API and SPA
(the containers keep running; `tools/dev-harness down` stops those).

In another terminal, put real traffic through it:

```sh
tools/dev-harness seed --rows 25000            # bulk-load the source
tools/dev-harness workload --rate 20 --duration 2m   # live inserts/updates/deletes
tools/dev-harness verify                       # compare source and target row by row
tools/dev-harness drift                        # corrupt the target behind the replication's back
```

`drift` is the quickest way to see why batch reload exists: it changes the *target* only, so Change
Tracking has nothing to report and no incremental run will ever repair it — `verify` keeps failing
until you trigger a backfill with a reconciling writer.

`tools/dev-harness help` lists every verb and option. The tool itself is
`tools/DataSync.DevHarness`; see `architecture/implementation/done/phase-011-dev-harness.md`.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 24+](https://nodejs.org/) and npm
- [Docker](https://www.docker.com/) (for local SQL Server instances)

## 1. Start SQL Server

```sh
docker compose up -d
```

This starts two independent SQL Server containers — `mssql-source` (`localhost,14330`) and
`mssql-target` (`localhost,14331`) — so a local replication genuinely crosses two database server
instances, not just two databases on one shared instance. Both use SA password `DataSync_Test_Pw1`
by default (override with the `DATASYNC_MSSQL_SA_PASSWORD` environment variable before first `up` —
the password is baked in at first container init, so changing it later requires
`docker compose down -v` to reset the data volumes).

Wait for both to report healthy:

```sh
docker compose ps
```

## 2. Build the solution

```sh
dotnet build
```

The API spawns `DataSync.TaskRunner` as a child process per run and locates its build output
relative to its own — build the whole solution at least once (not just `src/DataSync.Api`) before
running the API.

## 3. Run the API

```sh
dotnet run --project src/DataSync.Api
```

Listens on `http://localhost:5183`. On first run it creates a local `datasync-repo/` directory
(under `src/DataSync.Api/`, gitignored) as its git-backed config store and SQLite state database —
no separate setup step needed. Override the location via the `DataSync__RepoRoot` and
`DataSync__StateDbPath` environment variables (or `appsettings.Development.json`) if you'd rather
keep it elsewhere.

Secrets (connection passwords) need an OS keychain in production; in a sandboxed/CI environment
without one, `SecretStore` falls back to environment variables named
`CLRKERNEL_SECRET_DATASYNC_CONNECTION_<NAME>` (uppercased connection name) — set these before
triggering a run if you hit that fallback path locally.

## 4. Run the SPA

```sh
cd src/DataSync.Web
npm install
npm run dev
```

Open `http://localhost:5173`. It proxies `/api` and `/hubs` to the API (`http://localhost:5183` by
default — override with the `DATASYNC_API_URL` environment variable).

## 5. Define and run a replication

Entirely through the UI:

1. **Connections** — add a `SQL Auth` connection to each SQL Server instance (`localhost`, port
   `14330` for source / `14331` for target, user `sa`, the SA password from step 1).
2. **Replications → New Replication** — name it, leave the default `Continuous` schedule.
3. Open the replication → **Table Mappings → New Table Mapping** — pick the source
   connection/database/table and target connection/database/table via the cascading pickers;
   column mappings are auto-suggested for same-named columns.
4. **Runs → Run Now** — watch the live log tail and status update in real time.
5. **History** tab shows the git log of every config change DataSync auto-committed along the way.

## Backfilling a table

Incremental sync only ever applies what changed at the *source*. When a target has drifted for some
other reason — a bad deploy, an out-of-band edit, a mapping that was wrong for a while — the fix is a
backfill: **Runs → Backfill…**, which re-reads the source and makes the target match it.

A backfill is scoped to one table mapping and, optionally, to one segment of it — a list of values, a
range, "split this column's range into N buckets," or a **custom segmenting strategy**, where each
segment becomes its own independently queued run. It never advances the incremental watermark, so it
can be run against a live, scheduled replication without disturbing the ongoing sync.

A mapping states **how it divides for a reload** on its own editor, under *Default reload segmenting*.
That default is what the Backfill form opens pre-filled to, and what a scheduled `BatchReload` pass
processes. Leaving it empty means the whole table, unsegmented.

### Custom segmenting strategies

`Auto` splits a column's value range into evenly-sized buckets, which stops being enough as soon as the
boundaries have to mean something — a calendar month is not a fixed number of days. A replication can
define named strategies (**Overview → Settings**) that propose segments instead, authored four ways:

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

`label` names the segment in run history; `range_start`/`range_end` are half-open; `selected` decides
which candidates start ticked in the Backfill checklist, and — for a mapping whose default is a
strategy — which segments a *scheduled* pass reloads without anyone asking. A strategy flagging "the
last three months" is re-evaluated against today on every run, which is a relative-date ETL with no
extra scheduling concept behind it.

A strategy that queries a real database, bound as a mapping's default, runs on **every scheduled
pass**. The mapping editor says so beside the picker; whether that is acceptable is your judgement
about your tables, not something DataSync decides for you.

The reader/staging/writer pickers (here and in **Overview → Settings**) are populated from
`GET /api/connections/{name}/capabilities`, which reports what the connection's registered driver
actually supports — including which readers can be segmented and which writers *reconcile* (remove
target rows the source no longer has) rather than only insert and update.

A replication can also be a standalone reload rather than an incremental sync: set its reader to
`MsSqlBatchReload` and give each mapping a *Default reload segmenting* list, which it re-reads on its
normal schedule.

> **Breaking change.** The `segments` **reader option** — a JSON array hand-typed into the reader's
> settings — is no longer read. Segmenting now lives on the table mapping, where it has a real editor.
> A config still carrying that option behaves as though it had none (full table, unsegmented) until
> the mapping's *Default reload segmenting* is filled in. There is deliberately no automatic
> conversion: the two are not quite the same thing, and silently reinterpreting a stored reload scope
> is a worse failure than an obvious one.

## Where the state store lives

The state database — run history, the work queue, watermarks, users and sessions — is **SQLite by
default**, a file beside the config repo. Nothing needs configuring for that, and it is what an
unconfigured deployment gets.

It can instead run on SQL Server or PostgreSQL, for a deployment that would rather this lived on
infrastructure it already operates and backs up:

| setting (under `DataSync:`) | default | meaning |
| --- | --- | --- |
| `StateEngine` | `Sqlite` | `Sqlite`, `MsSql` or `Postgres` |
| `StateConnectionString` | — | how to reach that server. Required unless the engine is SQLite |
| `StateDbPath` | `<repo>/state.db` | the SQLite file. Ignored by the other two |

The schema is created on first open, whichever engine it is, and the three behave identically — the
cross-engine test suite exists to keep that true rather than to assert it once.

> **There is no migration between engines.** Pointing an existing deployment at a different one
> starts an empty state store; it does not move anything. Choose once, when the deployment is stood
> up. Moving an existing store is a deliberate follow-up.

An unrecognised `StateEngine` falls back to SQLite rather than refusing to start: a typo in an engine
name should not take down an API that has a perfectly good store already.

## Run history retention

Every pass writes a `TaskRuns` row and its log lines to the state database, so a continuous
replication of a busy table produces rows indefinitely. Two independent caps, both applied by an
hourly sweep inside the API process:

| setting (under `DataSync:`) | default | meaning |
| --- | --- | --- |
| `RunRetentionDays` | 90 | finished runs older than this are deleted |
| `RunRetentionMaxPerMapping` | 1000 | only the most recent N finished runs per table mapping are kept |
| `RunPruningIntervalMinutes` | 60 | how often the sweep runs |

A run failing *either* cap is pruned, along with its log lines. The count cap is per table mapping on
purpose: a global one would let one busy mapping evict a quiet mapping's entire history, which is
exactly the history somebody goes looking for when the quiet one finally breaks. **A run that has not
finished is never pruned**, whatever its age.

Set a cap to `0` to turn it off. Leaving it unset applies the default rather than meaning "keep
everything" — a state database that only grows is not a policy anyone chose.

> Verification results are **not** pruned by this. A verification run's `TaskRuns` row is, but its
> parquet result file and index entry are not — a known gap, not an oversight.

## Running the tests

```sh
dotnet test --filter "Category!=Integration"   # fast, no external dependencies
dotnet test --filter "Category=Integration"    # needs the two containers from step 1
```

The Playwright SPA end-to-end suite (`tests/DataSync.Web.Tests/`) drives the same golden-path
scenario as step 5 above through a real browser, starting its own scratch config repo, state
database, and disposable SQL Server test database automatically:

```sh
cd tests/DataSync.Web.Tests
npm install
npx playwright install chromium
npx playwright test
```

Screenshots of each screen land in `tests/DataSync.Web.Tests/screenshots/`.

## Repository layout

- `src/DataSync.Api` — ASP.NET Core orchestrator: REST API, scheduler, process supervisor, SignalR
  live-run hub.
- `src/DataSync.Web` — React/TypeScript SPA.
- `src/DataSync.Core` — config models, git-backed config store, secrets.
- `src/DataSync.State` — shared SQLite state store (runs, logs, watermarks, locks).
- `src/DataSync.Drivers.Abstractions` — the driver interfaces and capability discovery.
- `src/DataSync.Drivers.Generic` — the engine-neutral implementations every driver gets for free: a
  SQL dialect, a watermark reader, a batch-reload reader, a batched-insert staging provider and a
  delete/insert writer. A new engine's driver is a dialect, a connection factory and a catalog.
- `src/DataSync.Drivers.MsSql` — SQL Server, with its own faster implementations (Change Tracking
  reader, `SqlBulkCopy` staging, MERGE writers) alongside the generic ones.
- `src/DataSync.Drivers.Postgres` — PostgreSQL, on Npgsql. Registers only the generic pipeline;
  batch and watermark mode, no CDC.
- `src/DataSync.TaskRunner` — the console process actually spawned per replication run.
- `tools/DataSync.DevHarness` — the dev harness above (environment setup, workload generation,
  drift injection, source/target verification — including SQL Server → PostgreSQL, via
  `--target-engine postgres`). Not part of the shipped product.
- `tools/DataSync.Benchmarks` — `tools/benchmarks`, which measures how much the in-memory shape of a
  change batch costs, through a real `SqlBulkCopy` and through a typed sink. Also not shipped.
- `architecture/` — design docs; `architecture/implementation/` has a written summary of each build
  phase, including real bugs found and how they were fixed.
