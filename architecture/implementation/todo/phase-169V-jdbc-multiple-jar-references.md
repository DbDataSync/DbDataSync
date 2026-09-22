# Phase 169V — `driver.yaml` names a list of JDBC jars, not one

**Status**: Not started.
**Plan reference**: none — raised directly by the user, following on from phase 168V
(`architecture/implementation/todo/phase-168V-generic-driver-base-and-jdbc-descriptor.md`).
**Updated 2026-09-22**: `architecture/planning/todo/user-provided-files-store.md` resolved the
"literal filesystem path, provisional" question this phase originally deferred — `DriverJarPaths` entries
are now **names within the new `files/` store**, not arbitrary filesystem paths. Unbuilt, so changed here
directly rather than migrated; see that doc for the store itself, which this phase now depends on.

## Why

`JdbcDescriptorYaml.DriverJarPath`/`JdbcDriverSpec.DriverJarPath` is a single string today, and
`JdbcProviderFactory.FromJarPath` loads exactly one jar into a `URLClassLoader`. A real vendor driver is
not always one jar:

- **Oracle wallet/TCPS support** — `ojdbc8.jar` alone cannot open a wallet-based connection; it needs
  `oraclepki.jar`, `osdt_cert.jar`, `osdt_core.jar` alongside it.
- **Db2** — `db2jcc4.jar` plus a separate `db2jcc_license_cu.jar` (or a vendor-specific license jar) for
  full functionality.
- Several vendors split a driver into a core jar and a separate globalization/i18n jar shipped alongside
  it rather than shaded into one uber-jar.

None of these are exotic — they're the normal shape of "download the JDBC driver" for engines this repo
already tracks as candidates (`jdbc-driver-support.md`'s own testing list includes Oracle). A single-path
field cannot express them, and there's no workaround today short of hand-shading a fat jar outside the
product, which defeats "a vendor jar is usable as shipped" (`jdbc-driver-support.md`'s own stated
default-path guarantee).

## Design

`java.net.URLClassLoader`'s constructor already takes an array of URLs and treats them as one combined
classpath — nothing about the current single-jar call is doing anything URLClassLoader itself can't
already do with more than one:

```csharp
// today, Imported/JdbcProviderFactory.cs
public static JdbcProviderFactory FromJarPath(string jarPath, string driverClass) =>
    FromJarUrl(new java.io.File(jarPath).toURI().toString(), driverClass);

public static JdbcProviderFactory FromJarUrl(string jarUrl, string driverClass) =>
    FromClassLoader(new java.net.URLClassLoader([new java.net.URL(jarUrl)]), driverClass);
```

becomes:

```csharp
public static JdbcProviderFactory FromJarPaths(IReadOnlyList<string> jarPaths, string driverClass) =>
    FromClassLoader(
        new java.net.URLClassLoader(
            jarPaths.Select(p => new java.net.URL(new java.io.File(p).toURI().toString())).ToArray()),
        driverClass);
```

`FromJarPath`/`FromJarUrl` (singular) can stay as thin one-element wrappers around this — call sites that
only ever had one jar (there are none outside this project yet) cost nothing extra either way. `FromClassLoader`
and everything downstream of it (`java.lang.Class.forName(driverClass, true, classLoader)`) needs no change:
class resolution already searches the whole combined classpath a `URLClassLoader` was built from, one jar
or several.

### Shapes touched

- `JdbcDriverSpec.DriverJarPath: string` → `DriverJarPaths: IReadOnlyList<string>`
  (`src/DbDataSync.Drivers.Jdbc/JdbcDriverSpec.cs`).
- `JdbcDescriptorYaml.DriverJarPath: string` → `DriverJarPaths: IReadOnlyList<string>`
  (`src/DbDataSync.Drivers.Descriptor/DriverDescriptorYaml.cs`). Entries are **names inside `files/`**
  (`user-provided-files-store.md`), not filesystem paths — resolved via `FilesPaths.FilePath(repoRoot,
  name)` at the point `JdbcGenericDriver`'s constructor builds the class loader, the same "id/name in, real
  path out" shape `LibraryRegistry.GetFactory` already uses for a library id. YAML becomes:
  ```yaml
  jdbc:
    driverClass: oracle.jdbc.OracleDriver
    driverJarPaths:
      - ojdbc8.jar
      - oraclepki.jar
      - osdt_cert.jar
      - osdt_core.jar
  ```
  The common single-jar case is a one-element list (`driverJarPaths: [postgresql-42.7.13.jar]`) —
  slightly more to type than today's `driverJarPath: ...`, accepted deliberately rather than supporting
  both a scalar and a list on the same field (two ways to write the same thing, with YamlDotNet's own
  scalar-or-sequence handling adding real complexity for a field that isn't shipped anywhere yet — see
  "Nothing external depends on today's shape" below).
- `JdbcProviderFactory.FromJarPath`/`FromJarUrl` → add `FromJarPaths` (above), taking **resolved**
  filesystem paths — the `files/`-name-to-path resolution happens one layer up, in
  `JdbcGenericDriver`/`JdbcGenericDriver.FromDescriptor`, so `JdbcProviderFactory` itself stays ignorant
  of where a jar came from, matching how it already has no notion of `libraries/` either. Existing
  `FromJarPath`/`FromJarUrl` stay as thin single-element convenience wrappers.
- `JdbcGenericDriver`'s constructor (`src/DbDataSync.Drivers.Jdbc/JdbcGenericDriver.cs:37`) —
  `JdbcProviderFactory.FromJarPath(spec.DriverJarPath, spec.DriverClass)` →
  `JdbcProviderFactory.FromJarPaths(spec.DriverJarPaths.Select(name => FilesPaths.FilePath(repoRoot, name)).ToList(), spec.DriverClass)`.
  `JdbcDriverSpec` itself keeps carrying **names**, not resolved paths — resolution happens at
  construction time, the one place that already knows `repoRoot` (see `JdbcGenericDriver.FromDescriptor`'s
  existing signature, which reads the descriptor but not yet `repoRoot` — that becomes a new parameter,
  the same way `DriverDescriptorReader.BuildDriver` already threads a `LibraryRegistry` through for the
  ADO.NET path's own artifact resolution).
- `JdbcGenericDriver.FromDescriptor` (same file, ~line 106–115) — passes `jdbc.DriverJarPaths` through
  instead of `jdbc.DriverJarPath`, and gains the `repoRoot` parameter above.

### Nothing external depends on today's shape

`DriverJarPath` is used in exactly four places outside its own definition — `JdbcGenericDriver.cs`,
`JdbcCatalogTests.cs`, `JdbcChangeDatabaseTests.cs`, `JdbcReaderParityTests.cs` (positional
`JdbcDriverSpec(...)` construction) — plus one YAML string in `JdbcDescriptorTests.cs`. This is still
unshipped (no `driver.yaml` in `drivers/` uses it, no released version depended on the field name), so
this is a rename, not a migration: no deprecation shim, no dual-field support.

## What this phase does not build

- The `files/` store itself (`GET`/`POST`/`DELETE /api/files`, the upload GUI) —
  `user-provided-files-store.md`'s own scope, a dependency of this phase, not part of it. This phase can
  still be built and tested with jars placed into `files/` by hand (matching how the test project's own
  `DownloadJdbcTestJar` MSBuild target already drops a jar into a known location without any UI).
- No validation that the jars listed are mutually compatible, or de-duplication if the same jar is
  named twice — `URLClassLoader` tolerates duplicates and irrelevant jars on its classpath without
  complaint, so there's nothing to add here.
- No UI/CLI for picking multiple jars beyond what `user-provided-files-store.md`/`driver-yaml-authoring-ui.md`
  cover — `jdbc-driver-feature-gaps.md` covers the wider "no operator-facing JDBC connection UI at all"
  gap this sits inside of.

## How to verify when built

- Existing single-jar tests (`JdbcCatalogTests`, `JdbcChangeDatabaseTests`, `JdbcReaderParityTests`,
  `JdbcDescriptorTests`) pass unchanged in substance — only the literal call/YAML shape moves from a
  string to a one-element list.
- A new test loading a driver class split across **two** jars — the cheapest real fixture is splitting
  pgJDBC's own classes isn't practical (it's already one jar), so this wants either two small
  hand-built throwaway jars (a driver class in jar A referencing a helper class in jar B) or downloading
  a real split driver (Oracle's wallet jars, license-gated — check availability before committing to
  this as the fixture). Whichever fixture, the assertion is the same: `Class.forName` resolves the
  driver class from jar A and a class it references resolves from jar B, proving the combined classpath
  works, not just that both files were read.
