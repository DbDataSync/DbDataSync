# Phase 146 — DuckDB native asset exclusion

**Status**: Complete.
**Plan reference**: `architecture/planning/done/nuget-loaded-drivers.md` §*Package size — not a lever
here*, which named exactly this as the deferred work ("a separate exercise on DuckDB's native assets —
per-RID split packages... or dropping platforms we do not ship") when the original loader design
decided DuckDB had to ship regardless. Also closes the item phase 109i's own "What's explicitly out of
scope" section named and left: "DuckDB's native per-RID assets are still shipped, unchanged."

## Why now

A user question about the published `2026.9.16.506` release ("I thought DuckDB was no longer
embedded") led to actually inspecting the shipped nupkg rather than trusting phase 109i's own title.
It's 157MB. Unzipped and sorted by size: the top five files are DuckDB's own native binaries —
`libduckdb.dylib` (117MB, osx), two `libduckdb.so` (linux-x64 70.5MB, linux-arm64 63MB), two
`duckdb.dll` (win-arm64 42.8MB, win-x64 36.7MB) — ~330MB uncompressed, the overwhelming majority of
the package, all five shipping unconditionally on every install regardless of which one platform could
ever load.

109i's own retrospective already documented why, precisely: `ExcludeAssets="runtime"` (the mechanism
109h established for Microsoft.Data.SqlClient/Npgsql, reused verbatim by 109i for DuckDB) only drops a
package's managed `lib/` assembly. NuGet's asset-type taxonomy treats `native` — the
`runtimes/<rid>/native/` bucket a package like `DuckDB.NET.Data.Full` also carries — as a *separate*
bucket the same flag never touches. 109i built the managed-assembly half of decoupling (DuckDB's .NET
binding loads lazily through `LibraryRegistry`, installed unconditionally at every `serve` start) and
correctly scoped the native half out as future work. This phase is that future work, arriving sooner
than expected because someone actually asked why the package was still large.

## What this built

### The one-line mechanism: `ExcludeAssets="runtime;native"`

On all three direct consumers of `DuckDB.NET.Data.Full` — `DbDataSync.Drivers.DuckDb.csproj`,
`DbDataSync.Verification.csproj`, `DbDataSync.Scripting.csproj` — `ExcludeAssets="runtime"` became
`ExcludeAssets="runtime;native"`. Nothing else changed. No `.cs` file in any of the three projects, or
in `DbDataSync.Api`/`DbDataSync.Cli` two and three `ProjectReference` hops downstream, needed to change
either.

That this was sufficient rests entirely on one fact, true before this phase and unchanged by it:
`LibraryInstaller.PublishAsync` (the mechanism 109i's own `ServeCommand.EnsureDuckDbInstalledAsync`
already calls, unconditionally, at every `serve` start) restores each library's packages via `dotnet
publish` with `<RuntimeIdentifier>` set to `RuntimeInformation.RuntimeIdentifier` — **this machine's**
RID, not a portable, RID-less publish. Its own comment already named exactly this case: "a package with
`runtimes/<rid>/native/` assets (SqlClient's SNI shim, **DuckDB's libs**) has its native binaries for
*this* machine copied into the flat publish output." The install path that fetches the managed assembly
was always also fetching the one correct native binary alongside it — the native asset just had nowhere
to *not* be redundant, because the CLI's own package was still carrying all five platforms regardless of
what the install path did. Excluding `native` from the three consumers removes the redundant copy; the
already-existing install path is what was always going to supply the real one.

## Verification

**The size claim, checked, not estimated.** `dotnet pack src/DbDataSync.Cli/DbDataSync.Cli.csproj -c
Release` before this change: 152.6MB. After: 46.4MB — a 70% reduction. Unzipped and re-checked: no file
matching `*duckdb*` remains except `DbDataSync.Drivers.DuckDb.dll`/`.pdb` — this repo's own thin driver
wrapper, not the DuckDB engine. Every other large file remaining (LibGit2Sharp/SQLite native binaries
across every RID, Roslyn, the SPA bundle) is unrelated to DuckDB and unaffected by this phase — those
are meant to ship unconditionally (framework-dependent, RID-agnostic tool, per `DbDataSync.Cli.csproj`'s
own `RollForward` comment).

**The functional claim, checked, not assumed.** A throwaway console project (the same "real, not
mocked" precedent 109i's own verification and `ServeCommandDuckDbTests`'s doc comment already
established) referenced `DbDataSync.Libraries` directly, called `LibraryInstaller.InstallOrDeferAsync`
against a fresh root, listed what actually landed in `libraries/duckdb/lib/`, then wired the same
`AssemblyLoadContext.Default.Resolving` pattern `LibraryRegistry` uses, loaded the factory type, opened
a real `DuckDBConnection`, and ran `SELECT 6 * 7`:

```
Files in /tmp/duckdb-check-root/libraries/duckdb/lib:
      70529912 libduckdb.so
        448512 Apache.Arrow.dll
        166912 DuckDB.NET.Data.dll
        118784 DuckDB.NET.Bindings.dll
         41984 Apache.Arrow.Scalars.dll
DuckDB says: 42
```

One native binary — `libduckdb.so`, this machine's own RID (linux-x64) — not five. The query ran for
real, through the newly-excluded-from-the-package, lazily-installed assembly and its lazily-installed
native dependency.

**Existing test suites, unmodified, all still green**, confirming nothing about the three consumers'
own compiled behaviour changed (only what ships alongside them):
`DbDataSync.Drivers.DuckDb.Tests` 33/33, `DbDataSync.Verification.Tests` 27/27,
`DbDataSync.Scripting.Tests` 58/58, `DbDataSync.Libraries.Tests` 30/30, `DbDataSync.Cli.Tests` 139/139
(10 skipped, Windows-only) — including `ServeCommandDuckDbTests.FreshRoot_InstallsDuckDbForReal`, the
one test that already exercised a real install end-to-end before this phase and still does. The
DuckDB-touching slice of `DbDataSync.Api.Tests` (library search, install, validate,
query-preview): 29/29.

## What this does not change

- **DuckDB still ships to every real deployment, unconditionally** — 109i's own design, untouched.
  `ServeCommand.EnsureDuckDbInstalledAsync` still runs at every `serve` start regardless of whether
  anything asks for DuckDB; this phase only stops the CLI's own package from *also* carrying it
  pre-baked for four platforms nobody running that specific install will ever be.
- **The container image.** `Dockerfile`'s `internal build-catalog-cache` step restores every
  `KnownLibraries` entry (DuckDB included) independently of the CLI's own publish/pack output — it was
  never affected by what `ExcludeAssets` the three consumer `.csproj` files carried, and isn't affected
  by widening that exclusion now.
- **SqlClient/Npgsql.** A different, deliberate design (109h): `DbDataSync.Api`/`.TaskRunner` hold
  their own direct, non-excluded references to those packages because they must always be available,
  not lazily installed — there is no equivalent native-asset gap to close for them, because they were
  never excluded past the driver project in the first place.

## Small inaccuracy fixed in passing

The `ExcludeAssets="runtime"` comment on `DbDataSync.Drivers.DuckDb.csproj` (pre-existing, not written
this phase) already claimed "the managed DLL and its native libduckdb asset are neither copied here nor
into DbDataSync.Api/.Cli's own output" — true only of the managed half, and directly contradicted by
109i's own "What's explicitly out of scope" section a few dozen lines away in the same doc, which
correctly said the opposite. Rewritten (all three `.csproj` comments) to describe the real, now-verified
history instead of repeating the drift.
