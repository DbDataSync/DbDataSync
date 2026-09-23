# Phase 168V — A shared `GenericDriverBase`, so `driver.yaml` can build either an ADO.NET or a JDBC driver

**Status**: Built.
**Plan reference**: `architecture/implementation/todo/phase-167V-jdbc-metadata-databasemetadata-and-generic-descriptor.md`
(items 1 and 2 of its own design, deferred there — this phase carries them out, differently than
originally sketched, after a design conversation found a simpler shape), `architecture/implementation/done/phase-165V-jdbc-reader-spike-ikvm-postgres.md`
(`JdbcDriver`, renamed and refactored here), `architecture/implementation/done/phase-109d-the-yaml-descriptor.md`.

## Why phase 167V's own sketch for this was wrong

167V considered adding `"databaseMetaData"` as a third case in `driver.yaml`'s `catalog` string, next to
`"informationSchema"`/`"query"`. That doesn't work: `DriverDescriptorReader` (in
`DbDataSync.Drivers.Descriptor`) would need to reach `JdbcCatalog`, `internal` to
`DbDataSync.Drivers.Jdbc`, which `Descriptor` doesn't reference. The deeper problem isn't visibility,
though — it's that the catalog strategy and the connection-creation mechanism (ADO.NET `DbProviderFactory`
vs. IKVM) aren't independent. One string switch was trying to cover two axes.

## The shape

```csharp
// DbDataSync.Drivers.Generic — shared
public interface IGenericDriverSpec
{
    string Id { get; }
    SqlDialect Dialect { get; }
    IDescriptorCatalog Catalog { get; }
    IReadOnlyList<string> Readers { get; }
    IReadOnlyList<string> Staging { get; }
    IReadOnlyList<string> Writers { get; }
    ISegmentValueBinder? ValueBinder { get; }
    string? DisplayName { get; }
}

public abstract class GenericDriverBase<TSpec>(TSpec spec, ISegmentValueBinder binder)
    : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider
    where TSpec : IGenericDriverSpec
{
    // Readers/StagingProviders/Writers construction, ListTablesAsync/ListColumnsAsync via spec.Catalog,
    // TestAsync (`SELECT 1`) — everything GenericDriver already has that doesn't touch how a connection
    // gets made. CreateConnection/ListDatabasesAsync/SwitchDatabaseAsync stay abstract/overridable —
    // ADO.NET's DbConnectionStringBuilder assembly and JDBC's IKVM one have nothing in common.
}

public sealed record GenericDriverSpec(..., DbProviderFactory ProviderFactory, ...) : IGenericDriverSpec;
public sealed class GenericDriver(GenericDriverSpec spec) : GenericDriverBase<GenericDriverSpec>(spec, ...)
{
    public static IDriver FromDescriptor(DriverDescriptorYaml d, LibraryRegistry libraries) => ...;
}
```

```csharp
// DbDataSync.Drivers.Jdbc — JdbcDriver renamed and refactored onto the same base
public sealed record JdbcDriverSpec(..., string DriverClass, string DriverJarPath) : IGenericDriverSpec;
public sealed class JdbcGenericDriver(JdbcDriverSpec spec) : GenericDriverBase<JdbcDriverSpec>(spec, ...)
{
    public static IDriver FromDescriptor(DriverDescriptorYaml d, LibraryRegistry libraries) => ...;
}
```

`driver.yaml` names which one to build, as a type name — the same shape `library.json`'s `FactoryType`
already uses:

```yaml
id: mysql.generic
# base omitted — GenericDriver is the default, so every existing driver.yaml keeps working unchanged
dialect:
  catalog: query
metadataQueries: { tableQuery: ..., columnQuery: ... }
```
```yaml
id: postgres-via-jdbc
base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc
jdbc:
  driverClass: org.postgresql.Driver
  driverJarPath: /path/to/postgresql.jar   # a literal path — real artifact resolution (jars/ vs
                                            # libraries/) is still jdbc-driver-support.md's own open
                                            # question, not solved here
# dialect.catalog omitted → JdbcCatalog.Instance (DatabaseMetaData) is the default
typeMap:
  serial: Int32
  int4: Int32
```

`DriverDescriptorReader.BuildDriver` resolves `Base` via `Type.GetType` + reflection on a conventional
`public static IDriver FromDescriptor(DriverDescriptorYaml, LibraryRegistry)`, falling straight through
to `GenericDriver.FromDescriptor` when `Base` is omitted. `catalog: query`'s resolution
(`DescriptorCatalogResolution`) is shared, called by both `FromDescriptor` methods with their own default
(`InformationSchemaQueries`/`JdbcCatalog.Instance`).

## What this needs that isn't just the class hierarchy

`DbDataSync.Drivers.Descriptor` gets a `ProjectReference` to `DbDataSync.Drivers.Jdbc` — needed so
`Type.GetType("...JdbcGenericDriver, DbDataSync.Drivers.Jdbc")` can actually find the assembly at
runtime. This is transitive: `DbDataSync.Api`/`DbDataSync.TaskRunner` already reference `Descriptor`, so
neither composition root needs touching directly. It does reverse part of phase 165V's own scoping
("never registered in BuiltInDrivers/DbDataSyncHost/TaskRunner's composition roots... deliberately not
reachable") — deliberately, here: JDBC becomes reachable *if a `driver.yaml` names it*, not enabled by
default the way `BuiltInDrivers.All` is (JDBC still isn't in that list — it can't be, it takes a
per-vendor driver class and jar, not a parameterless constructor).

## What this does not solve

- Jar/artifact resolution for a shipped JDBC engine — `driverJarPath` is a literal filesystem path, the
  same shape the test project's own MSBuild-downloaded jar already is. `jars/` vs `libraries/` stays open.
- `databaseMetaData` catalog/schema semantics per vendor — unchanged from phase 167V: `JdbcCatalog`'s
  `DatabaseMetaData`-based answer is the default; `catalog: query`/`metadataProvider` stay the escape
  hatches for a vendor it doesn't fit.

## How to verify when built

- Every existing `GenericDriver`/`GenericDriverSpec` test passes unchanged (behavior-preserving refactor).
- A new descriptor-level test: a `postgres-via-jdbc`-shaped YAML with `base: ...JdbcGenericDriver...`
  round-trips through `DriverDescriptorReader.BuildDriver` to a real `JdbcGenericDriver` reading actual
  rows from the live Postgres container — the same parity shape phase 165V's own tests already use.
- Full existing suite green.

---

# Retrospective

## What shipped, and how it differs from the sketch above

The sketch's `GenericDriverBase<TSpec>(TSpec spec, ISegmentValueBinder binder)` primary constructor,
`IGenericDriverSpec`, `DescriptorCatalogResolution`, and the two concrete kinds (`GenericDriver`/
`GenericDriverSpec` refactored, `JdbcGenericDriver`/`JdbcDriverSpec` new) all shipped essentially as
drawn. Two things the sketch didn't get right:

- **`FromDescriptor` can't live where the sketch put it for `GenericDriver`.** `DbDataSync.Drivers.Generic`
  cannot reference `DbDataSync.Drivers.Descriptor` (wrong direction — Descriptor already depends on
  Generic). `GenericDriver.FromDescriptor(DriverDescriptorYaml, ...)` as sketched would have required
  exactly that. The ADO.NET path's construction logic stayed where it already lived —
  `DriverDescriptorReader.ToSpec`, in `Descriptor` — with a new `BuildDriver` alongside it doing the
  dispatch. `JdbcGenericDriver.FromDescriptor` *can* live in `DbDataSync.Drivers.Jdbc`, because that
  project can reference `Descriptor` one-directionally (for `DriverDescriptorYaml`) without creating a
  cycle — `Descriptor` never references `Jdbc` back; it finds `JdbcGenericDriver` at runtime via
  `Type.GetType`, which only needs the assembly loadable, not a compile-time reference. The two kinds'
  factories ended up in different projects; the reflection dispatch is what makes that invisible to the
  caller.
- **`DescriptorDialect` needed one real addition before JDBC could reuse it**: `ParameterNameIsBare` (see
  its own doc comment) — without it, a JDBC-via-descriptor engine would have hit phase 165V's own Finding
  1 again, for the identical reason. Found by working through the design, not by running anything broken
  first — the one case in this phase where the gap was caught before code, not after.

## Found by writing the tests, not assumed

- **`catalog:` becoming optional broke three places that still wrote `catalog: informationSchema`
  explicitly**: the shipped `mysql.generic.driver.yaml` resource, the CLI's `driver install` starter
  template (`DriverTemplates.cs`), and `DescriptorDriverApiFactory`'s real end-to-end test fixture (a
  live MySQL container, through the actual HTTP API). All three now omit `catalog:` — the fix and the
  better demonstration of "the common case needs nothing" at once. `KnownDriversCatalogTests` and
  `DescriptorDriverTests` both catching this is exactly why those tests exist.
- **The end-to-end JDBC descriptor test's first draft set `supportsChangeDatabase: false`**, on the
  assumption that a JDBC connection never switches database. It failed immediately:
  `BatchReloadReader.ReadChangesAsync` calls `dialect.UseDatabaseAsync` unconditionally — a call the
  *reader* makes directly, independent of `JdbcGenericDriver`'s own `SwitchDatabaseAsync` no-op (which
  only covers the *driver's* `ListTablesAsync`/`ListColumnsAsync`, mirroring what the original hand-written
  `JdbcDriver` already did). The already-proven behavior (phase 165V's own passing tests, using a
  hand-written `JdbcDialect` that never overrode `UseDatabaseAsync` at all) is that this resolves to
  `JdbcConnection.ChangeDatabase` → `java.sql.Connection.setCatalog(...)`, which pgJDBC accepts. Fixed the
  test's assumption, not the code — `supportsChangeDatabase: true` (the default) is correct and now stated
  as such, with the reasoning inline rather than left for the next person to rediscover.

## What this resolves from phase 167V's own deferred list

Item 1 (`databaseMetaData` as a `driver.yaml` catalog strategy) and item 2 (JDBC as a `GenericDriverSpec`
descriptor with its own `typeMap`) are both done — differently than 167V sketched, via a shared base
rather than a wider `catalog` string. `JdbcCatalog` stays the default for a `JdbcGenericDriver`-based
descriptor with no `catalog:` override; a real `typeMap` (proven against `int4`/`varchar(n)` in the
end-to-end test) replaces the single hardcoded, Postgres-scoped `JdbcDialect.ToCanonicalType`.

## What's still open

- **Whether the hand-written `JdbcDialect`/`JdbcCatalog` path should eventually be retired in favor of
  always going through a descriptor** — moved to its own follow-up:
  `architecture/planning/done/follow-up-phase-168-hand-written-jdbc-path-vs-descriptor.md`. Both paths
  work, side by side, today.
- **Jar/artifact resolution remains a literal filesystem path** (`driverJarPath`), same as before this
  phase and same as the test project's own MSBuild-downloaded jar. `jars/` vs `libraries/` is still
  `jdbc-driver-support.md`'s own open question.
- **No second JDBC-backed vendor descriptor exists yet** (only the one built for this phase's own test).
  Standing up a real `oracle-via-jdbc.driver.yaml` or similar, with its own `typeMap`, would be the next
  real test of whether the `typeMap`-per-vendor shape actually holds up outside Postgres.

## Verification

Full solution builds clean throughout. `GenericDriverTests` (9 tests, against a live Postgres container,
exercising `GenericDriver` end to end) unchanged and green — confirms the base-class extraction is
behavior-preserving. `DescriptorDriverTests` (Api.Tests, a real `mysql.generic` descriptor through the
actual HTTP API against a live MySQL container) green — confirms the default (no `base`) path is
unbroken end to end, not just at the unit level. Jdbc (5, including the new end-to-end descriptor test),
Generic (204), Descriptor (32) all green against live containers.
