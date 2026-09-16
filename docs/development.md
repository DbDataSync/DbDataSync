# Building DbDataSync from source

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · [Getting started](getting-started.md) · [Drivers and libraries](drivers-and-libraries.md) · **Building from source**

This is for working on DbDataSync itself — contributing, running the dev loop, or building it rather
than installing the packaged CLI. If you just want to run DbDataSync, see [Install](install.md)
instead; nothing here is needed for that.

## Quick start: the dev harness

To get a working dev environment without following the manual steps below, one command does the lot —
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
`tools/DbDataSync.DevHarness`; see `architecture/implementation/done/phase-011-dev-harness.md`.

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
instances, not just two databases on one shared instance. Both use SA password `DbDataSync_Test_Pw1`
by default (override with the `DBDATASYNC_MSSQL_SA_PASSWORD` environment variable before first `up` —
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

The API spawns `DbDataSync.TaskRunner` as a child process per run and locates its build output
relative to its own — build the whole solution at least once (not just `src/DbDataSync.Api`) before
running the API.

## 3. Run the API

```sh
dotnet run --project src/DbDataSync.Api
```

Listens on `http://localhost:5183`. On first run it creates a local `dbdatasync-repo/` directory
(under `src/DbDataSync.Api/`, gitignored) as its git-backed config store and SQLite state database —
no separate setup step needed. Override the location via the `DbDataSync__RepoRoot` and
`DbDataSync__StateDbPath` environment variables (or `appsettings.Development.json`) if you'd rather
keep it elsewhere. This is one of several ways to start DbDataSync — see
[Configuration](configuration.md) for the rest (the `dbdatasync` CLI, the Windows service, the
container image) and everything each one can be configured with, including authentication.

Secrets (connection passwords) need an OS keychain in production; in a sandboxed/CI environment
without one, `SecretStore` falls back to environment variables named
`DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_<NAME>` (uppercased connection name) — set these before
triggering a run if you hit that fallback path locally.

## 4. Run the SPA

```sh
cd src/DbDataSync.Web
npm install
npm run dev
```

Open `http://localhost:5173`. It proxies `/api` and `/hubs` to the API (`http://localhost:5183` by
default — override with the `DBDATASYNC_API_URL` environment variable).

From here, [Getting started](getting-started.md) walks through the same UI regardless of how
DbDataSync was started.

## Running the tests

```sh
dotnet test --filter "Category!=Integration"   # fast, no external dependencies
dotnet test --filter "Category=Integration"    # needs the two containers from step 1
```

The Playwright SPA end-to-end suite (`tests/DbDataSync.Web.Tests/`) drives the same golden-path
scenario as [Getting started](getting-started.md) through a real browser, starting its own scratch
config repo, state database, and disposable SQL Server test database automatically:

```sh
cd tests/DbDataSync.Web.Tests
npm install
npx playwright install chromium
npx playwright test
```

Screenshots of each screen land in `screenshots/`, one sub-folder per spec (`screenshots/README.md`
indexes them) — including the ones embedded in [Getting started](getting-started.md).

## Repository layout

- `src/DbDataSync.Api` — ASP.NET Core orchestrator: REST API, scheduler, process supervisor, SignalR
  live-run hub.
- `src/DbDataSync.Web` — React/TypeScript SPA.
- `src/DbDataSync.Core` — config models, git-backed config store, secrets.
- `src/DbDataSync.State` — shared SQLite state store (runs, logs, watermarks, locks).
- `src/DbDataSync.Drivers.Abstractions` — the driver interfaces and capability discovery.
- `src/DbDataSync.Drivers.Generic` — the engine-neutral implementations every driver gets for free: a
  SQL dialect, a watermark reader, a batch-reload reader, a batched-insert staging provider and a
  delete/insert writer. A new engine's driver is a dialect, a connection factory and a catalog.
- `src/DbDataSync.Drivers.MsSql` — SQL Server, with its own faster implementations (Change Tracking
  reader, `SqlBulkCopy` staging, MERGE writers) alongside the generic ones.
- `src/DbDataSync.Drivers.Postgres` — PostgreSQL, on Npgsql. Registers only the generic pipeline;
  batch and watermark mode, no CDC.
- `src/DbDataSync.TaskRunner` — the console process actually spawned per replication run.
- `tools/DbDataSync.DevHarness` — the dev harness above (environment setup, workload generation,
  drift injection, source/target verification — including SQL Server → PostgreSQL, via
  `--target-engine postgres`). Not part of the shipped product.
- `tools/DbDataSync.Benchmarks` — `tools/benchmarks`, which measures how much the in-memory shape of a
  change batch costs, through a real `SqlBulkCopy` and through a typed sink. Also not shipped.
- `architecture/` — design docs; `architecture/implementation/` has a written summary of each build
  phase, including real bugs found and how they were fixed.
