# Phase 90 — Mapping metadata cache: captured on creation, refreshed only on request

**Status**: Not started.
**Plan reference**: `architecture/planning/done/mapping-metadata-cache.md`, resolving the audit at
`architecture/planning/done/metadata-queries-design-time-only.md`.

## The gap

`ColumnMapping` stores no type/key/nullability info for either side, deliberately (its `TargetType`
field's own doc comment explains why: freezing an inference risks staleness). As a result, several
readers and every writer/staging provider re-query the source or target's live catalog on every actual
run to get information a stored cache could answer instead — a real cost, but not a bug to fix blindly,
since a table's shape genuinely can change and a silently stale cache is worse than the live query it
replaces. This phase builds the cache and a deliberate refresh action; it does not yet make anything
read from the cache instead of the source/target.

## What to build

### Schema

`TableMappingConfig` gains `SourceColumns`/`TargetColumns` (`IReadOnlyList<ColumnMetadata>` — the
existing `Name`/`NativeType`/`IsNullable`/`IsPrimaryKey`/`IsIdentity` shape), empty by default.

### Capture on creation

Find where the mapping editor's column-mapping tab currently fetches live source/target columns for its
picker (`MappingSide.tsx`/`ColumnMappingEditor.tsx` and whatever API call backs it — confirm the actual
current mechanism before assuming). Persist that same fetch's result into the new fields when the
mapping is saved, rather than adding a second query whose only purpose is populating the cache.

### Refresh endpoint

A new action on `TableMappingsController` (e.g. `POST .../table-mappings/{mappingName}/refresh-metadata`)
that re-runs the introspection live — the same `ITableCatalog`/`IDriver` methods already used at design
time — and overwrites `SourceColumns`/`TargetColumns`, returning the updated mapping.

### UI

A "Refresh metadata" control on the mapping editor (source and target sides, or one control for both —
your call once laid out) that calls the new endpoint and shows what came back, not a silent update.

## What this phase should not do

- Touch `BatchReloadReader.ExpandAutoSegmentsAsync` or any auto-segmenting behavior — confirmed to stay
  live, no stored substitute exists for sampling a value distribution.
- Change what any reader, writer, or staging provider actually does at read/write time —
  `WatermarkReader`, `BatchReloadReader`, `TriggerAuditReader`, `ScriptedQueryReader`, every writer's
  `TargetShape.LoadAsync`, and `BatchInsertStagingProvider.StageAsync` keep live-querying exactly as
  today. Wiring them to read this cache instead is a real behavior change, deliberately left for its own
  later, separately-reviewed phase.
- Backfill existing mappings' caches — an existing mapping's `SourceColumns`/`TargetColumns` stay empty
  until it's edited (triggering the same capture creation uses) or an operator explicitly hits Refresh.

## How to verify

- A test asserting a newly created mapping's cache is populated from the column-mapping tab's existing
  fetch, with no additional live query introduced.
- A test asserting only the Refresh action updates the cache — an ordinary save that doesn't touch
  source/target config leaves it untouched.
- A test asserting an existing mapping with an empty cache (simulating a pre-this-phase row) works
  correctly everywhere else in the app — nothing yet depends on these fields being populated.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

---

## Outcome

Built as specified. The cache exists, the editor captures it, one action refreshes it, and nothing
reads it — which is the phase.

### What was built

`TableMappingConfig` gains `SourceColumns`/`TargetColumns` (`List<CachedColumn>`, empty by default)
and `ColumnsCapturedUtc`. `CachedColumn` restates `ColumnMetadata`'s five facts rather than
referencing it: `DataSync.Drivers.Abstractions` references `DataSync.Core`, not the reverse, so config
cannot name the driver type. It is a class with a parameterless constructor and settable properties
for the reason `RenameStep` is — YamlDotNet constructs through property setters, so a positional
record serialises out and then throws on the way back in.

`MappingMetadataCapture.Apply` is the whole of what an ordinary save may do to the cache, and it is
almost nothing: an empty incoming list never clears a populated one, and `ColumnsCapturedUtc` is the
server's to stamp, moving only when the stored columns actually differ. `MappingMetadataService`
re-reads both catalogs and overwrites, behind `POST .../table-mappings/{mappingName}/refresh-metadata`.
`CachedMetadataCard` sits below the column-mapping grid with the age of the picture and one button.

### Capture reuses the editor's fetch, and the tests say so

The doc's central requirement was that capture introduce no second live query. The wire-up is that
`TableMappingForm` writes the column lists it already holds — the pickers and the column-mapping tab
both run `useColumns` on the resolved endpoints, keyed identically, so React Query has already served
the answer — into the mapping at save time. `MappingMetadataTests.ANewMappingCarryingTheEditorsColumns_
IsCapturedOnCreation` asserts `_catalog.Calls` is **empty** after a creation capture, which is the only
form in which "no additional query" is checkable at all: it is invisible in the result.

### Judgment calls

- **One Refresh button for both sides, results reported per side.** The operator's question is
  whether their picture of *this mapping* is current, and one press is one endpoint, one save and one
  commit where two would be two of each. The answer still splits, because that is where the two sides
  genuinely differ — a source that read cleanly beside a target provisioning has yet to create.
- **A side that cannot be read keeps its cached columns.** Emptying a usable picture because a
  connection was down is the one outcome somebody pressing Refresh cannot want. `SideRead.Columns` is
  nullable precisely to keep "could not read" distinct from "the catalog says no columns".
- **Changes are named, not counted.** "2 changed" is a number an operator has to go and investigate;
  `Region, Currency` is the investigation. Renames read as one removal plus one addition, because
  nothing in a catalog row says the new name is the old one.
- **`IColumnCatalog`, registered by interface as well as concretely.** One narrow seam over the part
  that does I/O, the same shape `IChangeCounterSource` is, so the cache's rules are testable without a
  database. Refresh goes through `MetadataService` rather than a driver catalog of its own, so a
  connection with a bound `metadataProvider` script is honoured here too and the cache cannot disagree
  with the picker that populated it.
- **`DateTime` in UTC rather than `DateTimeOffset`.** YamlDotNet writes a `DateTimeOffset` out as a
  mapping of its own properties and then cannot read it back — the field would have appeared to work
  until the next config load.
- **The client's half of the rule is duplicated on the server.** The editor only sends fresh columns
  when a side's table moved or nothing was cached; the server enforces the same thing independently,
  because the SPA is not the only thing that can `PUT`.

### The screenshot churn, and what it was not

Roughly forty golden-path screenshots came back modified. Only `05-table-mapping-form.png` and the two
new ones show the card; every other diff is run-to-run noise — timestamps, PIDs, durations — that this
suite regenerates on any full run. Spot-checked rather than assumed: `09-run-history.png` differs only
in its clock and PID, and `05` correctly shows the pre-save hint state, since an unsaved mapping has
nothing on disk to refresh.

### The golden-path failure that was not this phase

A full Playwright run failed twice at golden path 41 (`backfill-strategy-select` never appearing),
which looked like a regression and was not. Running the golden path alone with this phase applied
passed 45/45; running the **full suite on stashed, clean `main`** failed too, at a different test (43,
the natural key). `BackfillForm`'s pre-fill effect resets `mode` when the mapping query resolves, so a
`selectOption('custom')` that lands before that resolution is undone — a load-sensitive race that
predates this phase. A third full run with this phase applied passed 61/61. Recorded here because the
first two runs were reproducible enough to look causal, and the only thing that distinguished them was
a fair comparison.

### How it was verified

- `MappingMetadataTests` (new, 15): nine over `RefreshAsync` against real config, real endpoint
  inheritance and a real git commit with only the catalog faked — first capture reporting every column
  as added, added/removed/changed named per column, a dropped primary key noticed even though the type
  did not move (the exact hazard the audit named, and the one a type-only diff would report as
  nothing), a rename as removal-plus-addition, an unreadable side keeping its columns, a query source
  with no table stated as a reason rather than an error, both sides failing leaving the capture time
  alone, and each side asked exactly once. Six over `MappingMetadataCapture.Apply` for what a save may
  not do.
- `TableMappingsControllerTests` (+3): the same rules through the route — capture on create then an
  unrelated save leaving the timestamp where it was, a pre-phase client sending no columns not
  clearing them, and refresh on a missing mapping being a 404.
- `CachedColumnsYamlRoundTripTests` (new, 3): all five facts of a column through real YAML, a
  never-captured mapping, and a hand-written pre-phase-90 file with none of the three keys still
  loading — an added field that broke existing parses would take a config directory down on upgrade.
- `mapping-metadata-cache.spec.ts` (new, 4, Playwright): stubbed at the network boundary for the
  reason `lag-monitoring.spec.ts` is — the states are properties of the payload, and producing them
  live means altering a schema between two assertions. Screenshots `62-cached-metadata-card.png` and
  `63-cached-metadata-refreshed.png`.
- Full suite: `Category!=Integration` **1192 passed, 18 failed**; `Category=Integration` **218 passed,
  0 failed**. Full Playwright **61 passed**. `tsc -b`, the SPA build and `oxlint` clean on every file
  this phase touched.

### Pre-existing failures, confirmed as such

The same 18 phase 86 recorded, unchanged and confirmed again by the clean-`main` run made while
chasing the golden-path failure above: `InviteCommandTests.Invite_AgainstAnMsSqlConfiguredRepo_
Succeeds`, the two `AdminConfigControllerTests` file-source tests, five Windows-only tests in
`DataSync.Certificates.Tests`, `CertificateExpiryServiceWindowsTests`, and the whole of
`AdminCertificateServiceWindowsTests`. None is in a file this phase touched.

### What this phase did not do

`BatchReloadReader.ExpandAutoSegmentsAsync` is untouched, as confirmed. No reader, writer or staging
provider reads these fields — every one of them still queries the live catalog on every pass, exactly
as before, so refreshing changes what is stored and changes no behaviour. Nothing backfills an
existing mapping's cache: it stays empty until the mapping is edited or somebody presses Refresh.
Switching the run-time consumers onto the cache is the deliberate next phase, and the thing that makes
it reviewable on its own is that this one shipped without it.
