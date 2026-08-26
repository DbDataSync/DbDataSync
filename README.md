# DataSync

Cross-database replication tool. Define a replication (source table → target table, column
mapping, schedule, change-processing settings) entirely through a web UI, and DataSync keeps the
target in sync — full initial load, then incremental change capture and apply on a schedule or on
demand.

v1 supports MSSQL → MSSQL. See `architecture/planning/overview.md` for the broader ambition and
`architecture/detailed-design.md` for the full system design.

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
- `architecture/` — design docs; `architecture/implementation/` has a written summary of each build
  phase, including real bugs found and how they were fixed.
