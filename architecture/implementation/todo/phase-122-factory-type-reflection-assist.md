# Phase 122 — `factoryType` reflection-assist for non-catalog libraries (planned)

**Status**: Planned, not started — **follow-on, deferred until phases 116–120 are in production.**
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*Follow-on work* → *`factoryType` reflection-assist*, Decision 5. Depends on phase 120 (the install
endpoint and the non-catalog path).

## Why

Phase 120 requires the operator to paste the assembly-qualified `DbProviderFactory` type name
(`"Namespace.Factory, Assembly"`) for any library not in `KnownLibraries` — matching the CLI, and
honest ("we don't know this package"). But for a package that follows the ADO.NET convention (a
single public `DbProviderFactory` subclass with a static `Instance` field), that string is
mechanically derivable. This phase pre-fills it, leaving the operator to confirm or override.

## What this builds

- After `LibraryInstaller` restores a non-catalog package, before registration: load the primary
  assembly in an inspection-only context and scan for public `DbProviderFactory` subclasses.
  - Exactly one → pre-fill the `factoryType` field with its assembly-qualified name; the operator
    confirms.
  - Zero or more than one → the operator types it, as in phase 120.
- The scan is inspection-only (`MetadataLoadContext` or an isolated `AssemblyLoadContext`) so a
  malformed or hostile assembly can't execute during discovery — and the trust confirmation from
  phase 120 has already been accepted before the restore ran, so this adds no new trust surface.
- `config library install` gains the same assist: with no `--factory-type` and none in
  `KnownLibraries`, try reflection before failing.

## How to verify when built

- Unit test with a small fixture assembly carrying one `DbProviderFactory` subclass → the
  assembly-qualified name is discovered; a fixture with two → discovery returns "ambiguous" and the
  field is left for the operator.
- Integration: `POST /api/libraries` for `MySqlConnector` **without** `factoryType` and with
  `MySqlConnector` temporarily absent from `KnownLibraries` → the install completes, factory
  resolves, `SELECT 1` against the container works.
- The `MetadataLoadContext` path does not execute a module initializer (a fixture that would throw
  on load proves it).

## What this does not build

- Guessing anything beyond the factory type — connection-string key names, dialect, type maps stay
  the operator's problem for a non-catalog engine (that's what `KnownDrivers` is for).
- Removing the manual field — it stays, pre-filled.

## Open questions

- `MetadataLoadContext` needs a resolver over the restored closure + the framework; confirm the
  `.deps.json` phase 109c produces is enough to build one, or whether a simpler
  "isolated ALC, reflect, unload" is more robust.
- Whether to also verify the discovered factory actually instantiates (`Instance` field present and
  non-null) before offering it — leaning yes, it's cheap and catches an abstract-only match.
