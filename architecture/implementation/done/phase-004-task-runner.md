# Phase 4 — Task Runner Console Process

**Status**: Complete
**Plan reference**: `architecture/implementation-plan.md` § Phase 4

## What was built

**`src/DataSync.TaskRunner`**:
- `TaskRunnerOptions` — CLI arg parsing (`--repo-root`, `--state-db`, `--replication`, optional
  `--run-id`). `ConfigRoot` derives to `<repo-root>/config`, matching the convention
  `DataSync.Core.Config.ConfigPaths` already uses elsewhere.
- `RunExecutor` — the actual read → stage → apply → watermark-update pipeline
  (`architecture/detailed-design.md` §3.3), deliberately independent of CLI parsing/process exit
  codes so it's directly unit-testable. Given a replication name and run id, it: loads the task +
  all its table mappings via `ConfigRepository` (rejecting anything not 1:1 source:target — v1 only
  executes that shape, even though the config schema allows more); upserts the `Tasks` row;
  acquires a `RunLocks` row (own defense-in-depth against overlapping runs, even before Phase 5's
  Supervisor exists to do the same check before spawning); opens one connection per distinct
  (role, connection name) — source and target pools are always separate, even for a mapping that
  happens to reference the same named connection for both, per the Phase 3 finding about
  `SqlBulkCopy` deadlocking on a shared connection; resolves reader/staging/writer by `Kind` from
  the driver's advertised list; runs the pipeline per mapping; and records the outcome.
- `WatermarkKey` — the `ChangeWatermarks.SourceTable` key convention:
  `{connectionName}/{database}/{schema}.{table}`.
- `ExitCode` — `Success`, `ConfigError`, `ConnectivityError`, `DataError`, `AlreadyRunning`.
  Connectivity failures are distinguished from other data-processing failures via a dedicated
  internal `ConnectivityException` thrown only around the connection-open call — cleaner and more
  reliable than sniffing exception types after the fact.
- `Program.cs` — thin entrypoint: parse args, wire up `DriverRegistry` (MSSQL registered), a
  `SecretStore`, `ConfigRepository`, `StateDatabase`-backed stores, and a `LogWriter`; call
  `RunExecutor.ExecuteAsync`; return the exit code. Handles `Ctrl+C` via a `CancellationTokenSource`.

## How this was verified

**13 non-integration tests** (`DataSync.TaskRunner.Tests`, no SQL Server needed):
- `TaskRunnerOptionsTests` — arg parsing (valid, missing, bad GUID, unrecognized flag).
- `WatermarkKeyTests` — the key-building convention.
- `RunExecutorTests` — exercises `RunExecutor` against **real** `ConfigRepository` (temp git repo)
  and `DataSync.State` (temp SQLite file) infrastructure, stopping short of an actual database
  connection: replication not found → `ConfigError`; a 2-source table mapping → `ConfigError`
  (rejected before any connection is touched); a pre-held run lock → `AlreadyRunning`; and — the one
  test that does touch the network — a connection to `127.0.0.1:1` (`Connect Timeout=1`) → fails
  fast with `ConnectivityError`, confirming the lock is released and a `Failed` `TaskRuns` row with
  an error summary is recorded even on failure.

**1 end-to-end integration test** (`RunExecutorIntegrationTests`, tagged `Category=Integration`,
excluded from CI same as Phase 3): the single most valuable test from this phase — real git-backed
config, real SQLite state, the real MSSQL driver, and `RunExecutor` all wired together exactly as
`Program.cs` wires them, pointed at the live Docker SQL Server. Proves a full load followed by an
incremental insert+update+delete round trip replicates correctly end-to-end via the public
`RunExecutor` API.

**Manual "by hand" run of the compiled executable** (the plan's literal exit criteria — not just the
integration test calling the same class in-process): seeded a fresh git repo's config via
`ConfigRepository` from a throwaway script, created real source/target tables on the Docker SQL
Server, supplied the connection credential via `SecretStore`'s environment-variable fallback
(`CLRKERNEL_SECRET_DATASYNC_CONNECTION_<NAME>` — the in-memory store used for automated tests
doesn't cross a process boundary, so this exercised the *other* real resolution path), and ran
`dotnet run --project src/DataSync.TaskRunner -- --repo-root ... --state-db ... --replication
manual-sync`. Exit code `0`; confirmed directly against the database and state file:

```
TgtOrders: (1, Alice), (2, Bob)                      -- matches source exactly
TaskRuns:  manual-sync | Succeeded | RowsRead=2 | RowsWritten=2
ChangeWatermarks: manual-sync | manual-src/ManualRunDb/dbo.SrcOrders | <cursor>
Logs: "Run started...", "Reading changes for 'orders'...", "'orders': 2 row(s) read, 2 row(s)
       written.", "Run succeeded: 2 row(s) read, 2 row(s) written."
```

## Decisions made this phase

- **`RunExecutor` acquires its own `RunLocks` entry**, not just relying on Phase 5's future
  Supervisor to check before spawning. Cheap (the store already exists), and makes the Task Runner
  correctly safe against overlapping runs even when invoked manually/out-of-band — not redundant
  complexity, belt-and-suspenders around a real correctness property.
- **Connectivity vs. data errors are distinguished via a dedicated internal exception**
  (`ConnectivityException`), thrown only in the narrow window around `connection.OpenAsync`, rather
  than pattern-matching on caught exception types after the fact (fragile, and would have needed
  driver-specific knowledge like `SqlException` in a class that's meant to stay driver-agnostic).
- **One connection per (role, connection name), pooled and reused across table mappings within a
  run** — avoids re-opening a connection per mapping when multiple mappings share a source/target,
  while keeping source and target pools structurally separate so the Phase 3 same-connection
  deadlock can't reappear even if a mapping's source and target happen to name the same connection.
- **CLI args use `--repo-root` + `--replication`** (resolved through `ConfigRepository`'s name-based
  API), not the `--task-config <path>` sketched in `implementation-plan.md`. Once `ConfigRepository`
  existed (Phase 1) as the correct single read path for config, pointing at a raw file path instead
  would have meant either bypassing it or re-deriving the path convention a second time — the
  name-based form was the more consistent implementation of the same intent.

## Notes / things to revisit later

- `RunExecutor` currently loads *every* table mapping for a replication and runs them serially in one
  process invocation. `ChangeProcessing.Reader.Parallelism`/`Writer.Parallelism` from the config
  schema (Phase 1) are not yet used — parallel execution across mappings, or within one mapping's
  read/write, is unstarted work for a later phase if throughput becomes a real requirement.
- Manual cancellation (`Ctrl+C`) is wired to a `CancellationToken` threaded through the pipeline, but
  wasn't explicitly tested this phase (would require interrupting a real in-flight run against a
  large enough change set to observe).
- Nothing in `DataSync.Api` spawns `DataSync.TaskRunner` yet — that orchestration (scheduler, process
  supervision, REST endpoints) is Phase 5.
