# Phase 117 — bundled `KnownLibraries` and `KnownDrivers` catalogs

**Status**: Done.
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*The bundled catalogs*, Decisions 2 and 3. Depended on phase 116 (the `library` names and the
descriptor `library:` reference). No API or web surface — this is catalog data plus a CLI plumbing
change, so it lands independently of 118.

## What this built

### `KnownLibraries` (enriched, in `DbDataSync.Libraries`)

`LibraryCatalogEntry` record: `Id`, `PackageId`, `FactoryType`, `DisplayName`, `Description`,
`Vetted` (always `true`). The seven existing engines keep their exact factory-type strings; each got a
short kebab-case catalog id distinct from its package id (`mysql-connector` for `MySqlConnector`,
`microsoft-data-sqlclient` for `Microsoft.Data.SqlClient`, `npgsql`, `microsoft-data-sqlite`,
`oracle-managed-data-access`, `system-data-odbc`, `firebird-client`). `TryGet(packageId)` kept its exact
signature (used unchanged by `SetupCommand`); added `All` and `TryGetById(id)`.

### `KnownDrivers` (new, in `DbDataSync.Drivers.Descriptor`)

`KnownDriverEntry` record: `Id`, `DisplayName`, `BoundLibraryId`, `ResourceName`. One entry ships,
`mysql.generic`, bound to `mysql-connector`, its body embedded at
`Resources/mysql.generic.driver.yaml` (dialect + typeMap + capabilities, no id/displayName/library —
`KnownDrivers.Render` prepends those from the install itself). Seeded verbatim from the old
`DriverTemplates.MySql` body. `KnownDrivers` does not reference `DbDataSync.Libraries`'s types even
though the project already depends on it — the two catalogs stay decoupled at the type level;
`KnownDriversCatalogTests` is what checks a driver's `BoundLibraryId` actually resolves in
`KnownLibraries`.

### `config driver install --from <name>`

Reads `KnownDrivers` instead of a hardcoded switch. An unknown `--from` name lists every catalog id and
fails. No `--from` still writes `DriverTemplates.Minimal` (kept — `DriverTemplates.MySql` deleted,
its content moved into the embedded resource). `--from <name>` naming a real catalog entry defaults
`--library` to that entry's bound library id when `--library` is omitted.

### `config driver install` / `config library install` share one library-resolution shape

Both commands now resolve a library name against `KnownLibraries.TryGetById` first (a catalog
shorthand — fills in the real package id and factory type) before falling back to treating the given
name as a literal package id (the phase 116 behaviour, unchanged for that path). `config library
install <catalogId>` accepts a catalog id as its single positional argument; `--version` is still
required, `--factory-type` is not.

## How it was verified

- `dotnet build DbDataSync.slnx` clean.
- New `tests/DbDataSync.Drivers.Descriptor.Tests/KnownDriversCatalogTests.cs`: every `KnownDrivers`
  entry round-trips through `DriverDescriptorReader.Deserialize` + `ToSpec` (a stub `DbProviderFactory`)
  without error; every entry's `BoundLibraryId` resolves in `KnownLibraries`; every `KnownLibraries`
  entry's `FactoryType` splits into two non-empty comma-separated parts (a syntactic
  assembly-qualified-name check — the real assemblies aren't loaded in this test process, so this is
  as far as "well-formed" can be checked without a real install).
- New `tests/DbDataSync.Cli.Tests/DriverCommandTests.cs` (`Category=Integration` — real `dotnet
  publish` against the public feed): `config driver install my.mysql --version 2.4.0 --from
  mysql.generic` writes a `driver.yaml` whose body (everything after the id/displayName/library
  header) is byte-identical to a golden file checked into the test project
  (`tests/DbDataSync.Cli.Tests/Golden/mysql.generic.driver.yaml.body`) and installs `mysql-connector`;
  an unknown `--from` name lists `mysql.generic` in its error; installing from the catalog twice
  reuses the already-installed library on the second call.
- New `tests/DbDataSync.Libraries.Tests/LibraryCommandTests.cs` addition:
  `InstallByCatalogId_FillsPackageIdAndFactoryTypeFromTheCatalog` — `config library install
  mysql-connector --version 2.4.0` writes a manifest whose `Id` is the catalog id but whose package is
  the real `MySqlConnector`/`2.4.0`.
- Full `Category!=Integration` suite green (1041 tests across the solution); `DriverTemplates`'
  deletion of the `mysql` case didn't break any other CLI test (none referenced it directly —
  `SetupCommandTests`' MySQL-driver-step test faked the library installer, not the template).
- CLI smoke by hand against a scratch repo: `config library install mysql-connector --version 2.4.0`
  (catalog id), `config library list` (reports `resolves`), `config driver install foo --version 1.0.0
  --from bogus` (lists `mysql.generic`), `config driver install my.mysql --version 2.4.0 --from
  mysql.generic` with no `--library` (defaulted to `mysql-connector`, reused since already installed),
  `config driver list`.

## Decisions made

- **The catalog id for the one `KnownDrivers` entry is `mysql.generic`**, matching phase 120's own
  `POST /api/drivers/from-catalog { knownDriverId: "mysql.generic", ... }` example exactly — this
  phase's own "How to verify" draft had written `--from mysql` in two places (an old-template-name
  habit) while its "What this phase builds" section wrote `mysql.generic` for the same entry. Resolved
  in favor of `mysql.generic`, since that string is the one two other phase docs depend on literally;
  `--from mysql` would have been a dead end once 120 is built.
- **`KnownDrivers` embeds one `.yaml` resource per entry** (the "Open question" in this doc's original
  draft) — a future Oracle/Postgres-alike entry's diff won't touch the MySQL one.
- **No `connectionStringKeys` block added to the catalog** (the doc's other open question) — nothing
  needed one; `mysql.generic`'s SqlClient-shaped defaults are already confirmed to work empirically
  (109d/116's own test fixtures).

## What's explicitly out of scope / not built

- More than one `KnownDrivers` entry.
- Any API endpoint exposing the catalogs (118: `GET /api/known-libraries`, `GET /api/known-drivers`).
- The web "add" affordances (118 renders them disabled; 120 wires them).
- An operator-extensible catalog file — embedded only, per Decision 2.
