# Phase 42 — A declarative parameter system for handlers, connections, and scripts (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/declarative-parameter-system.md`. Split out of the
sync-verification-queries plan as a predecessor; phase 43 depends on this landing first.

## The gap

Three places today each solve "an author declares settings, an operator fills them in" differently, or
don't solve it at all:

- **Connections.** `ConnectionEditPage.tsx` hardcodes `host`, `port`, `database`, `authMode`, `userId`,
  `password` regardless of driver, plus a free-form `properties: Dictionary<string,string>` rendered by
  `KeyValueTable.tsx` — the one place a driver-specific setting can already sneak in, untyped. A new
  driver whose connection needs differ means editing this page directly.
- **Reader / staging / writer settings.** `architecture.md` names "any custom reader settings", "any
  custom caching options", "any custom writer settings" as part of the concept and nothing implements it.
  `DriverCapabilities` (`ReaderCapability`, `StagingCapability`, `WriterCapability`) advertises *which*
  Kinds exist, never what a chosen Kind's own settings are.
- **Scripts.** A script parameter today is `{ name, type: string, required }` (`csharp-scripting-host.md`'s
  manifest) — no typed values, no column pickers, no vararg.

## The parameter descriptor

One C# type, defined once, consumed generically by the SPA — the same relationship `DriverCapabilities`
already has with the Kind pickers.

```csharp
public sealed record ParameterDescriptor(
    string Name,
    string Label,
    ParameterType Type,
    bool Required,
    ParameterCardinality Cardinality,          // Single, or Vararg(min, max)
    IReadOnlyList<string>? DropdownOptions,    // only for Type == Dropdown
    ParameterLayout Layout);                   // Card, Group, Size

public enum ParameterType { Text, Number, Bool, Date, DateTime, Dropdown, ColumnPicker, Property }

public sealed record ParameterCardinality(int Min, int Max);   // Min == Max == 1 for a single value

public sealed record ParameterLayout(string Card, string Group, double Size);
```

`ColumnPicker` resolves against a table mapping's column mappings, not raw source/target columns — see
phase 43 for why (an aliased column stays one selection). `Property` is a key/value pair; a connection's
`properties` bag is simply a `Vararg` `Property` parameter, declared by the driver like any other —
not a special case.

## Where parameters are declared

- **Connection drivers**: `DriverCapabilities` gains `IReadOnlyList<ParameterDescriptor> ConnectionParameters`.
  A driver declares its `properties` bag as one `Property`-typed vararg entry here, rather than the SPA
  assuming every connection has one.
- **Readers / staging providers / writers**: `ReaderCapability`, `StagingCapability`, `WriterCapability`
  each gain `IReadOnlyList<ParameterDescriptor> Parameters`.
- **Scripts**: the manifest's parameter list moves from `{ name, type: string, required }` to
  `ParameterDescriptor`. Existing scripts with string-typed parameters keep working — `Text` is the
  default type, so this is additive, not a breaking migration.

## SPA rendering

- A new generic `ParameterForm` component (beside `Field.tsx` and `KeyValueTable.tsx`) that renders a
  list of `ParameterDescriptor` + current values, grouped by `Card`/`Group`, sized by `Size` (flex
  units).
- `KeyValueTable` becomes `ParameterForm`'s default renderer for `Property`-typed parameters, not a
  connection-specific component anymore.
- `ColumnPicker` rendering needs the current table mapping's column-mapping list from the API — the same
  data `MappingSide.tsx` already has, exposed to whatever screen hosts the picker (verification-query
  editor, phase 43).
- Connection editing, handler settings, and script binding parameter forms all become callers of the same
  `ParameterForm`, rather than three separate form implementations.

## What this phase does not build

- **Migrating the existing hardcoded connection fields** (`host`, `port`, `database`, `authMode`,
  `userId`, `password`) onto this system. Left open below.
- **Actually wiring declared reader/staging/writer parameter values into runtime behavior.** This phase
  builds declaration, persistence, and UI rendering of the values; a handler consuming its own declared
  settings is that handler's own change, made whenever a real customization need shows up. Nothing
  currently asks for one beyond the `architecture.md` mention.
- **Verification queries themselves** — phase 43, which consumes this.

## How to verify when built

- A driver capability with a declared `Text`/`Number`/`Dropdown` parameter renders in `ParameterForm` and
  round-trips through save.
- A `Vararg` `Property` parameter renders as a key/value table and round-trips, replacing the existing
  hardcoded connection `properties` UI without a visible behavior change to the operator.
- A `Vararg` parameter's `min`/`max` is enforced (client-side at minimum; server-side validation on save).
- `Card`/`Group`/`Size` layout hints produce the intended grouping — a Playwright screenshot check, since
  this is explicitly a visual-quality goal and not just correctness.
- Existing scripts with string-typed parameters still compile, bind, and run unchanged.
- Full suite green.

## Open questions

Carried forward from the planning doc, not resolved here:

- Whether reader/staging/writer parameter *values* flow through `ScriptResolution`'s inheritance
  (connection → replication → mapping) or are simpler and per-binding — most handler settings are
  probably per-binding, but this should be confirmed against a real example, not assumed.
- Whether the existing hardcoded connection fields migrate onto this system too, or stay fixed with only
  driver-specific extras declared this way. Needs a decision before or during implementation, not left
  ambiguous in the shipped code.
- The system-wide vararg cardinality guardrail's actual number (the ceiling that stops any one parameter
  from asking for an absurd count, independent of that parameter's own declared `max`).
