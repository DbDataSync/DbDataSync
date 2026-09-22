# Phase 170V — update IKVM 8.11.2 → 8.16.1

**Status**: Built. See Retrospective.
**Plan reference**: none — raised directly by the user.

## Why

`IKVM` is pinned to `8.11.2` (phase 165V) in three places. The current release is **8.16.1** (2026-09-15,
confirmed via the package's own GitHub releases). Two things in the gap are worth having, not just "newer
is better":

- **8.15.0 (2025-12-06) added .NET 10 SDK/tool support outright** (`".NET 10 SDK (vs2026)"`,
  `".NET 10 Tools"`). This repo already targets `net10.0` on 8.11.2, which predates that support being
  official — it has been working, not verified-supported.
- **8.16.1 fixed jar-file-handle release on `JarFile` disposal.** Doesn't change this repo's own leak
  profile (`JdbcProviderFactory` holds its `URLClassLoader` for process lifetime via a `Lazy<Driver>` and
  never disposes it — see below), but it's a correctness fix worth having regardless.

## What's in the gap (8.11.2 → 8.16.1), scanned for anything load-bearing here

Per the package's own release notes:

| Version | Relevant to this repo? |
|---|---|
| 8.13.0–8.13.4 | `jdk8u462-b08` bump, buffer-size fix, `IKVM.Java.Extensions` additions — none touch anything this repo calls. |
| **8.14.0** | **Breaking, but not for us**: `IKVM.Reflection` merged into `IKVM.CoreLib`, "no longer visible to external parties." This repo never references `IKVM.Reflection` directly — confirmed by grep, the only `IKVM.*` surface touched is `java.sql.*`/`java.net.*`/`java.util.*` (via `IKVM.Java`) and the package's own build-asset exclusion comment in the `.csproj`. Also allows `Name` in a jar manifest's main section — irrelevant here. |
| 8.15.0 | `jdk8u472-b08`, **official .NET 10 SDK/Tools support** — see "Why" above. |
| 8.16.0 | `jdk8u482-b08` then `jdk8u504-b01`, LLVM 20 on Linux, new `ikvm.runtime.Util` delegate-conversion methods, nested-annotation-encoding fix. None of this repo's code paths touch annotations or delegate conversion. |
| 8.16.1 | JAR file handle disposal fix, "embed an `ikvm.exports` resource for `IkvmReference` items" (only relevant if phase adopts `IkvmReference`, which it doesn't — see `jdbc-driver-feature-gaps.md`), metadata-token method resolution fix on .NET Core. |

**No breaking change in the gap affects this repo's actual usage** (`java.sql.Driver`/`Connection`/
`Statement`/`PreparedStatement`/`ResultSet`/`Types`, `java.net.URL`/`URLClassLoader`,
`java.util.Properties`) — the one real breaking change (`IKVM.Reflection` visibility) is in a namespace
nothing here imports. Java stays at SE 8 bytecode support throughout (`jdk8u4xx`/`jdk8u5xx` are JDK 8
security/bugfix updates, not a language-level version bump) — the Java-8 ceiling
`jdbc-driver-support.md`'s own "Risks" section names is unchanged by this update.

## What changes

Three files pin `8.11.2` literally; one more names it in a comment (informational, not load-bearing, but
should move too so it doesn't read as stale):

- `src/DbDataSync.Drivers.Jdbc/DbDataSync.Drivers.Jdbc.csproj:31` —
  `<PackageReference Include="IKVM" Version="8.11.2" ... />` → `8.16.1`.
- `tests/DbDataSync.Drivers.Jdbc.Tests/DbDataSync.Drivers.Jdbc.Tests.csproj` — same `PackageReference`.
- `src/DbDataSync.Libraries/KnownLibraries.cs:95` — `PinnedVersion: "8.11.2"` on the `"ikvm"` catalog
  entry. Its own doc comment (lines 81–82) also says "confirmed by reflecting the real 8.11.2
  assemblies" — update that comment once the assembly-name surface (`java.sql.Types, IKVM.Java`) is
  re-confirmed against 8.16.1 (expected to be unchanged — `IKVM.Java` is the assembly carrying the
  `java.*` surface across all these versions per the release notes above — but re-confirm rather than
  assume, the same discipline that comment's own text already models).
- The `.csproj` comment block explaining the asset-exclusion list (`ExcludeAssets="runtime;build;
  buildTransitive;native"`) references "the real 8.11.2 assemblies" — re-verify the exclusion list is
  still sufficient on 8.16.1 (package layout could in principle have changed jar/RID-image/native-asset
  grouping between these versions) before assuming the comment's conclusion still holds, and update the
  version number in the comment either way.

## How to verify when built

- `dotnet build` on both csproj changes — 0 errors is necessary but not sufficient (see below).
- Inspect build output for `DbDataSync.Drivers.Jdbc`/`DbDataSync.Drivers.Jdbc.Tests` with only
  `ExcludeAssets="runtime;build;buildTransitive;native"` set, the same empirical check the original
  165V/168V comment describes — confirm `ikvm/`, `ikvm.properties`, `libikvm.so` are still absent from
  the build output (i.e. the exclusion list doesn't need to grow for 8.16.1's package shape).
- Full `DbDataSync.Drivers.Jdbc.Tests` suite green against the live Postgres container — this is the
  real proof, not the build: `JdbcCatalogTests`, `JdbcReaderParityTests`, `JdbcChangeDatabaseTests`,
  `JdbcDescriptorTests` all exercise real `java.sql.*` calls end to end.
- `dbdatasync config library list` (or the equivalent surface check phase 109j built) against the
  `"ikvm"` catalog entry with the updated `PinnedVersion`, confirming `java.sql.Types, IKVM.Java` still
  resolves as a real type in the 8.16.1 assembly — the thing `KnownLibraries.cs`'s own comment says was
  "confirmed by reflecting the real assemblies rather than guessed," which stops being true the moment
  the version number moves until this check is re-run.
- Optionally, re-run this session's own `IkvmReference` scratch probe (not part of the real repo — see
  `jdbc-driver-support.md`'s "Where driver artifacts live") against 8.16.1 to confirm the `.dll`
  precompilation path (not adopted, but documented) still builds and connects — cheap, since the probe
  project already exists in scratch form.

---

# Retrospective

Three version bumps applied — `DbDataSync.Drivers.Jdbc.csproj`, `DbDataSync.Drivers.Jdbc.Tests.csproj`,
and `KnownLibraries.cs`'s `"ikvm"` entry's `PinnedVersion` — plus the two doc comments that named
`8.11.2` explicitly, so nothing points at a stale version number after this.

**Every verification in "How to verify when built" passed except one, skipped deliberately**:

- Clean rebuild against 8.16.1 — 0 errors, 0 warnings.
- Re-checked the build output for `ikvm/`, `ikvm.properties`, `libikvm.so` — still absent. The existing
  `ExcludeAssets="runtime;build;buildTransitive;native"` list needed no changes for 8.16.1's package
  layout.
- Full `DbDataSync.Drivers.Jdbc.Tests` suite (all 12 tests, including phase 171V's new ones) green
  against the live Postgres container — real `java.sql.*` connections, parameter binding, catalog
  metadata, `ServerVersion`/`DataSource`, all exercised for real on 8.16.1, not just compiled against it.
- `DbDataSync.Api`, `DbDataSync.TaskRunner`, `DbDataSync.Libraries` all build clean against the bump.
- **Not run**: the `dbdatasync config library list` surface-check re-confirmation
  (`java.sql.Types, IKVM.Java` still resolving as a real type via `DriverLibraryCompatibility`) — this
  dev environment has no `libraries/ikvm/lib/` installed (the JDBC test project references IKVM directly,
  un-excluded, the same way `DbDataSync.Drivers.Postgres.Tests` does for Npgsql — see that project's own
  csproj comment), so there's nothing for the surface checker to check yet. The live test suite passing
  is stronger evidence the assembly surface is intact than the metadata-only surface check would have
  been anyway; worth running the CLI check for real the first time `libraries/ikvm/` actually gets
  installed somewhere.
- The optional `IkvmReference` probe re-run was skipped — nothing in this phase's own scope depends on
  it, and `jdbc-ikvmreference-compile-button.md` will need its own fresh verification regardless once
  that's actually built.

No breaking change in the 8.11.2→8.16.1 gap materialized against this repo's actual usage, confirming
this doc's own pre-build scan.
