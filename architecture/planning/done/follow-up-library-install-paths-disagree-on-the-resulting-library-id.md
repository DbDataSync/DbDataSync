# Fixed: two "install a library" paths used to disagree on the resulting library id

Found writing `driver-authoring.spec.ts` — a test asserting `.selectOption('mysql-connector')` timed out
for a full minute with no such option, not a flake. Documented, then fixed for real on 2026-09-22.

## What was wrong

A `KnownLibraries` catalog entry carries two different strings for the same library: a short catalog id
(`"mysql-connector"`) and the real NuGet package id (`"MySqlConnector"`) — deliberately different, so an
operator can type the short one instead of spelling out the package id and factory type by hand. The
catalog id was only ever meant as a lookup shorthand. `LibraryManifest.Id`'s own doc comment already said
so: "Conventionally the primary NuGet package id."

Three install paths didn't agree on that:

- **`POST /api/drivers/from-catalog`** (`DriversController.InstallFromCatalog`) keyed the installed
  library by `entry.BoundLibraryId` — the catalog id.
- **`dbdatasync config driver install --from <knownDriverId>`** (`DriverCommand.InstallDescriptorAsync`)
  defaulted `--library` to the same catalog id and installed under it.
- **`dbdatasync config library install <catalogId>`** (`LibraryCommand.InstallAsync`) installed under
  whatever id the operator literally typed on the command line — the catalog id, if that's what they typed.

Only **`POST /api/libraries`** (`LibrariesController.Create` — what `LibraryFindPanel`'s quick-add chip,
search results, and manual entry all call) already did the right thing, keying by `body.PackageId`.

So clicking the "Add" button on the Drivers catalog list and clicking the same curated pick's quick-add
chip on the Libraries screen produced two different ids for the identical package+version, and the CLI
mostly matched the wrong one.

## The fix

All three now resolve the catalog id to its `PackageId` before installing, and use that as the library's
own id everywhere downstream (the install call, the registry, and — for the two paths that write a
`driver.yaml` — the `library:` field itself, so the written descriptor and the installed library always
agree):

```csharp
// DriversController.InstallFromCatalog
var catalogLibrary = KnownLibraries.TryGetById(entry.BoundLibraryId)!;
var libraryId = catalogLibrary.PackageId;
// ... install and register under libraryId, write "library: {libraryId}" into the driver.yaml
```

```csharp
// LibraryCommand.InstallAsync — --as still overrides explicitly, same as it always has
var id = CliOptions.Read(args, "--as") ?? catalogEntry?.PackageId ?? packageIds[0];
```

```csharp
// DriverCommand.InstallDescriptorAsync
var catalogLibrary = KnownLibraries.TryGetById(requestedLibraryName);
var libraryName = catalogLibrary?.PackageId ?? requestedLibraryName;
```

`LibrariesController.Create` was already correct and is unchanged.

A non-catalog package (no `KnownLibraries` match) is unaffected in all three — the operator's own typed
id passes straight through, exactly as before.

Covered by `LibraryCommandTests` (`InstallByCatalogId_FillsPackageIdAndFactoryTypeFromTheCatalog_AndIsKeyedByThePackageId`,
plus a case proving `--as` still overrides), `DriverCommandTests` (the golden-file test's `library:` line
and installed directory, and the "reuse" test), and `LibraryInstallTests`
(`FromCatalog_InstallsTheBoundLibrary_AndWritesTheDescriptor` and the force-delete test) — all updated to
assert the package id, not the catalog id.

## What's left: already-installed libraries from before this fix

A library installed under the old scheme (keyed by catalog id, on disk as `libraries/mysql-connector/`)
doesn't get renamed by this fix — there's no migration step. It keeps working under its old id. What
changes is only what a *new* install produces going forward.

This is exactly the scenario `LibraryRegistry.GetFactory`'s "did you mean" message (added the same day,
see its own doc comment) is for: a driver.yaml or CLI lookup naming the catalog id when the actual
installed library is keyed by its package id (or, for an old install, the reverse) gets a message naming
the id it's actually installed under, instead of a bare "not installed."
