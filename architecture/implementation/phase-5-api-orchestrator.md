# Phase 5 — Orchestrator / Scheduler in the Web API

**Status**: Complete
**Plan reference**: `architecture/implementation-plan.md` § Phase 5

## What was built

**`src/DataSync.Api`**, now a real orchestrator, not just a scaffold:

- **`ApiOptions`** — `RepoRoot`/`StateDbPath`/`TaskRunnerDllPath` from the `DataSync` config section,
  with dev-friendly defaults (a local `./datasync-repo`, auto-`git init`'d at startup; `TaskRunnerDllPath`
  guessed from this project's own build output path — see Decisions).
- **`ProcessSupervisor`** — spawns `DataSync.TaskRunner` as a genuine child process via `dotnet exec
  <TaskRunnerDllPath> --repo-root ... --state-db ... --replication ... --run-id ...` (the same `dotnet`
  host running the API), tracks active runs, cancels via `Process.Kill(entireProcessTree: true)`
  (writing the terminal `TaskRuns` row itself since a kill skips `RunExecutor`'s own completion path),
  and reconciles `TaskRuns` rows left `Running` by a previous API process on startup.
- **`SchedulerService`** (`BackgroundService`, 5s tick) + **`SchedulingEvaluator`** (pure, separated
  out for direct unit testing) — evaluates every enabled replication's `Continuous`/`Periodic`
  schedule against its last run's start time (via `Cronos` for cron parsing) and triggers due ones.
- **`RunMonitorService`** (`BackgroundService`, 1s tick) — polls `Logs`/`TaskRuns` for every run
  `ProcessSupervisor` is tracking and republishes over **`RunHub`** (SignalR, `/hubs/run`): `logLine`
  for new log rows, `runCompleted` once the child process exits. Also runs `ReconcileOrphanedRuns`
  once at startup.
- **`MetadataService`** — opens a connection, runs one `IDriver` introspection call, closes it.
- **Controllers**: `ConnectionsController`, `ReplicationsController`, `TableMappingsController`
  (CRUD, wrapping `ConfigRepository` — including two new methods it was missing, see Decisions),
  `MetadataController` (databases/tables/columns browsing), `RunsController` (trigger, cancel,
  history, single-run detail, logs).
- Config writes are attributed to a fixed `GitAuthor("DataSync API", "datasync@localhost")` — no real
  auth yet (`detailed-design.md` §8, still open).

## How this was verified

**Manually first, then automated** — given how many moving parts (process spawning, a scheduler, two
background pollers, SignalR) had to work together correctly, I ran the real compiled API by hand
against the live Docker SQL Server before writing any test for it, driving it entirely through HTTP:
created connections, browsed real metadata, created a replication + table mapping, triggered a run,
polled it to `Succeeded` with correct row counts, confirmed the target table, and confirmed every
config write produced a real git commit with no plaintext secret in it. Then connected a real SignalR
client mid-session and watched `logLine`/`runCompleted` events arrive live. **This manual pass caught
three real bugs** before they became test flakiness — see below.

**97 tests across the solution** after this phase (up from 69): **23 in `DataSync.Api.Tests`** — 7
pure `SchedulingEvaluator` unit tests (continuous/periodic due-ness, including real `Cronos`
evaluation), 16 controller tests via a real `WebApplicationFactory<Program>` host
(`TestApiFactory`, in-memory secrets, temp repo) covering CRUD round-trips, the "no plaintext secret
anywhere" property (response body *and* the actual git blob), and 404/204 semantics — plus **4
integration tests** (`Category=Integration`): metadata browsing against real SQL Server, and — the
one that automates the manual pass — `RunLifecycleIntegrationTests`, which creates real config via
HTTP, triggers a run, joins the SignalR group, and asserts both the live `logLine`/`runCompleted`
events *and* the final `TaskRuns` row, in one test.

## Bugs found via manual testing (fixed before any test was written around them)

1. **Enums serialized as numbers, not names.** `System.Text.Json` defaults to numeric enum
   serialization; a client sending `"driverType": "MsSql"` got a binding error. Fixed by registering
   `JsonStringEnumConverter()` globally.
2. **Config options read too early.** `ApiOptions` was originally computed as a plain local variable
   from `builder.Configuration` *before* `builder.Build()`. `WebApplicationFactory`'s test
   configuration overrides (`ConfigureWebHost` → `ConfigureAppConfiguration`) are layered on as part
   of `Build()` — so the eager read silently used pre-override values. Every controller test would
   have "worked" against the wrong (real, default) repo location without ever noticing, since PUT/GET
   round-trips are self-consistent regardless of which directory they land in — only a test that
   independently inspects the filesystem (checking git blobs for the no-plaintext-secret property)
   caught it. Fixed by registering `ApiOptions` in DI (`AddSingleton(sp => ApiOptions.FromConfiguration(...))`)
   and resolving it from `app.Services` *after* `Build()` for the startup `EnsureRepo` call — the
   general lesson (config must be resolved from DI, not read eagerly, for
   `WebApplicationFactory`-testability) applies to any future options type added here.
3. **`TaskRunnerDllPath`'s default resolution assumed `DataSync.Api` is the entry assembly.** True
   when running the API directly (`AppContext.BaseDirectory` contains `.../DataSync.Api/bin/...`),
   false when the API is loaded inside `DataSync.Api.Tests`'s host (`AppContext.BaseDirectory` is the
   *test* project's own output directory, which never contains that path segment). `TestApiFactory`
   now computes the path independently — walking up from its own build output to find `DataSync.slnx`
   (the repo root), then reconstructing `TaskRunner`'s build output path from its own
   Configuration/TFM segments — rather than relying on `ApiOptions`'s assembly-relative guess.

None of these would have been caught by unit tests alone; catching them required actually running the
process and, in case 3, actually spawning the real child process from inside a test host.

## Decisions made this phase

- **`ConfigRepository` gained `DeleteReplicationTask` and `DeleteTableMapping`** — a genuine Phase 1
  gap (only `DeleteConnection` existed). The REST API needs full CRUD, so these were added with tests
  in `DataSync.Core.Tests` rather than working around the gap in the API layer.
- **Cancellation is `Process.Kill(entireProcessTree: true)`, not a cooperative signal file.**
  `detailed-design.md` §3.1 explicitly allows this as a fallback; a cooperative mechanism (TaskRunner
  watching for a cancellation request) is real follow-up work, not implemented this phase. A run
  killed mid-flight can't corrupt target data — the writer's `MERGE` is one atomic statement, so a
  kill either lands before or after it commits, never mid-way.
- **`dotnet exec <dll>` to spawn TaskRunner**, not the platform-specific apphost binary — works
  identically regardless of how TaskRunner was published, using the same `dotnet` runtime already
  running the API.
- **Secrets and process spawning**: confirmed empirically (this sandbox has no OS keychain — `SecretStore`
  degrades to memory+env) that a spawned child process inherits the parent's environment variables,
  meaning `SecretStore`'s env-var fallback is a real, working production option for containerized
  deployments with no OS keychain (e.g. Docker/Kubernetes with secrets injected as env vars), not just
  a test workaround. Documented here since it's a real deployment-mode discovery, not just a testing note.

## Notes / things to revisit later

- `ProcessSupervisor.CancelRun` has a known, accepted rare race: if a run finishes naturally at almost
  the same instant a cancel request arrives, its real terminal status could be overwritten with
  `Cancelled`. Not addressed — low probability, low consequence (a `TaskRuns` row's status is wrong
  but data was correctly written either way).
- A run that's still genuinely alive after an API restart (i.e., not orphaned) keeps running to
  completion and writes its own correct final `TaskRuns` row, but isn't monitorable/cancellable via
  the *new* API process instance until it exits — `ProcessSupervisor` only tracks processes it itself
  spawned. Reasonable for v1; a more complete solution would need to reattach to the OS process by PID.
- Nothing in `DataSync.Web` (the SPA) talks to any of this yet — that's Phase 6.
