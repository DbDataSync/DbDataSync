# DataSync — Implementation Plan

Phased build plan derived from `architecture/detailed-design.md`. Phases are ordered so each one
produces something the next can build/test against; within a phase, items are listed in the order
they naturally get built, not strict dependency order.

## Phase 0 — Scaffolding

**Goal**: a buildable, empty solution with the project boundaries the architecture calls for.

- Solution layout:
  - `src/DataSync.Api` — ASP.NET Core Web API (orchestrator, scheduler, supervisor).
  - `src/DataSync.Web` — React + TypeScript SPA (Vite).
  - `src/DataSync.Core` — shared domain models (Connection, Replication/Task, Table Mapping, Column
    Mapping, Scheduling config types) used by both API and Task Runner.
  - `src/DataSync.Drivers.Abstractions` — `IDriver`, `IChangeReader`, `IStagingProvider`,
    `IChangeWriter`, driver registry.
  - `src/DataSync.Drivers.MsSql` — MSSQL driver implementation.
  - `src/DataSync.TaskRunner` — console app, per-run process.
  - `src/DataSync.State` — central SQLite access library.
  - `tests/` — one test project per `src/` project that has non-trivial logic.
- Basic CI: build + run tests on push (exact CI provider left to the user's preference — not
  prescribed by the architecture doc).
- **Exit criteria**: `dotnet build` succeeds across all projects; SPA scaffold runs `npm run dev` and
  loads a placeholder page; CI is green on an empty commit.

## Phase 1 — Config Model & Git-Backed Store

**Goal**: replication config can be defined as YAML, validated, written to disk, and auto-committed.

- Define YAML schemas in `DataSync.Core` for: `Connection`, `Replication`/`Task` (scheduling +
  change-processing settings), `TableMapping` (source/target tables, filters, column mappings), per
  the layout in `detailed-design.md` §3.6.
- Config read/write service: load a replication's full config from disk, validate it (including
  cross-checking selected reader/cache/writer against what the driver registry advertises, once
  Phase 3 exists — validation can initially just check shape), write changes back.
- LibGit2Sharp integration: on every config write, stage the changed file(s) and commit, authored as
  the acting user, with a generated summary message.
- Take a package dependency on `ClrKernel.Core.Secrets.SecretStore` (NuGet) in `DataSync.Core`. The
  `Connection` schema's credential field stores only a secret reference (key/identifier); on save,
  the API writes the actual credential value into the OS-native credential store via `SecretStore`
  and persists only the reference to YAML — connection YAML must never contain a plaintext
  credential field. `DataSync.TaskRunner` resolves the reference back to a value via the same
  `SecretStore` API at run time.
- **Exit criteria**: a test creates a connection + replication via the config service, confirms the
  YAML on disk matches expectations, confirms a git commit was created for each save with no
  plaintext secret present in any committed file, and confirms the credential value round-trips
  correctly through `SecretStore` (save via the API, resolve via a separate call as the Task Runner
  would).

## Phase 2 — Central SQLite State Store

**Goal**: `DataSync.State` provides the single, concurrency-safe access point for run/log/watermark
persistence used by both the API and every Task Runner process.

- Schema/migrations for `Tasks`, `TaskRuns`, `ChangeWatermarks`, `Logs`, `RunLocks` (per
  `detailed-design.md` §3.7). Use a lightweight migration approach (hand-rolled versioned SQL scripts
  applied on startup, or EF Core's Sqlite provider with migrations — pick whichever keeps
  `DataSync.State` dependency-light).
- Implement WAL mode + `busy_timeout` + retry-on-`SQLITE_BUSY` wrapper for all writes, and batched
  log-line flushing (buffer + periodic/size-triggered flush) so no caller writes one row per log
  line.
- **Exit criteria**: a concurrency test spins up several parallel writers (simulating API +
  multiple Task Runner processes) hammering `Logs`/`TaskRuns` simultaneously and confirms no
  unhandled `SQLITE_BUSY` errors and no lost writes.

## Phase 3 — Driver Abstraction + MSSQL Driver v1

**Goal**: the MSSQL driver can introspect metadata and move data end-to-end for a single table.

- Implement `IDriver`, `IChangeReader`, `IStagingProvider`, `IChangeWriter` in
  `DataSync.Drivers.Abstractions`, plus the driver registry.
- `DataSync.Drivers.MsSql`:
  - Metadata introspection (databases/tables/columns/types).
  - Change Tracking reader (build first — see `detailed-design.md` §3.5 for rationale).
  - Generic watermark reader (`WHERE x > y` against a configured watermark column) as the no-CT/CDC
    fallback.
  - CDC reader (added once Change Tracking path is proven).
  - Staging table writer via `SqlBulkCopy`.
  - `MERGE`-based writer from staging table to target; ordered insert/update/delete as fallback path.
- **Exit criteria**: an integration test (against a local SQL Server instance/container) runs a full
  reader→stage→write cycle for one table with Change Tracking enabled and confirms target rows match
  source after an insert, update, and delete on the source.

## Phase 4 — Task Runner Console Process

**Goal**: a standalone process that executes one full task run given a config path and run id.

- CLI entrypoint: `DataSync.TaskRunner --task-config <path> --run-id <guid>`.
- Pipeline wiring: load task config → resolve driver/reader/cache/writer from the registry → execute
  read→stage→apply→watermark-update sequence (`detailed-design.md` §3.3) → write final `TaskRuns`
  status.
- Structured logging to `DataSync.State`'s `Logs` table (batched per Phase 2), plus console output
  for local debugging.
- Exit code conventions: `0` success, non-zero distinguishing failure categories (config error,
  connectivity error, data error) so the Supervisor can log/react meaningfully without parsing stdout.
- **Exit criteria**: running the Task Runner by hand against a Phase-1-created config file and a
  Phase-3-provisioned MSSQL source/target performs a real replication run and produces correct
  `TaskRuns`/`ChangeWatermarks`/`Logs` rows.

## Phase 5 — Orchestrator / Scheduler in the Web API

**Goal**: the API can schedule, spawn, monitor, and report on Task Runner processes without manual
invocation.

- `IHostedService` scheduler evaluating each enabled task's schedule (continuous-loop with frequency,
  or cron-style periodic) on a tick.
- Process Supervisor: spawn via `System.Diagnostics.Process`, track PID/status in `TaskRuns`, enforce
  `RunLocks` to prevent overlapping runs, reconcile orphaned "Running" rows against live OS processes
  on API startup.
- REST endpoints: CRUD for connections/replications/table-mappings (wrapping the Phase 1 config
  service), metadata browsing (wrapping Phase 3 driver introspection), manual run trigger, run
  cancellation, run history/status/log retrieval.
- SignalR hub broadcasting run status transitions and new log lines to subscribed clients.
- **Exit criteria**: creating a replication via the API and either waiting for its schedule or hitting
  the manual-trigger endpoint results in a spawned Task Runner process, visible status transitions
  over the SignalR hub, and a correct final `TaskRuns` row — all without restarting the API.

## Phase 6 — React SPA

**Goal**: the full authoring + monitoring workflow is usable from the browser.

- Project setup: Vite + React + TypeScript, generated/typed API client against `DataSync.Api`.
- Connections management UI.
- Replication/Task Builder UI: source/target selection (via metadata endpoints), table mapping,
  column mapping/transforms, scheduling config, change-processing settings (reader/cache/writer
  choices constrained to what the selected driver advertises).
- Run Dashboard: task list, run history, live status + log tail (SignalR-backed).
- Config History view: read-only git log/diff view for a replication.
- **Exit criteria**: a user can, entirely through the UI, define a new MSSQL→MSSQL replication for a
  real table, trigger a run, and watch it complete with live log output — no direct file/API editing
  required.

## Phase 7 — End-to-End Validation

**Goal**: confirm the whole system works against real SQL Server instances, and is documented enough
for someone else to stand up.

- `docker-compose` with two SQL Server containers (source + target) for local dev/test.
- Manual end-to-end test: initial full load of a table, then incremental change capture/apply across
  several insert/update/delete cycles, confirmed via direct DB comparison.
- README / getting-started docs: how to run the API, SPA, and a Task Runner locally against the
  compose environment.
- Revisit `detailed-design.md` §8 open questions with real data: does central-SQLite contention show
  up under a realistic number of concurrently running tasks? Note findings; only act if it's actually
  a problem (fallback path is per-task SQLite files behind the same `DataSync.State` interface).
- **Exit criteria**: a fresh clone + documented setup steps produces a working local replication
  end-to-end, performed by someone other than the original implementer if possible.

## Backlog (explicitly future, not v1)

Not a phase — nothing here has been built, so unlike the numbered phases above (each of which
delivered and documented real work, including pure design/architecture passes), there's no
implementation doc for this section. Recorded so scope stays deliberate:

- **CDC reader** for MSSQL — deferred during Phase 3 (Change Tracking + the watermark fallback were
  enough to complete the v1 pipeline); see `architecture/implementation/done/phase-003-mssql-driver.md`'s
  Notes section for the original deferral rationale.
- **Batch reload** — a full or list/range-segmented reload/backfill of a table, distinct from the
  ongoing incremental `IChangeReader`s above (Change Tracking, the watermark fallback, and the
  deferred CDC reader all describe *incremental* sync). Fully designed (four review passes; see
  `architecture/implementation/done/phase-008-work-queue-schema.md`'s "Design history"); its foundation — the
  per-mapping `RunKind`/lock model and the durable work-queue-driven worker, needed so hundreds of
  queued mappings/backfills don't require one process per trigger — is built (Phase 8, the next
  numbered phase after this backlog list — this section was never itself a phase). The
  Its driver-level pieces — the segment type hierarchy, the segment-scoped batch-reload reader with
  Auto-segment expansion, the two reconciling writers, and capability discovery — are built (Phase 9).
  The Backfill trigger endpoint, the worker's `"segment"` options injection, standalone reload
  replications, and all SPA work are not yet built (Phase 10).
- Additional source/target database engine drivers (Postgres, MySQL, Oracle, etc.).
- Parquet (or other generic) staging provider.
- Alternate `DataSync.State` backends beyond SQLite.
- Auth/RBAC (flagged as an open question in `detailed-design.md` §8).
- Multi-node/remote Task Runner execution (flagged as out of scope in `detailed-design.md` §7).
- Near-real-time replication refinements (lower-latency continuous mode, dedup/ordering guarantees
  beyond what Change Tracking/CDC provide natively).
