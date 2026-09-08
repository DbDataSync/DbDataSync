# Phase 109e — compiled IDriver plugins from NuGet (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*The compiled driver*,
§*Assembly loading design*. Depends on 109a, 109c (provider layer / restore machinery). Independent
of 109d but shares its `DriverLoader` seam.

## What this builds

Loading a .NET assembly that implements `IDriver` — the same code a driver project in this solution
contains — from a NuGet package, into an isolated `AssemblyLoadContext`. For engines that need a
bespoke change-data reader (Flashback / LogMiner / binlog / a vendor CDC feed), an engine-specific
writer, a catalog no named strategy covers, or provisioning DDL.

### `DbDataSync.Drivers.Abstractions` as a published package

- Add packaging metadata to `DbDataSync.Drivers.Abstractions.csproj` (`PackageId`,
  `<GeneratePackageOnBuild>` in CI). It becomes the contract a third-party driver references.
- It must not transitively drag in anything heavy — it already references only `Core`. Keep it that
  way; a plugin author gets `IDriver`, `IChangeReader`, `ChangeRow`, `ReadResult`, the marker
  interfaces, and nothing else.
- Version it deliberately from here (see Open questions / plan Q3).

### The manifest — `<repo>/drivers/<id>/driver.json` (compiled variant)

```json
{
  "id": "mysql.binlog",
  "kind": "compiled",
  "assembly": "MyCompany.DbDataSync.Drivers.MySqlBinlog.dll",
  "driverType": "MyCompany.DbDataSync.Drivers.MySqlBinlog.MySqlBinlogDriver",
  "packages": [
    { "id": "MyCompany.DbDataSync.Drivers.MySqlBinlog", "version": "1.0.0" }
  ]
}
```

`driver install --kind compiled <packageId>` restores it the same way `provider install` does.

### The loader — `DriverLoader.LoadCompiledDriver`

- One `AssemblyLoadContext` per driver, name `driver:<id>`, **not collectible** (a driver lives for
  the process; hot-reload is a non-goal).
- An `AssemblyDependencyResolver` over `<repo>/drivers/<id>/lib/<assembly>.deps.json` for the
  plugin's private dependencies and native assets.
- The ALC's `Load` override returns `null` — deferring to the default context — for any assembly the
  default context already has: `DbDataSync.Drivers.Abstractions`, `DbDataSync.Core`,
  `System.Data.Common`, the framework. This is what makes the plugin's `IDriver` the *same type* as
  the host's. Provider assemblies (109c) are in the shared context too, so a `SqlConnection` a plugin
  creates and one the host creates are one type.
- Load the assembly, find `driverType`, `Activator.CreateInstance`, `registry.Register`.
- On a load failure (missing dependency, contract mismatch) — log the plugin's dependency closure and
  the specific assembly that could not resolve or down-bound, and skip that driver rather than
  failing host startup.

### Contract-version check

- `IDriver` gains `int ContractVersion => 1` (default so existing built-ins need no change).
- The loader refuses a plugin whose `ContractVersion` is outside `[MinSupported, Current]` with a
  message telling the operator which DbDataSync version to target.

## What this phase does not build

- Any specific engine's plugin — this is the mechanism. A fixture plugin exists only for tests.
- A compiled `StateDialect` plugin — that rides the same loader but is 109f/109h.
- Collectible / hot-reloadable plugins.
- Publishing `Abstractions` to nuget.org publicly (CI can push to an internal feed; public listing is
  a release decision).

## How to verify when built

- `dotnet build` clean; `dotnet pack DbDataSync.Drivers.Abstractions` produces a package.
- **New — `tests/DbDataSync.Drivers.Loader.Tests/`**:
  - a minimal fixture `IDriver` (its own csproj, packed to a CI-local feed) that wraps
    `Microsoft.Data.Sqlite` and does a trivial watermark read;
  - `driver install --kind compiled` it, load it, assert it appears in `DriverRegistry` and a
    read round-trips;
  - **isolation**: the fixture's `IDriver` `is` the host's `IDriver` (same `Type`); a fixture built
    against a deliberately newer transitive `System.Text.Json` loads with the host's version and logs
    the down-bind;
  - a fixture declaring `ContractVersion = 999` is refused with the target-version message.
- The golden-path suite green — no built-in driver changed.

## Open questions

- **Contract versioning cadence** (plan Q3). Leaning: `ContractVersion` integer + "no promises,
  fail loud" while pre-1.0; formalise only if an external ecosystem forms.
- Whether the loader should verify the plugin's `Abstractions` reference version against the host's
  before instantiating, or let the ALC redirect handle it and only report.
- Test-feed mechanics in CI — a `dotnet nuget add source ./local-feed` + `dotnet pack` step, scoped
  to the loader test project.
