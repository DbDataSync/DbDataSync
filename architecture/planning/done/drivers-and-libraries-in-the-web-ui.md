# Drivers and libraries in the web interface

**Resolved 2026-09-09.** The design is agreed and split into phases —
`architecture/implementation/todo/phase-116` through `phase-122`:

| phase | what |
| --- | --- |
| **116** | rename `provider` → `library`; a `driver.yaml` descriptor references its library by id |
| **117** | bundled `KnownLibraries` / `KnownDrivers` catalogs; `--from` reads them |
| **118** | read-only Drivers and Libraries screens under Admin; `GET /api/libraries`, extended `GET /api/drivers` |
| **119** | NuGet search proxy (`GET /api/libraries/search`) + the search box |
| **120** | web-triggered install / remove; the default Docker image moves to an SDK base |
| **121** | *(follow-on)* a runtime-only image with a pre-built catalog cache |
| **122** | *(follow-on)* `factoryType` reflection-assist for non-catalog libraries |

The rest of this document is the thinking that produced that split — kept for the rationale and the
recorded decisions; the buildable design lives in the phase docs.

---

Scope: make the drivers an operator can use, and the ADO.NET packages behind them, **visible and
manageable from the console** — not only from `dbdatasync config driver` / `config provider` and a
text editor. Along the way, fix the naming ("provider" is overloaded and barely user-facing) and give
both layers a bundled catalog of vetted, ready-to-use entries.

The mechanism already exists — phase 109a–109f built `GET /api/drivers`, the descriptor loader, the
compiled-plugin loader, and the provider layer. This is about exposing it, renaming it, and adding
the curated data that makes a one-click "add MySQL" possible.

Read `architecture/planning/todo/nuget-loaded-drivers.md` first — the parent design. Its
§*Security and trust*, §*Acquisition*, and §*Assembly loading design* apply here unchanged; this doc
adds a web front end, a rename, and two catalogs, not a new way to load a package.

**No migration concerns.** No deployment has adopted the phase-109 code yet, so the rename and the
descriptor model change below are free — no compatibility shim, no on-disk migration step.

---

## Recommendation up front

1. **Rename "provider" → "library" everywhere it means "a NuGet-restored assembly a driver loads."**
   `DbDataSync.Providers` → `DbDataSync.Libraries`, `provider.json` → `library.json`,
   `<repo>/providers/` → `<repo>/libraries/`, `dbdatasync config provider` → `config library`. Every
   piece of descriptive text says what it is: *a library is an ADO.NET database provider — or another
   third-party assembly — that DbDataSync restores from NuGet and loads at runtime.*

2. **In the same phase, make a descriptor reference its library by id.** Today a `driver.yaml`
   repeats a `packages:` list that nothing at load time actually reads (see *The descriptor / library
   split* below). Replace it with `library: <id>` pointing at one `library.json` — one source of
   truth, and a real "used by" relationship for the UI.

3. **A read-only "Drivers" view under Admin.** Every registered driver — built-in, descriptor,
   compiled — with its display name, source, the library it binds, and a capability summary
   (readers / caches / writers). This is where an operator lands when the connection editor's engine
   picker doesn't list what they need.

4. **A "Libraries" view under Admin, with install/remove.** Installed libraries (packages +
   versions, factory type, whether they resolve, which drivers use them). Install via a **NuGet
   search box** plus a **curated shortlist**; remove one nothing uses.

5. **Two bundled catalogs — `KnownLibraries` and `KnownDrivers`.** `KnownLibraries` grows out of
   today's `KnownProviderFactories`: the handful of vetted, common providers. `KnownDrivers` is new:
   ready-made descriptor bodies (dialect + type map + capabilities) for common engines, each bound to
   a `KnownLibraries` entry. Together they make "add MySQL / MariaDB" one click — install the vetted
   library, drop in the vetted descriptor, no YAML authoring.

6. **Curated vs. trusted-by-you.** The install UI visually separates catalog entries (DbDataSync's
   authors vouch for these) from anything found through search. Installing a non-catalog package
   requires an explicit "I understand I am running this package's code in the DbDataSync host"
   confirmation — it is RCE, same as a hook or a script.

---

## Why rename "provider"

`provider` currently means three unrelated things:

| where | what it is |
| --- | --- |
| `DbDataSync.Providers`, `provider.json`, `DbProviderFactory` | a restored ADO.NET client assembly |
| `IStagingProvider` — renders in the pipeline UI as **"staging provider"** | a cache strategy between reader and writer |
| the `metadataProvider` script slot | a script that supplies table metadata |

Two of those surface in the same SPA, and "provider" is the least specific name of the three.
"Library" is concrete, is what the thing actually is, and doesn't collide. The one term that stays is
the field `factoryType` and the phrase "ADO.NET provider" in prose — the industry term is correct at
that level.

### Rename scope

- **Project**: `src/DbDataSync.Providers/` → `src/DbDataSync.Libraries/` — namespace, `.csproj`, the
  two composition-root call sites, every `ProjectReference`.
- **Types**: `ProviderManifest` → `LibraryManifest`, `ProviderRegistry` → `LibraryRegistry`,
  `ProviderInstaller` → `LibraryInstaller`, `ProviderPaths` → `LibraryPaths`,
  `KnownProviderFactories` → `KnownLibraries`, `ProviderPackageRef` → `PackageRef`.
- **On disk**: `<repo>/providers/<id>/provider.json` → `<repo>/libraries/<id>/library.json`; the
  `lib/` subdirectory is unchanged.
- **CLI**: `dbdatasync config provider install|sync|list|uninstall` → `config library …`. No alias
  (phase 115's precedent: in-repo tooling, break it cleanly).
- **Runtime messages**: `LibraryRegistry.GetFactory`'s "run `dbdatasync config library install …`"
  and `ParameterCheck`'s unknown-driver message (both have tests asserting the exact string).
- **Docs**: `nuget-loaded-drivers.md` and the phase-109 implementation docs keep their historical
  "provider" wording; new prose uses "library".

---

## The descriptor / library split

Today a `driver.yaml` descriptor carries a `provider:` block with its packages written *inside it*:

```yaml
provider:
  factoryType: "MySqlConnector.MySqlConnectorFactory, MySqlConnector"
  packages:
    - { id: MySqlConnector, version: "2.4.0" }
```

Separately, `<repo>/providers/MySqlConnector/` holds a `provider.json` and the actually-restored
`lib/`. `config driver install` creates both. But at load time, `DriverLoader` does only this:

```csharp
var providerId = descriptor.Provider.Packages[0].Id;   // "MySqlConnector" — a string
var factory = providerRegistry.GetFactory(providerId); // resolved from providers/*/provider.json
```

So the descriptor's `packages:` block is **not** what gets restored or loaded — the `providers/`
directory is. `packages[0].id` is a lookup key; the `version:` beside it is decorative and can drift
from what's installed. The two are joined only by the convention "first package id == provider id."

### The fix (part of the rename phase)

The descriptor names a library by id and nothing else:

```yaml
# drivers/mysql.generic/driver.yaml
library: mysql-connector
```

```json
// libraries/mysql-connector/library.json  — the one place packages + versions + factory type live
{ "id": "mysql-connector",
  "factoryType": "MySqlConnector.MySqlConnectorFactory, MySqlConnector",
  "packages": [{ "id": "MySqlConnector", "version": "2.4.0" }] }
```

`config driver install <id> --library <name> …` creates (or reuses) the named library and writes the
`library:` line. `--factory-type` becomes a property of the library, not the descriptor. `DriverLoader`
resolves `descriptor.Library` → `libraryRegistry.GetFactory(name)`. This supersedes phase 109d's
"inline the package list" decision — cheap now, since nothing has shipped on it.

---

## The bundled catalogs

### `KnownLibraries`

`KnownProviderFactories` today is `id → factoryType`, seven entries. Grow each entry to:

```
id            packageId                       factoryType                  displayName          vetted
sqlserver     Microsoft.Data.SqlClient        Microsoft.Data.SqlClient…    SQL Server           true
postgres      Npgsql                          Npgsql.NpgsqlFactory, Npgsql  PostgreSQL           true
mysql-connector  MySqlConnector               MySqlConnector.MySqlConn…    MySQL / MariaDB      true
oracle        Oracle.ManagedDataAccess.Core   Oracle.ManagedDataAccess…    Oracle               true
sqlite        Microsoft.Data.Sqlite           Microsoft.Data.Sqlite…       SQLite               true
odbc          System.Data.Odbc                System.Data.Odbc.OdbcFactory ODBC                 true
firebird      FirebirdSql.Data.FirebirdClient FirebirdSql…FirebirdClient…  Firebird             true
```

Rendered as quick-add chips above the search box; picking one pre-fills package id + factory type,
the operator still picks a version.

### `KnownDrivers` (new)

A bundled descriptor body per common engine — the thing `DriverTemplates.MySql` is today, promoted
from a one-off CLI string into a catalog the API can also read. Lives in
`DbDataSync.Drivers.Descriptor` (so it can validate its own entries). Each entry:

- a `displayName` and a stable `id` (e.g. `mysql.generic`);
- the descriptor body — `dialect` (quoting, parameter prefix, row-limit style, catalog strategy) +
  `typeMap` + `capabilities` — as an embedded `.yaml` resource;
- the `KnownLibraries` id it binds (`mysql-connector`).

Seeded with the existing MySQL body. `config driver install --from <name>` reads this catalog instead
of the hardcoded `switch` in `DriverTemplates` (the `Minimal` blank shell stays for no-`--from`). CI
round-trips every entry through `DriverDescriptorReader` so a bundled descriptor can't rot.

The web "add a driver" flow then offers each `KnownDrivers` entry as one click: install the bound
library, write the descriptor, done — the vetted path, with no dialect/`typeMap` authoring.

---

## The web endpoints and screens

### Drivers (read-only, v1)

`GET /api/drivers` gains: `library` (the bound library id, null for a built-in), `capabilities`
(reader / staging / writer kind names, already computed by `DriverRegistry.Describe`), and
`source: "compiled"` as a real value (`DriversController` currently hard-codes `"descriptor"` for
everything non-built-in — a pre-existing gap to close here).

The screen: a table grouped by source, built-ins marked and non-removable, each descriptor/compiled
driver linking its library row. An "Add a driver" panel: the `KnownDrivers` list as one-click adds
(enabled in the install phase), plus the copyable `dbdatasync config driver install …` invocation for
a hand-authored descriptor, linking the doc. No dialect/`typeMap` authoring UI — that is its own
planning doc later.

New **Drivers** tab in `AdminTabs` (today: Configuration | Certificate).

### Libraries (install/remove, v1)

- `GET /api/libraries` → `[{ id, packages: [{ id, version }], factoryType, resolves, usedBy:
  [driverId…], curated }]`. `resolves` is a real `GetFactory` probe; `curated` = the id matches a
  `KnownLibraries` entry.
- `POST /api/libraries` → `{ packageId, version, factoryType?, source? }`. Runs
  `LibraryInstaller.InstallAsync`, writes `library.json`, returns the manifest or a structured error,
  sets restart-required.
- `POST /api/drivers/from-catalog` → `{ knownDriverId, version }`. Installs the bound library and
  writes the bundled descriptor. The one-click path.
- `DELETE /api/libraries/{id}` → refused with the using-driver list if `usedBy` is non-empty;
  `?force=true` overrides. In-use is in-use regardless of whether the library currently resolves.
- `GET /api/libraries/search?q=…` → NuGet search proxy.
- `GET /api/known-libraries`, `GET /api/known-drivers` → the catalogs.

All under `Policies.Admin`, stated explicitly per the `AdminConfigController` pattern.

### NuGet search

A plain HTTPS GET to the NuGet search service
(`https://azuresearch-usnc.nuget.org/query?q=<term>&prerelease=false`), reshaped to
`[{ id, description, latestVersion, versions: [...], totalDownloads, verified }]`. **Not** a NuGet
client — no `NuGet.Protocol`, no package resolution, just a search-index read. The parent doc
rejected a runtime NuGet client for *restore*; a read-only search query doesn't cross that line.

- **Offline / air-gapped:** the call fails, the box shows "search unavailable — enter a package id
  and version directly," manual entry still works. `DbDataSync:NuGetSearchEnabled` (default true)
  turns it off definitively.
- **Version pick:** the UI shows the version list and requires an explicit choice — the CLI's "pinned,
  never latest" rule holds in the UI too.

---

## Trust, build environment, and restart

1. **Loading a package is RCE in the API process.** Unchanged from hooks and scripts, but a web
   button lowers the barrier. Mitigation: the catalog / non-catalog split, an explicit confirmation
   for non-catalog packages, `Policies.Admin`, and the write landing as a diffable config commit in
   the Version Control tab.

2. **Web install needs the .NET SDK** — `LibraryInstaller` shells out to `dotnet publish`. This is
   already true of every non-container deployment: `dbdatasync` is a dotnet global tool and
   `dotnet tool install` requires the SDK, so a machine that has the CLI has the SDK. The gap is the
   Docker image alone (today an `aspnet:10.0` runtime base). **Decided: the default image moves to an
   `sdk:10.0` base**, so the full feature set — ad-hoc library install included — works on a first
   `docker compose up` with no operator setup. The image grows; accepted. A smaller runtime-only
   image, for shops wanting a minimal/hardened footprint with only pre-vetted drivers, is follow-on
   work (see *Follow-on work*). The read-only views have no SDK constraint on any deployment.

3. **Restart.** Libraries and drivers load at composition-root startup — a web install shows the same
   "restart required" banner as Admin → Configuration. Asymmetry worth noting: the TaskRunner is
   spawned per run and re-scans `<repo>/libraries` + `<repo>/drivers` every time, so it picks up a
   new library on the next run automatically; only the long-lived API process needs the restart.

---

## Implementation phases

Five committed phases plus two follow-ons — each compiles, passes CI, and is deployable on its own.
The buildable design for each is in its own doc:

- **`phase-116`** — the `provider` → `library` rename and the descriptor's `library:` reference.
- **`phase-117`** — `KnownLibraries` / `KnownDrivers` catalogs; `--from` reads them.
- **`phase-118`** — `GET /api/libraries`, extended `GET /api/drivers`, the two read-only Admin
  screens.
- **`phase-119`** — the NuGet search proxy and the search box (copyable command, no install yet).
- **`phase-120`** — `POST`/`DELETE` install/remove, the trust confirmation, and the default Docker
  image moving to an SDK base.
- **`phase-121`** *(follow-on)* — a runtime-only image with the catalog libraries pre-restored into
  it; catalog installs become a file copy, non-catalog installs go "pending restore" +
  `config library sync`.
- **`phase-122`** *(follow-on)* — `factoryType` reflection-assist: derive the factory type from a
  restored non-catalog package instead of making the operator paste it.

---

## What this does not do

- **Web authoring of `driver.yaml` descriptors** (dialect, `typeMap`, capabilities), or installing
  compiled `IDriver` plugins from the web — v1 scope. Its own planning doc later.
- **A runtime NuGet client for restore.** Still the `dotnet` throwaway-project mechanism from
  phase 109c. Search is the only network call added, and it's read-only.
- **Renaming `IStagingProvider` or the `metadataProvider` script slot.** Different concepts; the
  point is that "library" and "staging provider" stop both reading as "provider".
- **Hot reload.** A new library or driver takes effect on API restart.
- **State-store libraries.** `StateEngine` is a separate closed set (phase 109g's territory).
- **A marketplace / auto-update.** Install is explicit and version-pinned.

---

## Decisions

Resolved in review; recorded here so the phase docs can cite them.

1. **The default Docker image gets the .NET SDK.** The full feature set — ad-hoc (non-catalog)
   library install from the web included — works on a first `docker compose up`, no setup. The image
   grows; accepted. Every non-container deployment already has the SDK (`dotnet tool install`
   requires it). A minimal runtime-only image is follow-on work.
2. **Catalogs are embedded in the assembly**, like `KnownProviderFactories` today — not an
   operator-editable file. The catalog is "what DbDataSync's authors vouch for"; a custom config is
   the ordinary `driver.yaml` path. A `<repo>/driver-catalog/` drop-in can be added later if asked
   for.
3. **`KnownDrivers` ships one entry for v1: `mysql`.** It is the only worked descriptor body that
   exists and was tested end-to-end (109d). SQLite-via-descriptor needs `connectionStringKeys` work
   the generic layer lacks (its keys are Host/Port/Database; Microsoft.Data.Sqlite rejects `Host=`).
   Postgres-alike and Oracle each need someone who runs that engine to validate the type map. The
   catalog structure is built to grow.
4. **NuGet search: graceful degradation *and* a `DbDataSync:NuGetSearchEnabled` toggle** (default
   true). Degradation covers a flaky network; the toggle is for a shop that knows it is offline.
5. **`factoryType` for a non-catalog package with no known type: the operator pastes it**, matching
   the CLI. Reflection-assist is follow-on work.
6. **Two Admin tabs, Drivers and Libraries**, consistent with the existing Configuration |
   Certificate pattern.
