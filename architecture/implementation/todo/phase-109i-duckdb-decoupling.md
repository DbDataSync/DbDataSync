# Phase 109i — DuckDB decoupling (planned)

**Status**: Planned, not started. **Decided 2026-09-13, same style as 109h**: `ExcludeAssets="runtime"`
on `DuckDB.NET.Data.Full`, not a first-party plugin rewrite. Priority stays lower than 109g/109h (see
"Why it was deferred, revisited," below) but the mechanism is now the same one, not a separate design.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*Package size — not a lever
here*. Depends on 109c (the library layer) and reuses 109h's own decision verbatim.

## What this builds

### 1. The three consumers, unchanged in every `.cs` file

```xml
<PackageReference Include="DuckDB.NET.Data.Full" Version="1.5.5" ExcludeAssets="runtime" />
```

on `DbDataSync.Drivers.DuckDb.csproj`, `DbDataSync.Verification.csproj`, and
`DbDataSync.Scripting.csproj` — the three projects that construct `DuckDBConnection` directly
(`DuckDbDriver.cs`, `SegmentingStrategyRunner.cs`, `VerificationResultQuery.cs`). None of them go
through `DbProviderFactories`/a `DbProviderFactory` today — they `new DuckDBConnection(...)` directly —
and that's fine: `LibraryRegistry`'s `AssemblyLoadContext.Default.Resolving` hook (109c, reused
verbatim by 109h) satisfies an assembly load however the requesting code reached it, whether by a
direct `new` or a factory lookup. No source change to any of the three.

This is why 109i no longer needs its own design question about whether `DuckDbDriver` becomes a
`GenericDriver` descriptor or a compiled plugin — the old version of this doc's only open question.
Moot: it stays exactly the compiled driver it already is, same as `MsSqlDriver`/`PostgresDriver` under
109h's decision.

### 2. Test projects need their own direct reference — a third confirmed instance of 109h's own gap

Checked, not assumed, for all three: `tests/DbDataSync.Drivers.DuckDb.Tests.csproj` has no direct
`PackageReference` to `DuckDB.NET.Data.Full` — only a `ProjectReference` to the driver.
`tests/DbDataSync.Api.Tests.csproj` has none either, despite its own test code constructing
`DuckDBConnection` directly (grep confirms it, not just importing something that happens to use one).
Both need their own explicit, **not** excluded `PackageReference` to the identical package and
version, the same fix 109h specifies for the MsSql/Postgres test projects. (`DbDataSync.Scripting.Tests`
and `DbDataSync.Verification.Tests` weren't found referencing DuckDB types directly in their own test
code by the same grep — confirm at implementation time whether either needs the same treatment; don't
assume the absence holds once the actual exclusion is in place and something in their transitive chain
that used to carry a local copy no longer does.)

### 3. A `KnownLibraries` catalog entry for DuckDB — doesn't exist yet

Checked: `KnownLibraries.cs` has entries for `microsoft-data-sqlclient` and `npgsql` only. This phase
adds a third:

```csharp
"duckdb", "DuckDB.NET.Data.Full", "<factory type — confirm the exact class at implementation time>",
"DuckDB (embedded)", "The embedded analytical engine DbDataSync's own verification and custom " +
    "segmenting strategies run on.",
```

The factory-type string is the one piece of this catalog entry not confirmed here — none of the three
consumers currently resolve a `DbProviderFactory` by name (see item 1), so nothing in this codebase
today proves which class name is correct the way `MsSqlDriver`'s own `SqlClientFactory` reference
already does for its entry. Confirm `DuckDB.NET.Data`'s actual factory type against the installed
package before writing this line for real, rather than guess it into a catalog entry nothing has
exercised.

### 4. Unconditional install — DuckDB's seeding trigger genuinely differs from 109h's

109h's two providers install *when chosen* (a `StateEngine`, a connection's driver type) — a real
operator decision each time. DuckDB has no equivalent decision: verification and every custom
segmenting strategy use it regardless of which engines a deployment ever configures, so "install it
when chosen" has no trigger to hang off. The natural point is **unconditional, on every `serve`
start**, confirmed against the real current code rather than assumed:

```csharp
// src/DbDataSync.Cli/ServeCommand.cs:18, :44
public static async Task<int> RunAsync(string[] args)
{
    ...
    Prepare(root);
    // new: await EnsureDuckDbInstalledAsync(root, LibraryInstaller.InstallAsync, cancellationToken);
    ...
}
```

`RunAsync` is already `async Task<int>` and already calls the synchronous `Prepare(root)` at line 44 —
unlike `StateDatabaseTab.Save` (109h's own item 3), there is **no signature change needed anywhere** to
insert an async install step right after it. This is also the one call site both `dbdatasync serve`
directly and `setup`'s own Start button reach — `SetupScreen.cs`'s Start button already calls
`ServeCommand.RunAsync` (confirmed in phase 128's own TUI), so hooking here covers both entry points
with one change, not two.

The install itself should be idempotent and silent on success — check whether the library is already
present (`LibraryRegistry`'s own load, or a plain directory check on `<repo>/libraries/duckdb/lib/`)
before calling `LibraryInstaller.InstallAsync`, so every ordinary `serve` restart after the first one
does no network/restore work at all. A failure here should log and continue rather than refuse to
start — the same posture `MapTimeAsync`/`CertificateExpiryService` already take for "this shouldn't be
allowed to take the whole process down": verification and segmenting degrade if DuckDB never installs,
but replication itself does not depend on it.

## Why it was deferred, revisited

The original three reasons, checked against what actually changed:

- *"DuckDB is not a replication concern... has no bearing on the 'add a new engine from NuGet' goal."*
  Still true — unaffected by this update.
- *"The benefit is only 'update DuckDB.NET without a rebuild,' which matters less... a slower security
  cadence than `Microsoft.Data.SqlClient`."* Still a real, lower-priority-than-109h judgment — this
  phase doesn't argue otherwise.
- *"It is the most cross-cutting of the removals... carries the most regression risk for the least
  urgency."* **This one no longer holds under the chosen mechanism.** `ExcludeAssets="runtime"` changes
  zero `.cs` files in any of the three consumers — the regression surface this reasoning warned about
  (three projects' worth of source changes) doesn't exist under this design, the same de-risking 109h
  already established for its own two drivers.

So: still lower priority than 109h, no longer higher-risk than it.

## What this phase does not build

- Removing DuckDB's native assets from the package (per-RID split, trimming) — a separate packaging
  exercise, unchanged from the original doc.
- Making DuckDB optional / not-shipped — verification needs it; item 4 exists precisely because it's
  not optional.
- A `DbProviderFactory`-based rewrite of any of the three consumers' direct `DuckDBConnection`
  construction — not needed for this mechanism to work, per item 1.

## How to verify when built

- `dotnet build` clean.
- `DbDataSync.Drivers.DuckDb.Tests` and `DbDataSync.Api.Tests`, run standalone, green — proves each
  project's own new direct reference actually restores what the transitive one used to provide.
- `dotnet publish src/DbDataSync.Api` — its output does **not** contain `DuckDB.NET.Data.dll` unless a
  library install already ran. The acceptance test that the exclusion took effect.
- A fresh deployment (wiped `libraries/` directory) — `dbdatasync serve` installs DuckDB automatically
  on its first start, with no manual `library install duckdb` step, and a **second** `serve` start does
  no restore work (idempotence check, e.g. by timing or by confirming no network/`dotnet publish`
  subprocess runs the second time).
- Verification and segmenting `Category=Integration` suites green afterward, including the golden-path
  Playwright `24-create-table-plan`/`25-mapping-preview` flows that exercise a DuckDB strategy —
  unchanged from the original doc's own bar.
- A `DuckDB.NET.Data.Full` version bump + `library sync` takes effect with no DbDataSync rebuild — the
  same acceptance bar 109h set for its own two providers.

## Open questions

- **The exact `DuckDB.NET.Data` factory type name for the `KnownLibraries` entry** — see item 3. Not
  guessed here; confirm against the installed package.
- **Whether `DbDataSync.Scripting.Tests`/`DbDataSync.Verification.Tests` also need their own direct
  reference** — see item 2's parenthetical; a grep found nothing today, but re-check once the exclusion
  is actually in place, not before.
