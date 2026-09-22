# A "Compile" button for JDBC drivers — `IkvmReference` from the web UI

**Status**: Design, not phase-ready. Depends on phase 169V (multiple jars) landing first — this reuses
`JdbcDriverSpec.DriverJarPaths`, plural, as its input list.
**Plan reference**: `architecture/planning/todo/jdbc-driver-support.md` ("Where driver artifacts live" —
the `.jar` vs `.dll` trade-off this operationalizes), this session's own `IkvmReference` probe (folded
into that doc), `architecture/implementation/todo/phase-170V-jdbc-ikvm-version-update.md` (the pinned
IKVM version this compiles against).

## The good news: half of this already exists, unused

`JdbcProviderFactory` has had a second loading path since phase 165V, sitting dead:

```csharp
// Imported/JdbcProviderFactory.cs:17-23 — never called from anywhere in this repo today
public static JdbcProviderFactory FromAssemblyPath(string assemblyPath, string driverClass) =>
    new(() =>
    {
        var assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);
        var driverType = assembly.GetType(driverClass, true);
        return (java.sql.Driver)Activator.CreateInstance(driverType!)!;
    }, driverClass);
```

`JdbcGenericDriver`'s constructor only ever calls `FromJarPath`/`FromJarPaths` (169V). Wiring a compiled
`.dll` in is a small, contained change once the `.dll` exists — the real work in this doc is **producing**
that `.dll` on demand, from the web UI, safely.

## What "Compile" does

1. Reads the driver's `driverJarPaths` (169V) and its pinned IKVM version (the same one
   `DbDataSync.Drivers.Jdbc.csproj`/`KnownLibraries`'s `"ikvm"` entry name — phase 170V keeps these in
   sync).
2. Generates a throwaway `.csproj` — the exact shape this session's own scratch probe already proved
   works, and the same "generate a throwaway project, shell out to `dotnet`" mechanism
   `LibraryInstaller.RestoreAsync` already uses for library installs, not a new pattern:
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <TargetFramework>net10.0</TargetFramework>
       <Nullable>disable</Nullable>
     </PropertyGroup>
     <ItemGroup>
       <PackageReference Include="IKVM" Version="{pinned}" />
     </ItemGroup>
     <ItemGroup>
       <IkvmReference Include="{each of driverJarPaths}" />
     </ItemGroup>
   </Project>
   ```
3. Runs `dotnet build -c Release -o <outDir>` via the same `ProcessStartInfo`/concurrent-stdout-stderr-drain
   pattern `LibraryInstaller.RunDotnetAsync` already has (`FileName = "dotnet"`, `RedirectStandardOutput`/
   `RedirectStandardError` both read concurrently to avoid the pipe-buffer deadlock that comment already
   documents).
4. On success, records the produced assembly path and the IKVM version it was built with on the
   descriptor. On failure, surfaces the build output the same way `InstallErrorFormatting.TailOf` already
   trims a `dotnet publish` failure down to the useful tail rather than the whole MSBuild log.

## Schema addition

```csharp
// JdbcDescriptorYaml — new, optional
public sealed class JdbcDescriptorYaml
{
    public required string DriverClass { get; set; }
    public required IReadOnlyList<string> DriverJarPaths { get; set; }   // 169V

    /// Set by the "Compile" action, never hand-authored. Present -> JdbcGenericDriver loads through
    /// FromAssemblyPath instead of FromJarPaths. Absent -> unchanged, jar loading as today.
    public string? CompiledAssemblyPath { get; set; }

    /// The IKVM version the compiled assembly was built with — compared against the currently pinned
    /// version (KnownLibraries's "ikvm" entry) to detect staleness. See "The version-pinning problem,
    /// operationalized" below.
    public string? CompiledWithIkvmVersion { get; set; }
}
```

```csharp
// JdbcGenericDriver's constructor
public JdbcGenericDriver(JdbcDriverSpec spec)
    : base(spec, spec.ValueBinder ?? new GenericValueBinder(spec.Dialect, new JdbcProviderFactoryHandle()))
{
    if (spec.CompiledAssemblyPath is { } dll)
        JdbcProviderFactory.FromAssemblyPath(dll, spec.DriverClass);
    else
        JdbcProviderFactory.FromJarPaths(spec.DriverJarPaths, spec.DriverClass);
}
```

`driverJarPaths` stays on the descriptor regardless of whether a compiled assembly exists — it's the
recompile source, and the fallback the moment the compiled assembly is missing, deleted, or stale.

## The version-pinning problem, operationalized

`jdbc-driver-support.md` already names this risk in the abstract ("pinned to the IKVM version that
produced it"); a "Compile" button makes it a concrete UI state to handle, not just a caveat in prose. A
compiled `.dll` silently goes stale the moment phase 170V (or any future IKVM bump) changes the pinned
version — nothing currently re-checks that. `CompiledWithIkvmVersion` (above) exists so the Drivers screen
can show the real state instead of a button that always just says "Compiled":

- **Compiled, version matches** — "Compiled (IKVM {v})" with a "Recompile" / "Use jar instead" pair.
- **Compiled, version doesn't match the currently pinned one** — "Compiled with IKVM {old} — currently
  pinned to {new}" with **Recompile** as the primary action, not a silent continue-to-use. Whether a
  stale compiled assembly should still be *usable* until recompiled, or should auto-fall-back to jar
  loading until recompiled, is an open question below — the visible state is the same either way.
- **Not compiled** — "Compile" as the only action, plain.

## Open questions

1. **Does a version mismatch block loading, or just warn?** Auto-falling-back to `FromJarPaths` when
   `CompiledWithIkvmVersion` doesn't match keeps the driver always loadable at the cost of silently eating
   the whole point of compiling (startup speed); refusing to load until recompiled is safer but turns a
   routine IKVM bump into an outage for anyone who'd compiled. Leaning towards **warn, still load
   compiled** — matching how a stale library resolution is handled elsewhere in this repo (reported, not
   fatal) — but not decided here.
2. **One `IkvmReference` item per jar — does it produce one assembly or several?** Unverified. This
   session's probe only ever compiled a single jar. Before building this for real: extend the probe to
   two `IkvmReference` items and inspect the build output — if it's one assembly per jar, `CreateConnection`
   needs the *set* of produced paths, not one `CompiledAssemblyPath` string, and `FromAssemblyPath` may
   need a sibling that loads several assemblies before resolving `driverClass` (paralleling 169V's
   `FromJarPaths`). Blocks the schema shape above until answered — do not build the schema as written
   without this check first.
3. **Where does the compiled `.dll` live?** Same open question as the jar itself
   (`jdbc-driver-feature-gaps.md`'s "`jars/` vs `libraries/`"), plus one more wrinkle: it's *derived*
   build output, not an artifact an operator supplied — closer to `libraries/*/lib/`'s restored packages
   (excluded from the config repo's own version control) than to the jar it was built from. Proposed:
   `drivers/<id>/compiled/` alongside the `driver.yaml`, gitignored the way restored library output
   already is — confirm that exclusion pattern actually exists today before assuming it, rather than
   copying an unverified assumption into this doc.
4. **SDK requirement.** Same as library install (phase 120's own decision): needs the .NET SDK present
   in the API process's environment. The default Docker image already moved to an SDK base for that
   reason — "Compile" rides the same requirement, no new image change needed, but the runtime-only image
   (phase 121, for shops wanting a minimal footprint) would need this action disabled/hidden there, the
   same way phase 121 already treats non-catalog library installs as unavailable on that image.
5. **Build time and UX.** The probe measured ~8.6s for one jar. Multiple jars, or a slower host, could
   push this well past what feels like a synchronous button click. `LibrariesController.Create` (library
   install) is synchronous and awaited in-request — the same precedent applies here, but worth explicitly
   deciding rather than assuming: is a multi-jar compile still fast enough to stay a blocking `POST`, or
   does this need the async job pattern nothing else in `DbDataSync.Api` currently has? Measure with a
   real multi-jar driver before deciding — Oracle's wallet jars (four files) are the natural test case,
   the same one 169V's own "how to verify" section already proposes as a fixture.

## What this does not build

- Any change to the *default* loading path — `.jar` via `URLClassLoader` stays the default for every
  driver that never clicks "Compile," unchanged from `jdbc-driver-support.md`'s existing conclusion.
- A standalone `ikvmc` CLI invocation as an alternative to the throwaway-`IkvmReference`-project route.
  Searched for one (an `IKVM.MSBuild.Tools`/`IKVM.Tools.Ikvmc` global tool that takes a jar path directly,
  skipping the throwaway-project overhead) and couldn't confirm the invocation shape from IKVM's own
  public docs — the `IkvmReference`+`dotnet build` route is what's actually proven (this session's probe),
  so that's what this design uses. Worth revisiting once/if IKVM's docs clarify a standalone CLI exists.
- Any UI beyond the button/state described above — this lives inside the Drivers screen
  (`driver-yaml-authoring-ui.md`'s territory) once that exists, as a per-driver row action for any driver
  whose `base` is `JdbcGenericDriver`.
