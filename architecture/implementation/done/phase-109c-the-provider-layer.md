# Phase 109c — the provider layer

**Status**: Done.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*The provider layer*.
Depended on 109a. Independent of 109b. Nothing in the core *uses* the provider layer after this phase
— it only proves the layer works.

## What this built

A way to restore an ADO.NET provider package DbDataSync does not reference, load it at runtime, and
hand any consumer a `DbProviderFactory` for it. No consumer is switched over here — the assertion this
phase makes is that the mechanism works end to end, proven against `MySqlConnector`, a package no
`.csproj` in the solution references.

### `src/DbDataSync.Providers/` — a new, dependency-free project

Deliberately at the bottom of the graph: its `.csproj` has no `PackageReference` and no
`ProjectReference` at all, so any project — `Api`, `TaskRunner`, `Cli` — can take it without pulling in
anything provider-specific, and it never becomes the reason a future compiled-driver plugin (109e) drags
in something heavy.

- **`ProviderManifest.cs`** — `record ProviderManifest(string Id, string FactoryType, IReadOnlyList<ProviderPackageRef> Packages)`
  and `record ProviderPackageRef(string Id, string Version)`, serialised to/from
  `<repo>/providers/<id>/provider.json` via `System.Text.Json` with a camelCase naming policy on both
  read and write (so the same `JsonSerializerOptions` round-trips either direction without a
  case-insensitive escape hatch).
- **`ProviderPaths.cs`** — the one place the on-disk layout (`providers/<id>/provider.json`,
  `providers/<id>/lib/`) is stated, shared by the installer, the registry and the CLI.
- **`ProviderInstaller.cs`** — `InstallAsync`/`SyncAsync`. Writes a throwaway SDK-style class-library
  `.csproj` referencing every package in the manifest, with an explicit `<RuntimeIdentifier>` (the
  current machine's, via `RuntimeInformation.RuntimeIdentifier`) and `<SelfContained>false</SelfContained>` —
  a portable, RID-less `dotnet publish` does **not** copy `runtimes/<rid>/native/` assets into the flat
  output, which would have silently stranded exactly the packages (SqlClient's SNI shim, DuckDB's libs)
  this mechanism exists to carry. `dotnet publish -o <dir>` (not `restore`) is what produces one flat
  directory with the full managed closure, `.deps.json`, and native assets together — a `dotnet restore`
  leaves them scattered across the NuGet cache by design. The publish output is copied verbatim into
  `providers/<id>/lib/`, replacing any previous contents (a version downgrade must not leave a stale DLL
  from the old version for the loader to prefer).
- **`ProviderRegistry.cs`** — `LoadAll()` enumerates `providers/*/provider.json`, and for each one:
  arms a shared `AssemblyLoadContext.Default.Resolving`/`.ResolvingUnmanagedDll` handler pair (registered
  once per process; a second `ProviderRegistry` instance — a test standing up its own repo root — adds
  to the shared resolver list rather than double-subscribing) backed by an
  `AssemblyDependencyResolver` over that provider's `.deps.json`, then calls
  `DbProviderFactories.RegisterFactory(id, factoryType)` — the **string overload**, so the assembly only
  has to be *loadable* (findable by the resolver on first `Type.GetType`), never referenced.
  `GetFactory(id)` wraps `DbProviderFactories.GetFactory` with a message naming
  `dbdatasync provider install <id>` instead of the BCL's generic failure.
- **`KnownProviderFactories.cs`** — a small starter table (`MySqlConnector`, `Microsoft.Data.SqlClient`,
  `Npgsql`, `Microsoft.Data.Sqlite`, Oracle, ODBC, Firebird) so `provider install <packageId>` doesn't
  need `--factory-type` for the common case; editable after install since `provider.json` is a plain
  file, and required explicitly for anything not in the table rather than guessed wrong.

### `dbdatasync provider …` — `src/DbDataSync.Cli/ProviderCommand.cs`

Dispatched from `Program.cs` alongside `secret`/`cert`, same nested `args[0]` sub-switch:
`install <packageId>[ <packageId>…] [--as <id>] --version <v> [--factory-type type] [--source feed]`,
`sync [<id>]` (all installed providers when no id given), `list` (id, packages, factory type, whether it
currently resolves — a real `GetFactory` call, not just "the manifest exists"), `uninstall <id>`.
Repo root resolved through the existing `DbDataSyncRoot.Resolve(args)` — the same `--repo`/walk-up/default
logic every other command uses.

### Wiring — `DbDataSyncHost.cs` and `TaskRunner/Program.cs`

Both composition roots build `new ProviderRegistry(repoRoot).LoadAll()` **before** constructing
`DriverRegistry` — a descriptor or compiled driver registered from 109d/109e on resolves its provider
through this, so the closure has to be loadable first. An absent or empty `providers/` directory is a
silent no-op, verified by the full existing test suite staying green with no `providers/` directory
anywhere in a normal test run.

### `docker-compose.yml` + CI

Added a `mysql` service (`mysql:9`, port 13306, healthcheck via `mysqladmin ping`) to both
`docker-compose.yml` and `.github/workflows/ci.yml`'s `dotnet-integration` job — the non-embedded
engine every `Category=Integration` test in this phase runs against.

## What this phase does not build

- Any use of a provider by the state store, a driver, verification, or scripting — those keep their
  hard references, unchanged. This phase's only proof-of-life is the test suite below.
- The `driver.yaml` descriptor — 109d.
- Per-plugin isolated `AssemblyLoadContext` — providers share the default context deliberately (a
  `SqlConnection` from a provider and one from a future built-in driver must be the same `Type`);
  isolated contexts are for compiled plugins, 109e's problem.

## How it was verified

- `dotnet build DbDataSync.slnx` clean; the full non-integration and `Category=Integration` suites both
  green with the provider layer wired into both composition roots and no `providers/` directory present
  — confirms the no-op path.
- **New — `tests/DbDataSync.Providers.Tests/ProviderLoadTests.cs`** (`Category=Integration`, against
  the new `mysql` container):
  - `MySqlConnector` appears in **no** `.csproj` anywhere in the solution (a real `grep`-equivalent
    assertion over every tracked `.csproj`, excluding `bin`/`obj`) — the whole point;
  - `provider install` into a temp repo root produces `lib/MySqlConnector.dll` and a `.deps.json`, and
    writes `provider.json`;
  - `ProviderRegistry.GetFactory("MySqlConnector").CreateConnection()` opens against the real MySQL
    container and `SELECT 1` succeeds — proof the assembly loaded, the factory resolved, and the
    connection is genuinely live, not just constructed;
  - `GetFactory` for an uninstalled id names the install command in its exception message.
- **New — `tests/DbDataSync.Providers.Tests/ProviderCommandTests.cs`**: `install` then `list` reports
  the provider and that it resolves; `uninstall` removes the directory; `sync` rebuilds `lib/` from
  `provider.json` alone after `lib/` is deleted by hand; `install` of a package with no known factory
  type and no `--factory-type` is refused, naming the flag.

## Decisions and real bugs found

- **`dotnet publish`, not `dotnet restore`.** The plan doc's own language ("runs `dotnet restore` + a
  publish") turned out to need the publish step to do the actual work — a bare restore leaves packages
  in the shared NuGet cache, not assembled into one flat directory with a `.deps.json` a loader can
  point an `AssemblyDependencyResolver` at. `dotnet publish -o <dir>` is what produces that shape.
- **A real bug caught by the tests, not by inspection**: the first implementation ran
  `dotnet publish` into a `tempDir/out` subdirectory but then copied from `tempDir` itself into `lib/`
  — every file landed one directory level too deep (`lib/out/MySqlConnector.dll` instead of
  `lib/MySqlConnector.dll`), so `lib/` looked empty to anything checking its top level and the
  `AssemblyDependencyResolver` never found the `.deps.json` either. Caught immediately by
  `Install_RestoresTheClosure_AndWritesTheManifest` asserting `lib/`'s direct contents, before it ever
  reached the "does this open a real connection" test — exactly the reason that assertion was worth
  writing separately rather than only end-to-end.
- **A second bug, same root cause (parameter list didn't say what was self-evident)**: the CLI's
  `provider install` package-id extraction stripped known flag/value pairs (`--version`, `--as`, …) but
  not `--repo` — which `DbDataSyncRoot.Resolve` had already consumed from the *original* args, but which
  was still sitting in the args slice `InstallAsync` parses positionally, so a test passing
  `--repo <tempPath>` saw the temp path parsed as a second package id. Fixed by adding `--repo` to the
  strip list; caught by `ProviderCommandTests` exercising the command exactly as an operator would
  (through `ProviderCommand.RunAsync`, not by calling `ProviderInstaller` directly), which is why that
  test file exists as its own thing rather than being folded into `ProviderLoadTests`.
- **An explicit `<RuntimeIdentifier>` on the throwaway project**, not left RID-less. Not flagged as a
  risk in the plan doc, but a portable publish silently drops native assets — the exact case DuckDB and
  SqlClient's SNI shim need this whole mechanism for. Verified against `MySqlConnector` (no native
  assets, so this wouldn't have failed a test either way) — worth flagging as **not yet verified against
  a provider with actual native assets**, since none is installed by any test in this repo yet.
- **One shared static resolver-list**, not one per `ProviderRegistry` instance. `AssemblyLoadContext.Default.Resolving`
  is a single process-wide event; a second `Resolving` handler from a second `ProviderRegistry` (a test
  standing up its own temp repo, the API and the worker both loading providers in the same test process
  in some future test) would coexist fine as independent subscribers, but arming one shared handler that
  consults every registry's resolvers avoided the question of subscription order entirely.

## Open questions

- **Do the restored `lib/` DLLs go in git?** Still undecided, per the plan doc's own framing — this
  phase built the restore-then-load mechanism and the manifest; whether `lib/` is `.gitignore`d with a
  `provider sync` step for a fresh checkout, or committed for air-gapped reproducibility, is unchanged
  by anything built here and is a deployment-policy decision, not an engineering one this phase forces.
- **`provider install`'s `factoryType` guess** — built as a small hardcoded table
  (`KnownProviderFactories`), not attempted for every possible package. Unlisted packages require
  `--factory-type` explicitly; whether the table should grow or whether unlisted should stay a hard
  requirement is left for whoever adds the next engine.
- **Startup cost of the resolver with several providers registered** — not measured. Registration
  itself (`RegisterFactory` + arming one `Resolving` handler) is cheap; actual assembly loading is lazy
  and only measurable once a real deployment has more than one or two providers installed.
