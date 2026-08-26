# DataSync

Cross-database replication tool. Define a replication (source table → target table, column
mapping, schedule, change-processing settings) entirely through a web UI, and DataSync keeps the
target in sync — full initial load, then incremental change capture and apply on a schedule or on
demand.

v1 supports MSSQL → MSSQL. See `architecture/planning/overview.md` for the broader ambition and
`architecture/detailed-design.md` for the full system design.

## Quick start: the dev harness

To get a working environment without following the manual steps below, one command does the lot —
containers, databases, seed data, the API, the SPA, and a configured replication:

```sh
scripts/dev-harness up          # scripts\dev-harness up on Windows
```

Open `http://localhost:5173` and pick the `dev-sync` replication. Ctrl+C stops the API and SPA
(the containers keep running; `scripts/dev-harness down` stops those).

In another terminal, put real traffic through it:

```sh
scripts/dev-harness seed --rows 25000            # bulk-load the source
scripts/dev-harness workload --rate 20 --duration 2m   # live inserts/updates/deletes
scripts/dev-harness verify                       # compare source and target row by row
scripts/dev-harness drift                        # corrupt the target behind the replication's back
```

`drift` is the quickest way to see why batch reload exists: it changes the *target* only, so Change
Tracking has nothing to report and no incremental run will ever repair it — `verify` keeps failing
until you trigger a backfill with a reconciling writer.

`scripts/dev-harness help` lists every verb and option. The tool itself is
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
range, or "split this column's range into N buckets," where each bucket becomes its own independently
queued run. It never advances the incremental watermark, so it can be run against a live, scheduled
replication without disturbing the ongoing sync.

The reader/staging/writer pickers (here and in **Overview → Settings**) are populated from
`GET /api/connections/{name}/capabilities`, which reports what the connection's registered driver
actually supports — including which readers can be segmented and which writers *reconcile* (remove
target rows the source no longer has) rather than only insert and update.

A replication can also be a standalone reload rather than an incremental sync: set its reader to
`MsSqlBatchReload` and give it a `segments` reader option (a JSON array of segment descriptors, edited
in **Overview → Settings**) to re-read those segments on its normal schedule.

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
- `src/DataSync.Drivers.Abstractions` / `src/DataSync.Drivers.MsSql` — the driver interfaces and the
  v1 MSSQL implementation (Change Tracking reader, staging-table cache, merge writer).
- `src/DataSync.TaskRunner` — the console process actually spawned per replication run.
- `tools/DataSync.DevHarness` — the dev harness above (environment setup, workload generation,
  drift injection, source/target verification). Not part of the shipped product.
- `architecture/` — design docs; `architecture/implementation/` has a written summary of each build
  phase, including real bugs found and how they were fixed.
