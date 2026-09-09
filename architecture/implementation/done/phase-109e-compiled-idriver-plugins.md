# Phase 109e — compiled IDriver plugins from NuGet

**Status**: Done.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*The compiled driver*,
§*Assembly loading design*. Depended on 109a, 109c (provider layer / restore machinery). Independent
of 109d but shares its `DriverLoader` seam.

## What this built

Loading a .NET assembly that implements `IDriver` — the same code a driver project in this solution
contains — from a NuGet package, into an isolated `AssemblyLoadContext`. This is the mechanism, not any
specific engine's plugin; it exists for the case the descriptor (109d) is deliberately narrow about —
a bespoke change-data reader, an engine-specific write, provisioning, or anything else that needs real
control rather than a declarative table.

### `DbDataSync.Drivers.Abstractions` as a published package

`src/DbDataSync.Drivers.Abstractions/DbDataSync.Drivers.Abstractions.csproj` gained
`GeneratePackageOnBuild`, a `PackageId`, and a `Version` (matching `DbDataSync.Cli`'s own convention —
`0.1.0` for every local/CI build, overridden by `-p:Version=` on an actual release). Nothing pushes the
resulting `.nupkg` anywhere; that is a CI/release decision this phase does not make. The project
already referenced only `DbDataSync.Core` — the plan doc's "must not transitively drag in anything
heavy" requirement — and needed no change to keep that true.

### `IDriver.ContractVersion` and `DriverContract`

`IDriver` gained `int ContractVersion => 1` as a default interface member — every existing built-in and
every 109d `GenericDriver` is version 1 with no code change. A new `DriverContract` static class
(`DbDataSync.Drivers.Abstractions`) names `CurrentVersion`/`MinSupportedVersion` (both `1` today) and
`IsSupported(int)`. Per the plan doc's Q3 leaning ("no promises, fail loud" pre-1.0): no compatibility
window, a mismatch is refused with a message naming the version range this build supports.

### The manifest — `<repo>/drivers/<id>/driver.json` (compiled variant)

`CompiledDriverManifest` (`DbDataSync.Drivers.Descriptor`, alongside `ProviderManifest`'s shape):
`id`, `kind` (always `"compiled"`, stated rather than assumed so a future second compiled shape can
share the file name safely), `assembly` (a file name, resolved under this driver's own `lib/`),
`driverType` (an assembly-qualified type name), `packages`. Lives in the same `<repo>/drivers/<id>/`
directory a `driver.yaml` would — `DriverLoader` tells the two apart by which file is present.

### The loader — `DriverLoader.LoadCompiledDrivers` + `DriverPluginLoadContext`

- **One `AssemblyLoadContext` per driver** (`DriverPluginLoadContext`, named `driver:<id>`), **not
  collectible** — a driver lives for the process, hot-reload is a non-goal.
- **Shared-contract resolution**: `Load(AssemblyName)` checks whether `AssemblyLoadContext.Default`
  already has an assembly of that name loaded and, if so, returns `null` — deferring to the CLR's own
  fallback to `Default`, which is what makes the plugin's `IDriver` the *same `Type`* as the host's
  rather than a second incompatible one from a redundant copy in the plugin's own `lib/`. Only when
  nothing in `Default` matches does it fall through to its own `AssemblyDependencyResolver` over
  `<repo>/drivers/<id>/lib/<assembly>.deps.json`. `LoadUnmanagedDll` mirrors this for native assets via
  `ResolveUnmanagedDllToPath`.
- A version mismatch (the plugin wants a newer version of something the host already has loaded) is
  logged via `onDownBind` rather than silently accepted or thrown — the plan doc's "logs the driver's
  dependency closure and any assembly it had to down-bind."
- Loads the assembly, `Type.GetType`s `driverType`, `Activator.CreateInstance`s it, checks
  `DriverContract.IsSupported(driver.ContractVersion)`, then `registry.Register(driver)`. Any failure
  along this path — missing assembly, type not found, type doesn't implement `IDriver`, contract
  mismatch — is logged and that one driver is skipped, exactly as a failed `driver.yaml` is (same
  `onError` shape, same reasoning: one plugin's problem must not take every other driver down with it).
- Wired into both composition roots (`DbDataSyncHost.cs`, `TaskRunner/Program.cs`) alongside
  `LoadDescriptorDrivers` — same repo-root scan, same per-process repetition for the worker.

### `dbdatasync driver install --kind compiled`

Extended `DriverCommand.InstallAsync` (dispatches on `--kind`) rather than a separate subcommand:
`driver install <id> --kind compiled --package <packageId> --version <v> --assembly <name.dll>
--driver-type <FQTypeName> [--source feed]`. Restores the package into `<repo>/drivers/<id>/lib/`
directly — **not** `providers/<id>/`, because a compiled driver's package is private to it, unlike a
shared provider another driver or the state store might also resolve — via a new
`ProviderInstaller.RestorePackagesAsync`, extracted from `InstallAsync`'s restore-then-copy half so
both the provider and compiled-driver paths share the identical publish/flatten/replace mechanics
without duplicating them. `driver list` reports a compiled driver's assembly and type alongside every
descriptor's display name and provider.

## What this phase does not build

- Any specific engine's plugin — this is the mechanism; the fixture exists only for tests.
- A compiled `StateDialect` plugin — rides the same loader, but is 109f/109h's problem.
- Collectible / hot-reloadable plugins.
- Publishing `Abstractions` to nuget.org publicly — packaging metadata only; where (or whether) a
  `.nupkg` gets pushed is a release-process decision, not something this phase forces.

## How it was verified

- `dotnet build DbDataSync.slnx` clean; `dotnet pack` on `DbDataSync.Drivers.Abstractions` (implicit in
  every build now, via `GeneratePackageOnBuild`) produces a `.nupkg`.
- **New — `tests/fixtures/DbDataSync.Drivers.LoaderTestFixture/`**: a real, separate project — not
  referenced by any other project or by the solution's normal build graph in a way that would load it
  into a test process ahead of time — implementing `FixtureDriver` (wraps `Microsoft.Data.Sqlite`,
  `ContractVersion` defaults to 1), `FixtureDriverBadContract` (`ContractVersion => 999`), and
  `NotADriver` (a real type that isn't one). `tests/DbDataSync.Drivers.Loader.Tests/` publishes it via
  `dotnet publish -r <rid> --self-contained false` (the same native-asset lesson 109c's
  `ProviderInstaller` already learned, needed again here since SQLite's `libe_sqlite3.so` would
  otherwise be silently skipped) and drives it through the real loader:
  - `LoadCompiledDrivers_RegistersThePlugin_AndARoundTripSucceeds` — the loaded driver actually opens a
    SQLite connection and reads a value back (via reflection, since the test project has no
    compile-time reference to the fixture's concrete type);
  - `LoadCompiledDrivers_IsolationHolds_ThePluginsIDriverIsTheHostsType` — the assertion that matters:
    `Assert.IsAssignableFrom<IDriver>(driver)` succeeds even though the fixture's own publish output
    carries a second copy of `DbDataSync.Drivers.Abstractions.dll` (a transitive `ProjectReference`
    copy) — proof the shared-contract deferral works, not just that *a* type loaded;
  - `LoadCompiledDrivers_AContractVersionTheHostCannotSupport_IsRefused_AndSkipped` — `ContractVersion
    999` is refused, the registry never gets it, and the error names both the offending version and the
    host's supported range;
  - `LoadCompiledDrivers_AMissingAssembly_IsSkippedRatherThanThrowing` and
    `..._ADriverTypeThatIsNotAnIDriver_IsSkippedRatherThanThrowing` — both failure modes are logged and
    skipped, never propagated to crash host startup.
- **CLI smoke-tested by hand** against a real package (`Microsoft.Data.Sqlite`, since no real published
  package exists for the fixture): `driver install --kind compiled` restores it into
  `drivers/<id>/lib/`, writes a correct `driver.json`, and `driver list` reports it.
- Full non-integration and `Category=Integration` suites both green afterward, including every earlier
  phase's tests — nothing in 109a–109d regressed.

## Decisions and real bugs found

- **A real, pre-existing latent deadlock, caught by this phase's own tests hanging.** Both
  `ProviderInstaller.RunDotnetAsync` (109c) and this phase's own `FixturePublisher.PublishOnce` read a
  spawned process's `StandardOutput` to completion *before* reading `StandardError` — a classic
  cross-process deadlock the moment the child writes enough to the unread stream to fill its OS pipe
  buffer (64KB, typically) while blocked waiting for the parent to drain the other one. 109c's own
  provider-install tests never had enough stderr output to trigger it; this phase's fixture publish,
  restoring a heavier dependency closure with more NuGet/MSBuild diagnostic noise, did — the test hung
  for the length of the tool's default background-command timeout before anyone would have noticed.
  Fixed in both places by reading both streams concurrently (`Task.WhenAll`/`Task.WaitAll`) rather than
  sequentially. Worth calling out precisely because it was latent in already-shipped, already-tested
  code from two phases earlier — the fix belongs to this phase's retrospective because this is where it
  was found, not because 109c's own tests were wrong to have passed without it.
- **The fixture project deliberately references `DbDataSync.Drivers.Abstractions` via a normal
  `ProjectReference`**, which means its own publish output carries a redundant copy of that assembly —
  left in deliberately (not excluded) because the isolation test's whole point is proving the loader
  prefers the host's already-loaded copy over exactly that redundant one. A fixture engineered to avoid
  carrying a copy would have proven nothing about the mechanism it exists to test.
- **`ProviderInstaller.RestorePackagesAsync` extracted from `InstallAsync`**, not duplicated. The
  restore-then-copy mechanics (publish with an explicit RID so native assets flatten, replace any stale
  `lib/`, copy everything over) are identical whether the target is `providers/<id>/lib/` with a
  `provider.json` beside it or `drivers/<id>/lib/` with a `driver.json` — only the manifest differs,
  and only the caller (`ProviderInstaller.InstallAsync` vs. `DriverCommand.InstallCompiledAsync`) knows
  which one to write.
- **Known gap, not structurally closed**: if a provider (109c) a compiled driver also depends on has
  not yet been loaded into `AssemblyLoadContext.Default` at the moment the driver's own `Load` override
  runs (`ProviderRegistry.RegisterFactory` only registers a *name*, lazily resolved on first
  `DbProviderFactories.GetFactory` — it does not eagerly load the assembly), the driver's own
  `AssemblyDependencyResolver` will find and load a *private* copy from its own `lib/` instead, which
  is a second `Type` from whatever a later `provider install`-driven resolution would produce. Not
  observed in either composition root (providers load before drivers in both), and not exercised by any
  test here — flagged rather than fixed, since closing it properly would mean either eagerly loading
  every registered provider at startup (a real cost 109c's own Q4 already left open) or teaching the
  driver's `AssemblyDependencyResolver` about providers it should never resolve privately.

## Open questions — resolved

- **Contract versioning cadence (Q3)**: built exactly as leaned — an integer, no promises, fail loud.
  Revisit only if an external plugin ecosystem actually forms.
- **Whether the loader should verify the plugin's own `Abstractions` reference version, or let the ALC
  redirect handle it and only report**: resolved as the latter — the shared-context deferral means the
  plugin's `Abstractions` reference is never actually loaded from its own `lib/` at all (it defers to
  the host's copy), so there is no separate version to check; `ContractVersion` is the real compatibility
  signal, and that is checked explicitly.
- **Test-feed mechanics in CI**: resolved by not needing one — the fixture is published directly via
  `dotnet publish` rather than packed and restored through a local NuGet feed, since the loader is what
  this phase needed to prove, and the restore mechanism itself was already proven in 109c. A real
  driver's own `driver install --kind compiled` still goes through the normal NuGet restore path
  (smoke-tested by hand against `Microsoft.Data.Sqlite`, a real published package).
