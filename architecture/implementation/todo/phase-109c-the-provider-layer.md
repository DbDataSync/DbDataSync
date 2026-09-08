# Phase 109c — the provider layer (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*The provider layer*.
Depends on 109a. Independent of 109b. Nothing in the core *uses* the provider layer after this phase
— it only proves the layer works.

## What this builds

A way to restore an ADO.NET provider package DbDataSync does not reference, load it at runtime, and
hand any consumer a `DbProviderFactory` for it. No consumer is switched over here.

### The manifest — `<repo>/providers/<invariantName>/provider.json`

```json
{
  "id": "MySqlConnector",
  "factoryType": "MySqlConnector.MySqlConnectorFactory, MySqlConnector",
  "packages": [
    { "id": "MySqlConnector", "version": "2.4.0" }
  ]
}
```

`packages` is a list because a provider can span assemblies or ship native-asset packages that are
not transitive dependencies (Oracle, DB2). Committed to the git-tracked config repo — an install is
a reviewable commit.

### `dbdatasync provider …` — `src/DbDataSync.Cli/ProviderCommand.cs`

A new top-level command, dispatched from `Program.cs` alongside `secret` / `cert`, with the same
nested `args[0]` sub-switch those use:

- `provider install <packageId>[ <packageId>…] [--version v] [--source feed]` — writes a throwaway
  SDK-style `.csproj` in a temp dir referencing the package(s), runs `dotnet restore` + a publish so
  NuGet's own resolver produces the full closure, copies the output into
  `<repo>/providers/<id>/lib/`, and writes `provider.json` (guessing `factoryType` from the package's
  known factory, editable after). Uses the `dotnet` muxer `ProcessSupervisor` already depends on — no
  NuGet client library in the host.
- `provider sync` — re-runs the restore for every `provider.json` (a fresh deployment, or after a
  version bump).
- `provider list` — id, version, factory type, whether the assembly currently resolves.
- `provider uninstall <id>` — removes the directory.

### `ProviderRegistry` — `src/DbDataSync.Drivers.Abstractions/` (or a new `DbDataSync.Providers`)

- At host startup (API and TaskRunner both), enumerate `<repo>/providers/*/provider.json` and for
  each call `DbProviderFactories.RegisterFactory(id, factoryType)` — the **string overload**, which
  does a lazy `Type.GetType()` on first `GetFactory`, so the assembly need only be *loadable*, not
  *referenced*.
- Loadable means: an `AssemblyLoadContext.Default` resolving handler (or a single shared
  `AssemblyLoadContext`) backed by an `AssemblyDependencyResolver` pointed at
  `<repo>/providers/<id>/lib/<id>.deps.json`, so managed dependencies and
  `runtimes/<rid>/native/` assets (SqlClient's SNI, others) resolve. Providers load into the default
  / one shared context deliberately — a `SqlConnection` from a provider and one from a future
  built-in driver must be the same `Type`.
- `ProviderRegistry.GetFactory(id)` wraps `DbProviderFactories.GetFactory` with a clear error when
  the id is not installed.

### Wiring

- `ApiOptions` / the TaskRunner args already carry the repo root; the registry derives
  `<repo>/providers/` from it.
- An empty or absent `providers/` directory makes startup a silent no-op.

## What this phase does not build

- Any use of a provider by the state store, a driver, verification, or scripting — those keep their
  hard references. This phase's only proof-of-life is a test.
- The `driver.yaml` descriptor — 109d.
- Per-plugin isolated `AssemblyLoadContext` — that is for compiled plugins (109e); providers are
  shared-context.

## How to verify when built

- `dotnet build` clean; existing suite green.
- A `mysql` service added to `docker-compose.yml` (and the CI `services:` block).
- **New — `tests/DbDataSync.Providers.Tests/ProviderLoadTests.cs`** (`Category=Integration`):
  - `provider install MySqlConnector` into a temp repo root (assert the `lib/` closure and
    `provider.json`);
  - start a `ProviderRegistry` against it;
  - `GetFactory("MySqlConnector").CreateConnection()` opens against the MySQL container and
    `SELECT 1` succeeds;
  - `MySqlConnector` appears in **no** `.csproj` (`grep` assertion) — the whole point.
- **New — `ProviderCommandTests`**: `provider list` reports the installed provider; `uninstall`
  removes it; `sync` rebuilds `lib/` from `provider.json` alone.

## Open questions

- **Do the restored `lib/` DLLs go in git?** (Plan doc Q2.) Leaning: a lockfile + `provider sync`
  rebuild; revisit if air-gapped deployment is a requirement.
- Whether `provider install`'s `factoryType` guess needs a small built-in table
  (`MySqlConnector` → `MySqlConnector.MySqlConnectorFactory, MySqlConnector`, etc.) or should always
  be operator-confirmed.
- Startup cost of the resolver with several providers registered — measure; likely negligible since
  registration is lazy.
