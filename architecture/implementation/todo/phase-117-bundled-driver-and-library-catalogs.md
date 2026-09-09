# Phase 117 — bundled `KnownLibraries` and `KnownDrivers` catalogs (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*The bundled catalogs*, Decisions 2 and 3. Depends on phase 116 (the `library` names and the
descriptor `library:` reference). No API or web surface — this is catalog data plus a CLI plumbing
change, so it can land independently of 118.

## Why

`KnownProviderFactories` today is a 7-entry `id → factoryType` guess table. `DriverTemplates.MySql`
is a one-off `driver.yaml` string reachable only through `config driver install --from mysql`, not
validated by CI, living in the CLI project where the API can't see it. To make a one-click "add
MySQL / MariaDB" possible in the web UI (phase 120), both need to become real, validated, shared
catalogs: a curated set of libraries, and a curated set of ready-made descriptor bodies bound to
them.

## What this phase builds

### `KnownLibraries` (enriched, in `DbDataSync.Libraries`)

Each entry grows from `id → factoryType` to a record: `id`, `packageId`, `factoryType`,
`displayName`, `description` (one line), `vetted` (always `true` for a bundled entry — the field
exists so the web UI can render "vetted" vs. "you found this" without special-casing). The seven
existing engines keep their exact factory-type strings. `KnownLibraries.TryGet(packageId)` stays for
the CLI's existing factory-type guess; add `All` and `TryGetById(id)` for the catalog consumers.

### `KnownDrivers` (new, in `DbDataSync.Drivers.Descriptor`)

Placed here, not in the CLI, so it can validate its own entries against `DriverDescriptorReader`.
Each entry:

- a stable `id` (`mysql.generic`), a `displayName`;
- the descriptor body — `dialect` + `typeMap` + `capabilities` — as an **embedded `.yaml` resource**
  (`Resources/mysql.generic.driver.yaml`), minus the `id`/`displayName`/`library` lines, which the
  installer fills in;
- the `KnownLibraries` id it binds (`mysql-connector`).

v1 ships **one** entry, `mysql.generic`, seeded from the current `DriverTemplates.MySql` body. (See
Decision 3 in the plan doc for why not SQLite/Postgres-alike/Oracle yet.)

### `config driver install --from <name>`

Reads `KnownDrivers` instead of `DriverTemplates`' hardcoded `switch`. `--from` with an unknown name
lists the catalog ids. No `--from` still writes `DriverTemplates.Minimal` (kept). `--from <name>`
with no `--library` uses the catalog entry's bound library id and installs it if absent.

### `config library install <catalogId>`

Accept a `KnownLibraries` id (`mysql-connector`) as shorthand: fills `packageId` + `factoryType`
from the catalog, still requires `--version` (the "pinned, never latest" rule holds).

## How to verify when built

- New `tests/DbDataSync.Drivers.Descriptor.Tests/KnownDriversCatalogTests.cs`: every `KnownDrivers`
  entry round-trips through `DriverDescriptorReader.Read` + `ToSpec` (with a stub `DbProviderFactory`)
  without error — a bundled descriptor can't rot; every entry's `library` id exists in
  `KnownLibraries`; every `KnownLibraries` entry's `factoryType` is a well-formed
  assembly-qualified name.
- `config driver install --from mysql` produces a `driver.yaml` byte-identical (modulo the
  `id`/`displayName`/`library` lines) to a golden file checked into the test project.
- Full `Category!=Integration` suite green; `DriverTemplates` deletion doesn't break any CLI test
  (they move to asserting against `KnownDrivers`).
- CLI smoke: `config driver install my.mysql --from mysql` on a clean temp repo installs
  `mysql-connector` and writes a loadable descriptor; `config library install mysql-connector
  --version 2.4.0` works by catalog id.

## What this phase does not build

- More than one `KnownDrivers` entry. The structure is built to grow; the entries aren't the point.
- Any API endpoint exposing the catalogs (118: `GET /api/known-libraries`, `GET /api/known-drivers`).
- The web "add" affordances (118 renders them disabled; 120 wires them).
- An operator-extensible catalog file — Decision 2: embedded only.

## Open questions to resolve during implementation

- Whether `KnownDrivers` bodies are one embedded `.yaml` per entry or a single embedded document.
  Leaning: one file per entry — a diff to the MySQL type map shouldn't touch an Oracle one.
- Whether to also carry a per-entry `connectionStringKeys` block in the catalog (the generic
  defaults are SqlClient-shaped; MySQL happens to accept them — phase 109d's finding). Leaning: only
  add it to an entry that provably needs it, as 109d did.
