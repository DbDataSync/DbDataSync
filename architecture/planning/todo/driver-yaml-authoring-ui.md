# A simple web UI for authoring `driver.yaml`

**Status**: Core built 2026-09-22 (`DriverEditPage`, the `POST`/`GET`/`PUT` backend, `driverYamlAssembly.ts`).
See Retrospective. What's still open: structured `typeMap` authoring (v2, as this doc always scoped it),
"start from a `KnownDrivers` entry" (not built — `GET /api/known-drivers` doesn't expose a full descriptor
body, only 4 metadata fields, contradicting what this doc originally assumed), and the JDBC create path
has no Playwright coverage (needs a real jar + `ikvm` actually installed, neither set up in the test
server — proven at the API level instead, where it doesn't need either).
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

**Updated 2026-09-22**, after the Drivers/Libraries/Files nav restructure: these three moved out of Admin
into their own rail section (own icon, `/drivers`, `/drivers/libraries`, `/drivers/files` — still
admin-gated, both at the rail-item level and per-endpoint `Policies.Admin`) — a real UX review found this
matters more than a cosmetic move, because it also settles this doc's own page shape (below).

A dedicated page, not a panel on the list — checked against the one precedent this app already has for
"author a chunk of code/config, list it, edit it later" (Scripts: `ScriptsPage` list + a real
`ScriptEditPage`, not a form bolted onto the list). `DriversPage` (the list, `/drivers`) gets a "New
driver" button navigating to `DriverEditPage` at `/drivers/new` (and `/drivers/:id/edit` for editing an
existing one) — the same two-page shape, not a growing panel next to the existing `KnownDrivers` one-click
add and the copyable `dbdatasync config driver install …` command, which both stay on `DriversPage`
itself, unchanged.

### 1. Base

A two-option picker — **ADO.NET** (the common case, `Base` omitted) or **JDBC** (`base:
DbDataSync.Drivers.Jdbc.JdbcGenericDriver, ...`). Everything below it changes shape based on this choice;
nothing else in the form is meaningfully generic across both.

### 2. Connection library (ADO.NET) or driver jar(s) (JDBC)

**ADO.NET**: reuses the library picker built for phase 119/120 — `KnownLibraries` quick-add chips plus the
NuGet search box — not reinvented here. Picking or installing one sets `Library`.

**Checked, not assumed: this isn't a drop-in import today.** `LibraryFindPanel` (the whole chips+search+
install flow, `LibrariesPage.tsx`) is a page-local function, not exported — same for the Files upload
control (`FilesPage.tsx`). "Reuse" means extracting each into its own component first
(`components/LibraryFindPanel.tsx`, `components/FileUploadPanel.tsx` or similar), a small, contained
prerequisite this doc didn't originally price in, not a blocker.

**JDBC**: `DriverClass` (text — `org.postgresql.Driver`-shaped) plus a jar list, picked from `GET
/api/files` or uploaded inline via the now-extracted upload control — `architecture/planning/todo/user-provided-files-store.md`'s
`files/` store, reachable directly too (Drivers section → Files tab) for general management.

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
the in-app `CodeEditor` (`language="sql"`, phase 028/160K already built it for scripts) rather than a
plain `<textarea>` — SQL syntax highlighting, nothing new to build for the editor itself.

### 5. Typemap: structured now, or raw YAML

Full structured authoring for `TypeMap` is genuinely harder than everything else in this form —
`TypeMapEntryYaml`'s `(p,s)`-placeholder substitution rules
(`decimal(p,s)` → `{ kind: Decimal, precision: p, scale: s }`) are a small DSL of their own, and a
dropdown-and-fields UI for it is real, separate design work, not a checkbox list.

Proposed v1: `CodeEditor` again, `language="yaml"`, dropped into "raw YAML for `dialect` and `typeMap`" —
the two fields hardest to give a good structured UI, pre-filled with a skeleton and, when the operator
picked a `KnownDrivers` entry as a starting point (see below), that entry's real `typeMap` as a starting
point to edit rather than write from scratch. **Checked, not assumed**: Monaco's `yaml` tokenizer is
already registered (`monacoSetup.ts`, phase 35, for read-only config diffs), so no new language to wire
up — but `CodeEditorProps.language` is currently typed `'csharp' | 'sql'` only and needs widening to
include `'yaml'`, and this would be the **first editable** YAML surface in the app (every existing use is
`MonacoDiff`/read-only). No live diagnostic squiggles the way script editing gets them either — there's no
compile endpoint for a driver.yaml to check against as you type; errors surface on Save, via the same
round-trip validation §"Save / validate / edit" below already describes.

Structured authoring for `typeMap` specifically is a plausible v2, not blocking v1 — the honest scope line
`drivers-and-libraries-in-the-web-ui.md` already drew ("no dialect/typeMap authoring UI") moves to
"structured UI for everything except typeMap's own DSL, raw YAML with a real starting point for that," not
all the way to "fully structured."

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
2. `user-provided-files-store.md`'s own API + GUI (`GET`/`POST`/`DELETE /api/files`, the Files tab) — a
   real prerequisite for the JDBC half of 3, but independently useful and buildable first.
3. The structured form (base, capabilities, library/jar picker embedding 2, metadata queries) plus the
   raw-YAML dialect/typeMap editor — the bulk of the UI.
4. "Start from a `KnownDrivers` entry" — small, additive, fine to land whenever convenient inside 3.

---

# Retrospective

## What shipped

Every phase above except 4 (see "What's still open" in the Status line): the known-kinds endpoint (built
alongside a real refactor of `GenericDriverBase` — see `phase-172V`'s own follow-on work, the switch→
dictionary change that made `SupportedReaderKinds`/etc. structurally correct), the `files/` store (phase
173V, already built before this), `DriverEditPage` and its backend.

**One deliberate simplification from the design above, decided while building, not before**: §4/§5's
three-way split (a catalog-strategy picker plus two SQL text areas as their own structured control,
separate from a dialect/typeMap raw editor) became **one raw YAML block** covering dialect, typeMap, and
metadataQueries together. Splitting metadata-queries out cleanly needs parsing the raw dialect text to
know whether `catalog: query` is already set there — real complexity a v1 doesn't need, and arguably a
more honest line anyway: those three are all "how this engine's SQL looks," which a vendor-familiar
operator would naturally edit together. `driverYamlAssembly.ts`'s own top-level-key splitter is what makes
even this simpler split possible — not a real YAML parser, deliberately: it finds top-level keys by
"starts at column 0," reliable for this app's own generated shape, with the raw editor itself as the
escape hatch when a hand-authored file doesn't split cleanly.

**§2's other real correction**: `LibraryFindPanel` and the Files upload control genuinely weren't reusable
when this doc first claimed they were ("reuses... wholesale") — both were page-local. Extracted first (a
separate, already-pushed commit), *then* embedded here — `LibraryFindPanel` gained one new prop
(`onInstalled`) so the driver form can react to a library it just installed, `FileUploadPanel`'s
`onUploaded` already had the equivalent shape.

## A real bug found building the backend, not part of the design above

`DriverDescriptorReader.BuildDriver`'s JDBC path dispatches through `MethodInfo.Invoke`, which wraps
whatever `JdbcGenericDriver.FromDescriptor` itself throws in a `TargetInvocationException`.
`InstallFromCatalog` never hit this — it calls `DriverDescriptorReader.ToSpec` directly, bypassing
`BuildDriver`'s reflection entirely. This authoring endpoint is the first caller to actually exercise that
path with operator-supplied YAML, where a failure needs a clean message, not a reflection wrapper's own.
Unwrapped in `DriversController.TryBuild`; proven with a real test (`Create_WithAJdbcBaseMissingItsJdbcBlock_...`),
not just fixed and assumed correct.

## A real product inconsistency found writing the Playwright test

`LibraryFindPanel`'s own install (`POST /api/libraries`) keys a new library by *package* id
(`"MySqlConnector"`), not the catalog's own id (`"mysql-connector"`) the way `POST /api/drivers/from-catalog`
does — two different code paths landing on two different ids for what looks like the same "install
mysql-connector" action. Found because `driver-authoring.spec.ts`'s own `selectOption('mysql-connector')`
timed out for a full minute with no such option — not assumed, not guessed at. Documented separately:
`follow-up-library-install-paths-disagree-on-the-resulting-library-id.md`, not fixed here (out of this
doc's own scope, and needs a real decision about which behavior is correct).

## Testing

- `driverYamlAssembly.test.ts` — assemble/parse round-trips for ADO.NET and multi-jar JDBC shapes, and
  that the raw body stays distinct from its structured neighbors in the assembled document.
- `DriverAuthoringTests.cs` (API-level, 12 tests) — create/get/update, admin gating, the duplicate-id 409,
  the invalid-YAML 400 (asserting nothing was written, via a before/after directory count — the shared
  test fixture means outright absence isn't a safe assertion), and the `TargetInvocationException`-unwrapping
  proof above.
- `driver-authoring.spec.ts` (Playwright, 2 tests) — the real create-then-edit flow end to end: fill the
  form, install a library inline via the embedded `LibraryFindPanel`, save, see it listed; open it for
  editing, confirm the loaded fields match (id disabled and correct, capabilities pre-checked), change the
  display name, save, confirm the change persisted.

Verified together: `tsc -b`/`oxlint`/production `vite build`/full `vitest` suite (84/84) all clean;
every Playwright spec touching the Drivers page re-run (18/18) for regressions; the API test suite's
relevant classes green (17 total across `DriverAuthoringTests`/`DriverKnownKindsTests`/`DriversControllerTests`,
confirmed individually — a combined-filter run hit an unrelated environment hang, chased down to ~40
orphaned MSBuild worker nodes left behind by an earlier killed process, cleaned up but not worth re-chasing
the combined run for once the individual results already stood).
