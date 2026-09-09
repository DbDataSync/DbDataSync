# Phase 116 — rename "provider" → "library", and a descriptor references its library by id

**Status**: Done.
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*Why rename "provider"*, §*The descriptor / library split*. First phase of that arc; 117–120 build
on it. Independent of the phase-109g–109i dependency-removal series, but renames the layer they
consume — those still-`todo/` phase docs keep their `ProviderRegistry` wording and will rebase onto
`LibraryRegistry` when they're actually implemented, per this phase's own plan.

## What this built

### The rename — `src/DbDataSync.Providers/` → `src/DbDataSync.Libraries/`

- Project directory, `.csproj`, namespace, `DbDataSync.slnx` entries (both `/src/` and `/tests/`
  folders), every `ProjectReference` to it (`DbDataSync.Api`, `DbDataSync.TaskRunner`,
  `DbDataSync.Cli`, `DbDataSync.Drivers.Descriptor`).
- Types: `ProviderManifest` → `LibraryManifest`, `ProviderRegistry` → `LibraryRegistry`,
  `ProviderInstaller` → `LibraryInstaller`, `ProviderPaths` → `LibraryPaths`,
  `KnownProviderFactories` → `KnownLibraries`, `ProviderPackageRef` → `PackageRef`.
- On disk: `<repo>/providers/<id>/provider.json` → `<repo>/libraries/<id>/library.json`; the `lib/`
  subdirectory name unchanged.
- CLI: `dbdatasync config provider install|sync|list|uninstall` → `config library …`.
  `ProviderCommand.cs` → `LibraryCommand.cs`; `ConfigCommand.cs`'s dispatch arm and usage text;
  `Help.cs` updated to match.
- `LibraryRegistry.GetFactory`'s message now names `dbdatasync config library install <id>`;
  `ReadinessChecks.cs`'s check renamed `ProvidersAndDriversCheck` → `LibrariesAndDriversCheck`, its
  `CheckResult` name `"Providers / drivers"` → `"Libraries / drivers"`, fix text now says
  `config library sync`.
- Doc comments across the solution touched wherever they described this layer (`DbDataSyncHost.cs`,
  `TaskRunner/Program.cs`, `SetupCommand.cs`, `DriverLoader.cs`, `DriverDescriptorReader.cs`,
  `CompiledDriverManifest.cs`).

### The descriptor references a library by id

- `driver.yaml`'s `provider:` block (`factoryType` + `packages`) is gone; replaced by a single
  `library: <id>` line.
- `DriverDescriptorYaml.cs`: `DescriptorProviderYaml`/`DescriptorPackageYaml` removed; `Provider`
  replaced by a bare `required string Library`.
- `DriverDescriptorReader.cs` unchanged in shape (still returns a `GenericDriverSpec` given an
  already-resolved factory); `DriverLoader.LoadDescriptorDrivers` now resolves
  `libraryRegistry.GetFactory(descriptor.Library)` instead of reading a package id out of the old
  inline block.
- `DriverCommand.cs`: `--provider <packageId>` → `--library <name>`. `config driver install` now
  checks `LibraryRegistry` first — **reuses** an already-installed library named `<name>` (trusting
  its on-disk `factoryType` over a mismatched `--factory-type`/`KnownLibraries` guess, per the open
  question below) rather than reinstalling it, and only calls `LibraryInstaller.InstallAsync` when
  `<name>` isn't yet installed. `--factory-type` still accepted for the install-fresh path.
- `DriverTemplates.cs`: `Render` dropped its `factoryType`/`package` parameters (nothing in the
  template body needs them once the `provider:` block is gone) in favor of one `libraryId` parameter;
  templates emit `library: __LIBRARY_ID__`.
- `library.json` needed no schema change — `LibraryManifest` already carried `Id`/`FactoryType`/
  `Packages` from 109c; only the descriptor side was carrying a redundant copy.

### Leaves alone

- The three built-in drivers, `DriverRegistry`, `DriverLoader`'s compiled-plugin path and
  `CompiledDriverManifest` (only its `Packages` field's type renamed to `PackageRef` — a compiled
  driver's package still restores privately under its own `lib/`, not a library), the `dotnet publish`
  restore mechanics, `IStagingProvider`, the `metadataProvider` slot.
- No web/API changes — `GET /api/drivers` untouched (extended in 118).
- `CONFIG.md` — turned out to have no `provider:`-shaped starter-YAML comment to update; its two
  "provider" mentions are about `ClrKernel.Core.Secrets.SecretStore`'s naming and the state-engine
  connection-string key, unrelated to this rename.
- `DbDataSync.Api/Services/ParameterCheck.cs`'s unknown-driver message — already said "Unknown driver
  '{type}'. Install it with `dbdatasync config driver install <package>`.", with no "provider" wording
  to change and no test asserting its exact string.

## How it was verified

- `dotnet build DbDataSync.slnx` clean; `npm run build` unaffected (confirmed — the SPA doesn't touch
  this layer until 118).
- Full `Category!=Integration` suite green (renamed `DbDataSync.Providers.Tests` →
  `DbDataSync.Libraries.Tests`, every caller's tests updated for the rename — no behavior changes
  beyond the ones described above).
- `tests/DbDataSync.Drivers.Descriptor.Tests/DescriptorDialectTests.cs` updated: a descriptor with
  `library: mysql-connector` (and two more fixtures, `firebird-client`/`oracle-managed-data-access`)
  parses; the `provider:` form is gone from every fixture in the file.
- `tests/DbDataSync.Api.Tests/DescriptorDriverTests.cs` (`Category=Integration`, MySQL → SQL Server
  end to end) green with the reference model — `DescriptorDriverApiFactory` installs the library and
  writes a `library:`-form `driver.yaml` before host start, as `config driver install` now would.
- New `tests/DbDataSync.Drivers.Descriptor.Tests/DriverLoaderTests.cs`: a descriptor naming a library
  that isn't installed is logged-and-skipped by `DriverLoader` with a message naming
  `dbdatasync config library install`, and `LoadDescriptorDrivers` itself never throws (proving host
  startup survives one bad descriptor, the existing `onError` contract).
- CLI smoke by hand against a scratch repo: `config library install MySqlConnector --version 2.4.0`,
  `config library list` (reports `resolves`), `config driver install mysql.generic --library
  MySqlConnector --version 2.4.0 --from mysql` (writes the `library:`-form descriptor), `config driver
  install mysql.generic2 --library MySqlConnector --version 2.4.0` (reused the already-installed
  library — printed "Reusing already-installed library"), `config driver list`, `config library
  uninstall`, `config driver uninstall` (both directions).

## Decisions made

- **`config driver install` reusing an existing library trusts the installed one's `factoryType`**
  over a mismatched `--factory-type`/`KnownLibraries` guess, rather than requiring them to match (the
  open question leaned this way; implemented as leaned — a warn-on-mismatch is left for whenever it's
  actually observed to matter, since nothing today can produce a mismatch other than an operator
  deliberately overriding `--factory-type`).
- **`PackageRef` (not `LibraryPackageRef`)** — the shorter name read fine at every call site once the
  rename landed; no grep-ambiguity showed up in practice since it's always used alongside
  `LibraryManifest`/`LibraryInstaller` in context.
- **`DriverTemplates.Render` dropped its `factoryType`/`package` parameters** rather than keeping them
  unused — the plan doc didn't call this out explicitly, but once the `provider:` block left the
  template body there was nothing left for those parameters to fill in.

## What's explicitly out of scope / not built

- Anything web-facing (118+).
- The catalogs — `KnownLibraries` stays a plain `id → factoryType` table here; enriched in 117.
- Blocking `config library uninstall` on an in-use library (118's `usedBy`) — confirmed by smoke test
  above: uninstalling a library still in use by a driver succeeds today, silently.
