# Follow-up: two "install a library" paths produce two different id shapes for what looks like the same action

Found writing `driver-authoring.spec.ts` — a test asserting `.selectOption('mysql-connector')` timed out
for a full minute with no such option, not a flake. Documented to fix later, not addressed here.

## What's true today

**`POST /api/drivers/from-catalog`** (`DriversController.InstallFromCatalog`) installs a `KnownDrivers`
entry's bound library using the **catalog's own id**:

```csharp
result = await LibraryInstaller.InstallOrDeferAsync(
    apiOptions.RepoRoot, entry.BoundLibraryId,   // "mysql-connector"
    [new PackageRef(catalogLibrary.PackageId, body.Version)], catalogLibrary.FactoryType);
```

**`POST /api/libraries`** (`LibrariesController.Create` — what `LibraryFindPanel`'s quick-add chip,
search results, and manual entry all call, *even for the same curated `mysql-connector` catalog entry*)
installs it keyed by **package id** instead:

```csharp
result = await LibraryInstaller.InstallOrDeferAsync(
    apiOptions.RepoRoot, body.PackageId,          // "MySqlConnector"
    [new PackageRef(body.PackageId, body.Version)], factoryType, nugetSource: body.Source);
```

`KnownLibraries`'s own `mysql-connector` entry has `id: "mysql-connector"` and
`packageId: "MySqlConnector"` — two different strings by design (the id is a stable catalog key, the
package id is NuGet's own name). Clicking the *same* quick-add chip through two different screens
(`LibrariesPage`'s own "Find a library to install" panel vs. anything that reaches
`from-catalog`) lands on two different library ids for the identical package+version.

## Why this matters

Not a bug in the sense of anything breaking today — both paths install a working library, and nothing
currently depends on the id matching across them. But it's a real, confusing inconsistency: an operator
(or a driver-authoring form's own library `<select>`) has no way to predict which id a given "install"
click will produce without knowing which underlying endpoint it happens to call. `driver-authoring.spec.ts`
had to be written around this explicitly (see its own comment) rather than being able to assume one
canonical shape.

## Suggested fix, for whenever this gets picked up

Make `LibrariesController.Create` check whether `body.PackageId` matches a `KnownLibraries` entry's own
`PackageId` and, if so, install under that entry's `Id` rather than the raw package id — the same
resolution `InstallFromCatalog` already does, just reached from a different entry point. Needs a decision
first: is silently renaming a curated package's resulting id from what the operator typed in the surprising
in a different way (an operator who typed "MySqlConnector" manually, not via the chip, might not expect
"mysql-connector" to be what shows up)? Worth resolving deliberately, not by whichever behavior happens to
be convenient to implement.
