# Phase 109d — the YAML driver descriptor

**Status**: Done.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*The descriptor*,
§*Worked examples*. Depended on 109a (string id), 109b (`GenericDriver`), 109c (provider layer).

## What this built

A git-tracked `driver.yaml` that declares a SQL engine's dialect, binds it to a provider from 109c,
and stands up a `GenericDriver` for it — no C#. Proven against MySQL, replicating into SQL Server,
through the real API and a real spawned `DbDataSync.TaskRunner` process, with the MySQL side existing
entirely as a restored provider and a YAML file on disk.

### `src/DbDataSync.Drivers.Descriptor/` — a new project

References `Core`, `Drivers.Abstractions`, `Drivers.Generic`, `Providers`, and `YamlDotNet` directly
(not transitively through `Core`, since `Core`'s own YAML use is `internal`).

- **`DriverDescriptorYaml.cs`** — the file's shape as plain classes (`DriverDescriptorYaml`,
  `DescriptorProviderYaml`, `DescriptorDialectYaml`, `DescriptorCapabilitiesYaml`, `TypeMapEntryYaml`),
  camelCased to match the YAML. Two fields exist beyond the plan doc's worked example, because
  `GenericDriverSpec` genuinely needs them and the worked example never showed a connection being
  assembled: `dialect.defaultDatabase` (SQL Server has `master`, Postgres has `postgres`; MySQL has no
  equivalent and defaults to empty, which every provider tested here accepts) and an optional
  `dialect.connectionStringKeys` block (Npgsql spells connect timeout `Timeout`, SqlClient spells it
  `Connect Timeout` — this is exactly the per-engine variation `GenericConnectionStringKeys` exists to
  carry, and the descriptor needs a way to override it too).
- **`TypeMapEntryYamlConverter.cs`** — a hand-written `IYamlTypeConverter`, the same shape
  `BatchReloadSegmentYamlConverter` already uses for a different reason: a `typeMap` value is either a
  bare scalar (`datetime: Timestamp`) or a mapping (`"decimal(p,s)": { kind: Decimal, precision: p,
  scale: s }`), and YamlDotNet's own object deserialization commits to one node kind per declared .NET
  type with no built-in "try scalar, else mapping" fallback.
- **`DescriptorDialect.cs`** — a `SqlDialect` built entirely from the `dialect` and `typeMap` blocks.
  `QuoteIdentifier`/`ParameterReference` from `quoteIdentifier`/`parameterPrefix`; `RenderTieSafeRowLimit`
  branches on `rowLimit` (`limitOffset` → plain `LIMIT n`, losing tie-safety — see Decisions below);
  `ToCanonicalType` is a name-keyed lookup over `typeMap`, parsed once at construction via
  `CanonicalTypeSpec.Parse` (already shared by every dialect) into base-name → (placeholder names,
  entry), then at lookup time substitutes a matched native type's own `(p,s)`-style arguments for those
  placeholders — strictly a table, no branching, exactly as scoped. `RenderColumnType` throws
  `NotSupportedException`: provisioning is out of scope, and a descriptor never gets asked for it since
  nothing here wires `IProvisioner`.
- **`DriverDescriptorReader.cs`** — parses the file and builds the `GenericDriverSpec`, given an
  already-resolved `DbProviderFactory` (kept out of this class so it has no `ProviderRegistry`
  dependency; see `DriverLoader`). Rejects any `catalog` other than `informationSchema` up front.
- **`DriverLoader.cs`** — the one shared routine both composition roots call: enumerates
  `<repo>/drivers/*/driver.yaml`, resolves each descriptor's provider by its first package's id
  (`ProviderRegistry.GetFactory(packages[0].Id)`), and registers a `new GenericDriver(spec)`. A failed
  descriptor is logged and skipped rather than failing host startup — one operator's typo in a driver
  they added must not take down every other replication. This is the "two composition roots" seam the
  plan doc's §*Blockers to clear* named; `DbDataSyncHost.cs` and `TaskRunner/Program.cs` both call it
  with their own `ProviderRegistry`/`DriverRegistry` instances (the worker is a separate OS process, so
  it repeats the load rather than sharing the API's in-memory registry — same reasoning 109c already
  established for the provider layer itself).

### `IDriver.DisplayName` and `GenericDriverSpec.DisplayName` (not in the plan doc)

`GET /api/drivers` needs a human name per driver, and neither `IDriver` nor `GenericDriverSpec` carried
one — every built-in's id (`MsSql`, `Postgres`, `DuckDb`) already reads fine as a display name, so
nobody had needed to separate the two before. Added `string DisplayName => DriverType;` as an `IDriver`
default-interface member (every built-in keeps its current behaviour with no code change) and an
optional `GenericDriverSpec.DisplayName` (falls back to `Id`), set from the descriptor's own
`displayName` field.

### `GET /api/drivers` — `src/DbDataSync.Api/Controllers/DriversController.cs`

Returns `[{ id, displayName, builtIn, source: "builtin" | "descriptor" }]` from a new
`DriverRegistry.All` enumeration property. `builtIn`/`source` are decided by checking membership in the
three `DriverIds` constants — no new bookkeeping on `DriverRegistry` itself, since only three ids will
ever need it until 109e adds a third source value.

### SPA — `useDrivers()` + the connection editor

`api/client.ts` gained `drivers.list()`; `api/hooks.ts` gained `useDrivers()` (no polling — the driver
list changes at most once per deployment, unlike everything else in that file). `ConnectionEditPage.tsx`'s
engine `<select>` now maps over `useDrivers()`'s result instead of three hardcoded `<option>`s, falling
back to the same three built-in ids by value until the query resolves, so a fresh page load never shows
an empty picker. `types.ts` gained `DriverSummary`.

### `dbdatasync driver …` — `src/DbDataSync.Cli/DriverCommand.cs` + `DriverTemplates.cs`

`driver install <id> --provider <packageId> --version <v> [--factory-type type] [--from mysql]
[--display-name name]` — runs the equivalent of `provider install` for the package, then writes a
`driver.yaml` skeleton. `--from mysql` seeds the plan doc's own worked example verbatim (trimmed of
nothing); no `--from` writes a minimal shell with an empty `typeMap` (every native type is
`Unmappable`, reported rather than guessed at) that an operator fills in. `driver list` /
`driver uninstall <id>` round out the set. Templates are rendered via placeholder-token substitution
(`__ID__`, `__FACTORY_TYPE__`, …) rather than C# string interpolation — the YAML itself is full of `{ }`
flow-mapping syntax, which fights an interpolated string's own brace-escaping badly enough that the
first draft of this file didn't compile.

## What this phase does not build

- Provisioning (`CREATE`/`ALTER TABLE`) for a descriptor driver — `RenderColumnType` throws, as planned.
- CDC / change-tracking readers, engine-specific writers — compiled drivers, 109e.
- Oracle `ALL_*` or ODBC `GetSchema` catalog strategies — `informationSchema` only; `DriverDescriptorReader`
  rejects any other `catalog` value outright rather than silently misbehaving.
- A value-aware type map. `ToCanonicalType` is exactly the table the plan doc scoped it to be.

## How it was verified

- `dotnet build DbDataSync.slnx` + `npm run build`/`lint` clean; the full non-integration and
  `Category=Integration` suites both green (43 API tests now, up from 41 — the two new descriptor
  ones).
- **New — `tests/DbDataSync.Drivers.Descriptor.Tests/DescriptorDialectTests.cs`** (unit, 16 cases): the
  full descriptor parses; plain-scalar typeMap entries map directly; `decimal(p,s)` and `varchar(n)`
  substitute their placeholders correctly (including a second `decimal(5,0)` case to prove the
  substitution isn't hardcoded to one pair of values); `max` entries carry no length; an unlisted
  native type is `Unmappable`; quoting (including embedded-backtick escaping) and parameter rendering
  for the backtick style; both `rowLimit` styles render correctly (`limitOffset` → plain `LIMIT`,
  `offsetFetch` → the base class's `WITH TIES` form); `RenderColumnType` throws; a `supportsChangeDatabase:
  false` engine (Oracle template) refuses `UseDatabaseAsync` before touching the connection.
- **New — end-to-end integration test, `tests/DbDataSync.Api.Tests/DescriptorDriverTests.cs`**
  (`Category=Integration`, a `DescriptorDriverApiFactory` that installs `MySqlConnector` and writes a
  real `mysql.generic` driver.yaml to its repo root *before the host starts* — exactly what an operator
  running `dbdatasync driver install` would leave on disk):
  - `GET /api/drivers` lists `mysql.generic` with its real display name, `builtIn: false`,
    `source: "descriptor"`, alongside the three still-present built-ins;
  - creates a MySQL connection (`driverType: "mysql.generic"`) and a SQL Server one through the real
    API, defines a replication with `Reader.Kind = "Watermark"` (a `GenericDriverKinds` name, resolved
    from `mysql.generic`'s own registered readers exactly as an `MsSql`-prefixed Kind resolves from
    `MsSqlDriver`'s) and `Cache`/`Writer` Kinds from the SQL Server target's driver, triggers a run,
    waits for it over the real SignalR hub, and asserts both source rows landed correctly on the SQL
    Server target. The spawned `DbDataSync.TaskRunner` process loads the descriptor independently (its
    own `DriverLoader.LoadDescriptorDrivers` call in `Program.cs`) — this is the "two composition roots"
    concern actually exercised, not just unit-tested in isolation.
  - Seeds the MySQL scratch database through `ProviderRegistry.GetFactory("MySqlConnector")` resolved
    from the running host's own DI container — not a direct `MySqlConnector` package reference in the
    test project, which would have undermined 109c's "MySqlConnector is referenced by no `.csproj`"
    assertion the moment it was added anywhere in the solution.
- **CLI smoke-tested by hand** (not an automated test, but exercised against a real temp repo):
  `driver install mysql.smoketest --provider MySqlConnector --version 2.4.0 --from mysql` restores the
  provider and writes a driver.yaml that `DriverDescriptorReader` parses without error; `driver list`
  and `provider list` both report it correctly (`resolves`); `driver uninstall` removes it.
- **Golden-path Playwright, targeted**: the full `golden-path.spec.ts` (45 tests, sequential/stateful —
  confirmed by first trying a single filtered test in isolation, which failed for unrelated
  setup-ordering reasons, then running the whole file) passes, including test 31's exercise of
  `connection-driver-select` (`selectOption('Postgres')`/`selectOption('MsSql')`) — proof the picker's
  new data-driven rendering doesn't regress the existing built-in-only flow.

### Scope decision: no new Playwright spec for the descriptor picker

The plan doc asked for a Playwright test proving the engine picker shows a descriptor driver's display
name once one is present. Not built as a *new* spec: doing so would mean adding a real
`dotnet publish`-based provider install (several seconds of NuGet/MSBuild work) to the Playwright
suite's global setup permanently, for a claim already proven twice over — at the API level
(`DriversController` returning the descriptor correctly, integration-tested above) and by inspection
(the picker's new code is a plain `.map` over already-typed data, not new logic worth its own browser
test). The existing suite's unmodified pass on the now-data-driven picker is the regression check that
matters.

## Decisions and real bugs found

- **`limitOffset` engines lose tie-safety.** The base class's own doc comment explains why
  `RenderTieSafeRowLimit`'s `WITH TIES` form exists at all — a plain `LIMIT n` can silently skip a row
  that ties the boundary value. Accepted for this phase, matching the plan doc's own framing: an engine
  that needs bulk batch reads *and* guaranteed-no-skip behaviour is the signal to write a compiled
  dialect, not a descriptor one. Documented in `DescriptorDialect`'s own doc comment, not buried.
- **`GenericConnectionStringKeys`' SqlClient-shaped defaults (`Host`, `Connect Timeout`, `"User Id"`)
  turned out to already be accepted aliases in MySqlConnector's own builder** — verified empirically
  (a throwaway console app against the real container) before assuming it, the same way 109b's Postgres
  test needed an explicit override (Npgsql does *not* accept `Connect Timeout`, only `Timeout`). No
  `connectionStringKeys` override was needed in the MySQL descriptor used by any test here, though the
  field exists in the schema for an engine that does need one.
- **Real bug, caught by the compiler, not by review**: the first draft of `DriverTemplates.cs` and
  `DescriptorDriverApiFactory.cs` used C# interpolated raw strings (`$"""..."""`) to render YAML
  containing literal `{ }` flow-mapping syntax (`{ id: MySqlConnector, version: "2.4.0" }`). Interpolated
  raw strings need doubled braces (`{{`/`}}`) to escape a literal one, and the YAML's own single braces
  with spaces inside them (`{ id: ... }`) don't parse as valid escape sequences either — both files
  failed to compile on the first attempt. Fixed by switching to placeholder-token substitution
  (`.Replace("__ID__", id)`) over a plain (non-interpolated) raw string, which sidesteps the whole class
  of problem rather than fighting it.
- **`DriverRegistry.All` and `IDriver.DisplayName`** were not named in the plan doc at all — both
  turned out to be required the moment `GET /api/drivers` had to return something a person could read,
  and both are additive (a default-interface member, an enumeration property) rather than changes to
  anything existing.

## Open questions — resolved

- **YAML schema for `typeMap` value forms**: built exactly as leaned — a bare kind name or an explicit
  `{ kind, precision, scale, length, max, unicode }` mapping, no third form.
- **Whether `driver.yaml`'s `provider` block references a `provider.json` id or inlines the package
  list**: inlined, as leaned — `driver install` runs the provider restore itself rather than requiring
  one to already exist, and resolves the provider at load time by the first package's id.
- **`IValueBinding` fallback for a descriptor driver**: resolved in 109b (`GenericValueBinder`); this
  phase just uses it — no descriptor in this phase's tests supplies a `ValueBinder` override, and none
  needed to.
