# Phase 168V — A shared `GenericDriverBase`, so `driver.yaml` can build either an ADO.NET or a JDBC driver

**Status**: Building.
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

(filled in as this phase is built)
