# Phase 183M — `LibrariesService`'s "used by" doesn't survive a catalog-id/package-id divergence

**Status**: Built. See Retrospective.
**Plan reference**: none — found live, diagnosing a real operator report ("the Libraries page says my
IKVM library is unused, but I have a JDBC driver.yaml that names it"). Same bug class `9b6eb2c`/`03e2277`
already fixed twice in this repo's own recent history — this is the one remaining unpatched instance.

## Why

`9b6eb2c` ("A library's id is always its real package id") made a library install register under its
real NuGet package id (`"IKVM"`), not the shorter `KnownLibraries` catalog id (`"ikvm"`) an operator
naturally types. `03e2277` then found the `Curated` flag in `LibrariesService.List()` had gone stale the
moment that landed — it only checked the catalog id — and fixed it with `KnownLibraries
.TryGetByIdOrPackageId`.

That same PR left one more computation in the identical file unfixed: `usedBy`, the mapping from a
library to which `driver.yaml`s reference it. It's built by grouping on `descriptor.Library` (whatever
literal string an operator's driver.yaml spells under `library:`) and read back out keyed by `m.Id` (the
*installed* library's real id — always the package id, per `9b6eb2c`):

```csharp
// Before, LibrariesService.cs
var usedBy = DriverDescriptorScanner.Scan(apiOptions.RepoRoot)
    .Where(e => e.LibraryId is not null)
    .GroupBy(e => e.LibraryId!)                                    // keyed "ikvm"
    .ToDictionary(g => g.Key, g => ...);
...
usedBy.GetValueOrDefault(m.Id, [])                                  // looked up "IKVM"
```

`KnownLibraries.cs:93`'s own ikvm entry is the concrete case that surfaced this: catalog id `"ikvm"`,
package id `"IKVM"` — differing *only in case*. A plain `Dictionary<string,...>` built with no comparer
is ordinal (case-sensitive), so `"ikvm"` and `"IKVM"` never match, and every JDBC driver.yaml naming its
library the documented, natural way (`library: ikvm` — every doc comment and worked example in this
codebase writes it exactly this way) reports the library as **unused**, with no error anywhere: the file
parses fine, the driver builds fine, the connection tests fine — only this one display computation
silently loses the reference.

## What changed

`LibrariesService.cs` gained one private helper, used everywhere `usedBy` is built or read:

```csharp
private static string CanonicalLibraryId(string id) => KnownLibraries.TryGetByIdOrPackageId(id)?.PackageId ?? id;
```

`List()`'s dictionary is now grouped and looked up through this (plus an explicit
`StringComparer.OrdinalIgnoreCase` on both the `GroupBy` and the `ToDictionary`, since a catalog id and
its package id can differ in case alone, exactly like ikvm/IKVM, independent of which "shape" either side
happens to be in). `UsedBy(string libraryId)` — the same computation `DELETE /api/libraries/{id}` checks
before refusing to remove a library still in use — gets the identical treatment, comparing both sides
through `CanonicalLibraryId` rather than doing a bare `==`.

This is deliberately symmetric: it doesn't matter which of the two id shapes a driver.yaml happens to use,
or which shape the library happens to be installed under (a pre-`9b6eb2c` install could still be keyed by
the catalog id) — `CanonicalLibraryId` normalizes either input to the same package id, so every
combination matches.

## What this does not do

- Does not touch `Resolves` — ikvm reporting "does not resolve" is separately documented, expected, and
  intentional (`KnownLibraries.cs:78-91`'s own comment): it isn't a `DbProviderFactory`-shaped library at
  all, and `DriverLoader.LoadDescriptorDrivers` never calls `DbProviderFactories.GetFactory("ikvm")` for
  real. Nothing here changes that.
- Does not touch `DriverDescriptorScanner` itself — it already carries the raw, un-normalized
  `descriptor.Library` string faithfully; normalization belongs at the point two different sources of an
  id get compared, which is entirely inside `LibrariesService`.
- Does not add a migration for an id stored the old way — `CanonicalLibraryId` already makes both shapes
  equivalent everywhere this file compares them, so there's nothing to migrate.

## How to verify

- `tests/DbDataSync.Api.Tests/LibrariesControllerTests.cs`'s existing
  `AnAdmin_SeesTheInstalledLibrary_WithUsedByPopulatedFromTheRealDescriptor` still passes (the
  non-divergent case, both sides already agreeing on the catalog id).
- New: `AnAdmin_SeesADriverNamingTheLibraryByItsPackageId_AsUsingTheSameLibrary` — a second driver.yaml
  (`LibrariesAdminApiFactory.DriverIdByPackageId`) naming the same installed library by its package id
  (`MySqlConnector`) rather than the catalog id the first driver's descriptor uses, against a library
  installed under the catalog id (`mysql-connector`) — the divergence from the opposite direction of the
  real ikvm report, proving the fix isn't one-sided.
- Both pass: `dotnet test tests/DbDataSync.Api.Tests --filter FullyQualifiedName~LibrariesControllerTests`
  (6/6 passed).

## Retrospective

Built and verified 2026-09-23, in the same session that diagnosed it. No design questions — this is a
narrow, mechanical fix applying an already-established pattern (`KnownLibraries.TryGetByIdOrPackageId`,
already proven correct for the `Curated` flag one PR ago) to the one call site that was missed. The real
finding was diagnostic, not architectural: confirming via `KnownLibraries.cs:93` that ikvm's catalog id
and package id differ *only in case* (`"ikvm"` vs `"IKVM"`) is what made a plain case-sensitive dictionary
lookup the exact, reproducible failure mode, rather than a vaguer "sometimes ids don't match" guess.
