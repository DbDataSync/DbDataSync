# Phase 109d — the YAML driver descriptor (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*The descriptor*,
§*Worked examples*. Depends on 109a (string id), 109b (`GenericDriver`), 109c (provider layer).

## What this builds

A git-tracked `driver.yaml` that declares a SQL engine's dialect, binds it to a provider from 109c,
and stands up a `GenericDriver` for it — no C#. This is the phase that first lets an operator add a
whole engine to a running deployment.

### `<repo>/drivers/<id>/driver.yaml`

```yaml
id: mysql.generic
displayName: MySQL / MariaDB (generic)
provider:
  factoryType: "MySqlConnector.MySqlConnectorFactory, MySqlConnector"
  packages:
    - { id: MySqlConnector, version: "2.4.0" }
dialect:
  quoteIdentifier: backtick        # backtick | doubleQuote | bracket
  parameterPrefix: "@"
  rowLimit: limitOffset            # limitOffset | offsetFetch
  catalog: informationSchema       # informationSchema | (later: oracleAllTables | odbcGetSchema)
  supportsChangeDatabase: true
typeMap:                           # name (+ (p,s) args) -> canonical; unlisted -> Unmappable
  int:            Int32
  bigint:         Int64
  "decimal(p,s)": { kind: Decimal, precision: p, scale: s }
  "varchar(n)":   { kind: String, length: n, unicode: true }
  datetime:       Timestamp
  json:           Json
capabilities:
  readers: [Watermark, BatchReload]
  staging: [StagingTable]
  writers: [DeleteInsert]
```

### The deserialiser — `src/DbDataSync.Drivers.Generic/` (or a `DbDataSync.Drivers.Descriptor`)

- Parse `driver.yaml` into a `DriverDescriptor` record.
- Build a `DescriptorDialect : SqlDialect` from the `dialect` block:
  - `QuoteIdentifier` / `ParameterReference` from `quoteIdentifier` / `parameterPrefix`;
  - `RenderTieSafeRowLimit` from `rowLimit`;
  - `ToCanonicalType` = a name-keyed lookup over `typeMap` with `(p,s)` arg substitution, falling to
    `CanonicalTypeKind.Unmappable` — **strictly a table, no branching** (a `tinyint(1)`-style
    value-aware case is the signal to write a compiled driver instead, per the plan);
  - `RenderColumnType` = the reverse table, used only by provisioning, which is out of initial scope
    — so it may `throw NotSupported` for now.
- Resolve the provider via `ProviderRegistry.GetFactory(descriptor.Provider.FactoryType-or-id)`.
- Construct `new GenericDriver(spec)` with `catalog: informationSchema` → `InformationSchemaQueries`.

### Registration — `DriverRegistry` + both composition roots

- `DbDataSyncHost.cs` and `TaskRunner/Program.cs` already build a `DriverRegistry` and register the
  three built-ins. Add: enumerate `<repo>/drivers/*/driver.yaml`, deserialise, `registry.Register`.
- One shared routine (`DriverLoader.LoadDescriptorDrivers(repoRoot, providerRegistry)`) called from
  both — this is the "two composition roots" blocker; keep it one function.

### `GET /api/drivers` — `src/DbDataSync.Api/Controllers/DriversController.cs`

- Returns `[{ id, displayName, builtIn, source: "builtin" | "descriptor" }]` from the registry.
- `src/DbDataSync.Web/src/api/` — a `useDrivers()` query; the connection editor's driver `<select>`
  reads it instead of the hard-coded list from 109a.

### `dbdatasync driver …` — `src/DbDataSync.Cli/DriverCommand.cs`

- `driver install <id> --provider <packageId> [--from <template>]` — runs `provider install` for the
  package and drops a `driver.yaml` skeleton the operator fills in (`--from mysql` seeds a known
  starting dialect + typemap).
- `driver list` / `driver uninstall <id>`.

### Config validation

- `ConfigValidation` — a connection whose `driverType` is not a registered driver is rejected at
  save, message naming `driver install` (the 109a stub message becomes real here).

## What this phase does not build

- Provisioning (`CREATE`/`ALTER TABLE`) for a descriptor driver — `RenderColumnType` can throw.
- CDC / change-tracking readers, engine-specific writers — those are compiled drivers (109e).
- Oracle `ALL_*` or ODBC `GetSchema` catalog strategies — `informationSchema` only for now.
- A value-aware type map (expressions, conditionals). If an engine needs it, it gets a compiled
  driver.

## How to verify when built

- `dotnet build` + `npm run build`/`lint` clean; existing suite green.
- **New — end-to-end integration test** (`tests/DbDataSync.Api.Tests/DescriptorDriverTests.cs`,
  `Category=Integration`): with `MySqlConnector` installed and a `mysql.generic` `driver.yaml`,
  drive the real API to create a MySQL connection, define a mapping to a SQL Server target, run a
  watermark pass, and assert the target rows match the MySQL source. First "new engine, no rebuild."
- **New — `DescriptorDialectTests`** (unit): `typeMap` lookups including `decimal(p,s)` /
  `varchar(n)` arg substitution; an unlisted type → `Unmappable`; `rowLimit` / quoting rendering.
- **New — Playwright**: the connection editor's engine picker shows `MySQL / MariaDB (generic)` from
  `GET /api/drivers` once the descriptor is present.

## Open questions

- YAML schema for `typeMap` value forms — plain kind name vs. `{ kind, precision, scale, … }` — keep
  it minimal; document the closed set of `kind` values.
- Whether `driver.yaml`'s `provider` block should reference a `provider.json` id or inline the
  package list. Leaning: inline, and `driver install` writes both — one file to review per driver.
- `IValueBinding` for a descriptor driver: fall back to `System.Data.DbType` from the canonical kind.
  Acceptable loss for watermark/batch; note it in the descriptor docs.
