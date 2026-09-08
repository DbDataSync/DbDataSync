# Phase 109b — a public GenericDriver (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md`. Depends on 109a (string
driver id). Nothing depends on 109b until 109d, but it de-risks that phase by landing the reusable
core on its own with its own tests.

## What this builds

`PostgresDriver` already demonstrates that a new engine is "a dialect, a connection factory and a
catalog" — its own doc comment says so, and it registers nothing but `DbDataSync.Drivers.Generic`
components. This phase extracts that shape into a class anyone can construct:

```csharp
public class GenericDriver : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider
{
    public GenericDriver(GenericDriverSpec spec);
    // spec: driver id, SqlDialect, DbProviderFactory, ITableCatalog,
    //       IValueBinding, which generic Kinds to expose, default port, connection-param shape
}
```

- **Readers/StagingProviders/Writers** are built from `spec` — `WatermarkReader`, `BatchReloadReader`,
  `BatchInsertStagingProvider`, `DeleteInsertWriter`, `SnapshotWriter`, `Scd2Writer`,
  `TriggerAuditReader` — each already takes `(SqlDialect, ITableCatalog, IValueBinding)`. The spec
  says which subset to register.
- **`CreateConnection`** uses `spec.ProviderFactory.CreateConnection()`, sets `ConnectionString` from
  a `DbConnectionStringBuilder`-based assembly of host/port/database/properties + the resolved
  credential, and applies the command timeout. The per-engine connection-string *keys* (Npgsql's
  `Timeout` vs SqlClient's `Connect Timeout`) come from `spec`, not from provider-typed builders.
- **Catalog methods** delegate to `spec.Catalog` — `InformationSchemaQueries` for the portable case,
  a different `ITableCatalog` for Oracle/ODBC (109d's problem, not this one).
- **`IProvisioner`** is *not* on `GenericDriver` in this phase — `CreateTargetTablePlanner` /
  `AlterTargetTablePlanner` are generic and could be wired later, but provisioning is out of the
  descriptor's initial scope.

### Where it lives

`src/DbDataSync.Drivers.Generic/GenericDriver.cs` — it belongs with the components it composes, and
`Generic` already references `Abstractions` and `Core`. (`Generic` also transitively pulls
`Scripting.Abstractions`; if that turns out to matter for a plugin author referencing this type,
split `GenericDriver` + its spec into a leaf `DbDataSync.Drivers.Generic.Core` — an open question,
not a blocker.) `PostgresDriver` can then be re-expressed as
`new GenericDriver(PostgresGenericSpec)` for its generic Kinds, keeping only what is genuinely
Postgres-specific (the row estimator, the provisioner) — but that rewrite is optional and can be a
follow-up; this phase only has to *add* `GenericDriver`, not adopt it.

## What this phase does not build

- The descriptor (`driver.yaml`) or its deserialiser — 109d.
- The provider layer — 109c. This phase's tests construct `GenericDriver` with
  `NpgsqlFactory.Instance` directly, because `Npgsql` is still a hard reference.
- Rewiring `MsSqlDriver`/`PostgresDriver` to use it (allowed as a follow-up, not required).

## How to verify when built

- `dotnet build` clean; existing suite green (nothing adopted it, so nothing moved).
- **New — `tests/DbDataSync.Drivers.Generic.Tests/GenericDriverTests.cs`** (`Category=Integration`,
  against the existing `postgres` container):
  - construct `new GenericDriver` with `PostgresDialect.Instance` + `NpgsqlFactory.Instance` +
    `PostgresCatalog.Instance`;
  - `ListTablesAsync` / `ListColumnsAsync` return the same shape `PostgresDriver` does for a seeded
    table;
  - a watermark read after an insert yields the row and advances the position;
  - a `BatchReload` + `DeleteInsert` round-trip lands the source rows on a target table.
- The assertion that matters: this is byte-identical behaviour to `PostgresDriver`'s generic Kinds,
  proving the extraction lost nothing.

## Open questions

- `GenericDriverSpec` as a record vs. a builder. Leaning: a record, with a couple of static factories
  (`GenericDriverSpec.InformationSchema(id, dialect, factory)`) for the common shape.
- Whether `IValueBinding` (typed segment-bound binding) can fall back to a generic
  `System.Data.DbType` mapping when a descriptor supplies no per-type binding — needed for 109d,
  worth prototyping here.
