# A simple web UI for authoring `driver.yaml`

**Status**: Design, not phase-ready.
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md` — this is the
doc its own "What this does not do" section named as follow-on work: *"Web authoring of `driver.yaml`
descriptors (dialect, `typeMap`, capabilities)... its own planning doc later."* This is that doc.

## Scope, against the real schema

The user's ask — "pick a base, pick from the desired pipeline phases, add jar/nuget references, add
metadata queries" — maps directly onto `DriverDescriptorYaml` (`src/DbDataSync.Drivers.Descriptor/DriverDescriptorYaml.cs`),
field by field:

| UI element | Schema field(s) |
| --- | --- |
| id / display name | `Id`, `DisplayName` |
| pick a base | `Base` (omitted = `GenericDriver`/ADO.NET; `DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc` for JDBC) |
| pick pipeline phases | `Capabilities.Readers`/`.Staging`/`.Writers` |
| add jar/nuget references | ADO.NET: `Library` (an installed `library.json` id). JDBC: `Jdbc.DriverClass` + `Jdbc.DriverJarPaths` (169V) |
| add metadata queries | `Dialect.Catalog` (`informationSchema`/`databaseMetaData`/`query`) + `MetadataQueries.TableQuery`/`.ColumnQuery` |

**One field the ask didn't mention, and the UI can't skip: `TypeMap`.** It's `required` on the descriptor
and empty means every native type comes back `Unmappable` — a driver with no `typeMap` entries is not
actually usable. This doc proposes a hybrid rather than either silently dropping it or blocking on full
structured authoring for it — see "Typemap: structured now, or raw YAML" below.

## Layout

Inside the existing Admin → **Drivers** tab, next to the two affordances already there (the `KnownDrivers`
one-click add, and the copyable `dbdatasync config driver install …` command for a hand-authored file): a
third, **"Build one"**, opening a form.

### 1. Base

A two-option picker — **ADO.NET** (the common case, `Base` omitted) or **JDBC** (`base:
DbDataSync.Drivers.Jdbc.JdbcGenericDriver, ...`). Everything below it changes shape based on this choice;
nothing else in the form is meaningfully generic across both.

### 2. Connection library (ADO.NET) or driver jar(s) (JDBC)

**ADO.NET**: reuses the *existing* library picker built for phase 119/120 wholesale — `KnownLibraries`
quick-add chips plus the NuGet search box — not reinvented here. Picking or installing one sets `Library`.

**JDBC**: `DriverClass` (text — `org.postgresql.Driver`-shaped) plus a jar list. Where the jar *files*
themselves come from — the one thing this UI couldn't be designed in isolation, when this doc was first
written — is now resolved: `architecture/planning/todo/user-provided-files-store.md`'s `files/` store,
with its own small GUI (Admin → Files). The jar picker here is "pick from `GET /api/files` or upload a
new one inline," embedding that store's upload control the same way §2's ADO.NET half already embeds the
Libraries search box rather than sending the operator to a different screen — one small multi-select
list, not a new upload mechanism of its own.

Once jars exist, `jdbc-ikvmreference-compile-button.md`'s "Compile" action is a natural next affordance
on this same screen — out of scope for authoring itself, cross-referenced only.

### 3. Pipeline phases

Three checkbox groups, one per `Capabilities` list:

```
Readers:   [ ] Watermark   [ ] BatchReload   [ ] TriggerAudit   [ ] KeyReconcile
Staging:   [ ] StagingTable
Writers:   [ ] DeleteInsert   [ ] KeyReconcileDelete   [ ] Snapshot   [ ] Scd2
```

**These lists must not be hardcoded in the SPA.** `GenericDriverBase<TSpec>.BuildReaders/BuildStaging/
BuildWriters` (`src/DbDataSync.Drivers.Generic/GenericDriverBase.cs`) is the actual source of truth for
which `GenericDriverKinds` strings are valid — a `switch` that throws `ArgumentException` for anything
else. A checkbox list authored separately in TypeScript would drift from that `switch` the next time a
kind is added (or, per phase 172V, when JDBC writers go from theoretically-listed-but-broken to actually
working). Proposed: a small `GET /api/known-driver-kinds` returning
`{ readers: [...], staging: [...], writers: [...] }`, sourced from the same constants
`GenericDriverKinds`/the `BuildWriters` switch already use, so the UI can never list a kind the backend
would reject.

**Writers, for a JDBC base, are a trap until 172V ships.** `JdbcConnection.BeginDbTransaction` throws
`NotImplementedException` today (`phase-172V-jdbc-write-support.md`) — a driver.yaml built through this UI
with `base: JdbcGenericDriver` and `writers: [DeleteInsert]` would parse and register cleanly, then crash
the first time anything actually tried to write through it. Until 172V ships, the UI should either hide
the Writers group entirely when Base = JDBC, or the `known-driver-kinds` endpoint above should be able to
express "writers exist as a concept but aren't usable for this base yet" so the SPA doesn't need its own
hardcoded "JDBC can't write" rule that itself goes stale the moment 172V lands.

### 4. Metadata queries

A catalog-strategy picker: **Default** (informationSchema for ADO.NET, `DatabaseMetaData` for JDBC —
labelled per the chosen base, not a bare "default"), or **Custom query**, which reveals two SQL text
areas (`TableQuery`/`ColumnQuery`) — the exact two fields `MetadataQueriesYaml` already has. Good fit for
the in-app Monaco editor (phase 028/160K already built it for scripts and docs) rather than a plain
`<textarea>` — SQL syntax highlighting, nothing new to build for the editor itself.

### 5. Typemap: structured now, or raw YAML

Full structured authoring for `TypeMap` is genuinely harder than everything else in this form —
`TypeMapEntryYaml`'s `(p,s)`-placeholder substitution rules
(`decimal(p,s)` → `{ kind: Decimal, precision: p, scale: s }`) are a small DSL of their own, and a
dropdown-and-fields UI for it is real, separate design work, not a checkbox list.

Proposed v1: the same Monaco editor as above, dropped into "raw YAML for `dialect` and `typeMap`" — the
two fields hardest to give a good structured UI, pre-filled with a skeleton and, when the operator picked
a `KnownDrivers` entry as a starting point (see below), that entry's real `typeMap` as a starting point to
edit rather than write from scratch. Structured authoring for `typeMap` specifically is a plausible v2,
not blocking v1 — the honest scope line `drivers-and-libraries-in-the-web-ui.md` already drew ("no
dialect/typeMap authoring UI") moves to "structured UI for everything except typeMap's own DSL, raw YAML
with a real starting point for that," not all the way to "fully structured."

### Starting from a `KnownDrivers` entry

`GET /api/known-drivers` already exists (phase 117/120) and each entry is a full descriptor body. "Build
one" should offer "start from..." against that catalog — picks a base, capabilities, dialect and typeMap
that are already known-good, leaving the operator to change only what's actually different for their
variant (a different `driverClass`, a narrower capability set, a vendor-specific `typeMap` addition)
rather than authoring a `PostgreSQL`-shaped descriptor from an empty form. This is the same "vetted
starting point over blank authoring" instinct the one-click catalog add already embodies, applied to the
authoring path instead of only the zero-authoring path.

## Save / validate / edit

**Create**: a new `POST /api/drivers` (doesn't exist today — only `POST /api/drivers/from-catalog` does).
Validates by the same round-trip `DriversController.InstallFromCatalog` already performs — deserialize,
`DriverDescriptorReader.BuildDriver`, register into the live `DriverRegistry` immediately, `409` if the id
already exists, `RestartRequiredState.Touch()` on success. A parse or build failure surfaces the real
exception message (`YamlDotNet.Core.YamlException`, or `BuildDriver`'s own `NotSupportedException` for a
missing `jdbc:` block) rather than a generic "invalid descriptor."

**Edit**: `GET /api/drivers/{id}/yaml` (raw) + `PUT /api/drivers/{id}`. Loading an existing file into the
form has to round-trip cleanly even for a file that was hand-edited outside this UI (the CLI-authored
path stays fully supported, this UI is additive) — which is the other reason the "structured fields +
raw-YAML passthrough for dialect/typeMap" split above matters: a hand-authored `typeMap` with entries this
UI's structured fields don't (yet) model still loads into the raw editor unchanged, rather than the form
silently dropping anything it doesn't recognize.

**Delete**: reuses whatever `DELETE` story exists for a driver already (checked: none does yet — today a
descriptor is only ever removed by deleting `drivers/<id>/` by hand). Out of scope here; flagged so this
doc doesn't imply it's covered.

## Trust posture

Writing a `driver.yaml` is not itself code execution the way installing a compiled `IDriver` plugin or an
arbitrary non-catalog library is — no new assembly gets loaded that wasn't already vetted (an ADO.NET
`library` the operator already installed, or IKVM for JDBC). Still `Policies.Admin`, consistent with every
other Drivers/Libraries mutation, but doesn't need the "I understand I am running this package's code"
confirmation `drivers-and-libraries-in-the-web-ui.md` requires for a non-catalog library install — that
confirmation belongs to the jar-upload/library-install sub-flows this form embeds, not to the form itself.

## What this does not build

- The `files/` store and its own upload GUI — `user-provided-files-store.md`'s scope, embedded here
  (§2), not rebuilt here.
- Structured `typeMap` authoring beyond the raw-YAML editor (v2 candidate).
- `DELETE /api/drivers/{id}` — doesn't exist today for *any* driver, descriptor-authored-by-hand included;
  out of scope for this doc specifically.
- The `IkvmReference` "Compile" button itself — its own doc
  (`jdbc-ikvmreference-compile-button.md`), surfaced from this screen once both exist.
- Compiled `IDriver` plugin authoring (109e) — a different mechanism (a real .NET project, not YAML),
  out of scope for a YAML-authoring UI by definition.

## Rough phase split, if this gets picked up

Mirroring how `drivers-and-libraries-in-the-web-ui.md` itself split into 116–122:

1. `GET /api/known-driver-kinds` + `POST`/`GET`/`PUT /api/drivers/{id}/yaml` — the API surface, no UI yet,
   testable on its own the way every prior phase in that family was.
2. `user-provided-files-store.md`'s own API + GUI (`GET`/`POST`/`DELETE /api/files`, Admin → Files) — a
   real prerequisite for the JDBC half of 3, but independently useful and buildable first.
3. The structured form (base, capabilities, library/jar picker embedding 2, metadata queries) plus the
   raw-YAML dialect/typeMap editor — the bulk of the UI.
4. "Start from a `KnownDrivers` entry" — small, additive, fine to land whenever convenient inside 3.
