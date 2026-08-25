# Phase 0 — Scaffolding

**Status**: Complete
**Plan reference**: `architecture/implementation-plan.md` § Phase 0

## What was built

A buildable, empty solution matching the project boundaries in `architecture/detailed-design.md`.

**Solution**: `DataSync.slnx` (dotnet 10's new XML solution format) at the repo root.

**Projects** (`net10.0`, nullable + implicit usings enabled):

| Project | Role | Key packages |
|---|---|---|
| `src/DataSync.Core` | Shared domain models; config read/write + git-backed persistence; secrets resolution | `YamlDotNet`, `LibGit2Sharp`, `ClrKernel.Core.Secrets` |
| `src/DataSync.Drivers.Abstractions` | `IDriver`/`IChangeReader`/`IStagingProvider`/`IChangeWriter` interfaces + driver registry | — |
| `src/DataSync.Drivers.MsSql` | MSSQL driver implementation | `Microsoft.Data.SqlClient` |
| `src/DataSync.State` | Central SQLite state store access | `Microsoft.Data.Sqlite` |
| `src/DataSync.TaskRunner` | Per-run console process | — |
| `src/DataSync.Api` | ASP.NET Core Web API (controllers), orchestrator/scheduler | `Microsoft.AspNetCore.OpenApi` |
| `src/DataSync.Web` | React + TypeScript SPA (Vite) | — |

Project references follow the dependency graph from `detailed-design.md`: `Drivers.Abstractions` →
`Core`; `Drivers.MsSql` → `Core` + `Drivers.Abstractions`; `State` → `Core`; `TaskRunner` → `Core` +
`Drivers.Abstractions` + `Drivers.MsSql` + `State`; `Api` → `Core` + `Drivers.Abstractions` +
`Drivers.MsSql` + `State`.

**Tests**: one xUnit project per `src/` project expected to carry logic (`DataSync.Core.Tests`,
`DataSync.State.Tests`, `DataSync.Drivers.MsSql.Tests`, `DataSync.TaskRunner.Tests`,
`DataSync.Api.Tests`), each referencing its corresponding `src/` project. `Drivers.Abstractions` (pure
interfaces) and `Web` (front-end, will get its own test tooling later) were not given test projects
in this phase.

**CI**: `.github/workflows/ci.yml` — two jobs, `dotnet` (`dotnet restore`/`build`/`test` against the
solution) and `web` (`npm ci`/`npm run build` in `src/DataSync.Web`), both on push/PR to `main`.

**Housekeeping**: removed the `webapi` template's sample `WeatherForecastController`/`WeatherForecast`
and `.http` scratch file, replaced with a trivial `HealthController` (`GET /api/Health` →
`{ "status": "ok" }`) so there's a real, buildable endpoint instead of unused sample code. Added a
root `.gitignore` covering `bin/`/`obj/`/`node_modules/`/`dist/`/IDE and OS cruft.

## Decisions made this phase

- **CI provider: GitHub Actions** (user confirmed) — the flagged open item from `implementation-plan.md`
  Phase 0 is resolved.
- **`DataSync.Api` uses controllers**, not minimal APIs (`dotnet new webapi --use-controllers`) — the
  API surface described in `detailed-design.md` §3.1 (connections, replications, table-mappings,
  metadata browsing, run history, manual trigger/cancel, plus a SignalR hub) is broad enough that
  controller-based organization should stay more maintainable than a flat minimal-API file as it
  grows through later phases.
- **Config read/write + git auto-commit logic lives in `DataSync.Core`**, not a separate project. Both
  `DataSync.Api` (writes on save) and `DataSync.TaskRunner` (reads a task's config at run start) need
  it, and it operates directly on the domain models that already live in `Core`, so a dedicated
  project would just be indirection without a second implementation ever using it differently.
- **Corrected package choice**: initially added `Microsoft.AspNetCore.SignalR.Client` to
  `DataSync.Api` by mistake — that's the *client* package for consuming a hub from a .NET app. Server-
  side hub hosting is already part of the ASP.NET Core shared framework via `Microsoft.NET.Sdk.Web`,
  so no SignalR package reference is needed on the API project itself; it was removed. The hub
  implementation itself is Phase 5 work, not part of this scaffolding.

## Verified

- `dotnet build` — solution builds with 0 warnings, 0 errors.
- `dotnet test` — all 5 test projects pass (1 placeholder passing test each, from the xUnit template;
  real tests land as each project gains logic in later phases).
- `npm run build` (in `src/DataSync.Web`) — SPA builds successfully via `tsc -b && vite build`.
- `.gitignore` confirmed to exclude all `bin/`, `obj/`, and `node_modules/` output (23 matched paths
  via `git status --ignored=matching`) before anything was staged.

## Notes / things to revisit later

- `DataSync.TaskRunner`'s `Program.cs` and each test project's `UnitTest1.cs` are still template
  boilerplate (`Console.WriteLine("Hello, World!")` / an empty passing `[Fact]`) — expected to be
  replaced with real content starting in Phase 1.
- No `Directory.Build.props` / central package version management was introduced; if package-version
  drift across projects becomes annoying, that's a reasonable small addition later, not required by
  the architecture.
