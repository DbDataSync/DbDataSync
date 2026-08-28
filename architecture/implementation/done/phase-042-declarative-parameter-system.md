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

---

# Retrospective

Built as planned. The three open questions were all decidable from evidence already in the repo rather
than from taste, which is worth recording because the plan framed two of them as guesses.

## The connection's own fields do not move onto this, and that is not a compromise

The plan left it open. `host`, `port`, `database`, `authMode`, `userId` and `password` stay exactly
where they are, for a reason stronger than "it would be work": one of them is a **secret**. Phase 31
already found what happens when credential handling gets a general-purpose path — a rejected save had
already written a password to the secret store, because validation ran after the block that stored it.
Routing a credential through a generic `Record<string, string>` that a form builds and a validator
walks would be inviting that class of bug back in.

The rest of them are not driver-specific either: they are the shape of `ConnectionConfig`, they are
validated at save by `ValidateAddressing`, and every driver builds its connection from them. This
system is for what a driver needs **in addition** — which is exactly what the properties bag always
was, and is now declared rather than assumed.

## Handler settings are per-binding, confirmed against the real ones

The plan said "probably per-binding, but confirm against a real example". There are three real ones —
`snapshotIsolation`, `watermarkColumn`, `script` — and all three live in `ChangeProcessing.*.Options`,
which is per replication and inherited by every mapping under it. None has ever wanted a per-mapping
override, and nothing asked for one. Building an inheritance chain for them would have been building a
requirement rather than meeting one.

So values stay where they already are. This phase declares what they are; it does not move them.

## Declaring found a required setting nobody was told about

`WatermarkReader` throws *"the 'watermarkColumn' option is required"* — at run time, on the first pass,
after an operator chose the Kind and saved and waited. Declaring it `Required` means the save is
refused, naming it. That is not a new rule; it is the existing rule moved to where it can be acted on.

The same is true of a misspelling. `snapshotIsolatoin` used to save cleanly and then do nothing, which
from the operator's side is indistinguishable from the setting not working. It is refused now, naming
the key.

## Everything defaults to what it already meant

`Type` defaults to `Text`, `Cardinality` to a single value, `Layout` to nothing, `Required` to false.
A declaration that states only its name is a single optional text value — which is precisely what
every script parameter written before this system existed already was. That is what makes it additive:
no manifest migration, no version field, and `ScriptParameterDeclaration` could simply be replaced by
`ParameterDescriptor` because the former's three fields are three of the latter's.

## Nullable defaults, because config is committed

`Cardinality` and `Layout` are nullable rather than defaulted, and read through `Occurrences` and
`Placement`. YamlDotNet omits nulls; it does not omit a non-null instance that happens to equal a
default. Defaulted, every script manifest saved from then on would have gained
`cardinality: {min: 1, max: 1}` and `layout: {card: '', group: '', size: 1}` — noise in a git-committed
file that gets diffed in the Version Control tab.

And the computed properties are on neither wire. Serialized, they were written out and then failed to
load, having no setter to read back into — `HookSaveValidationTests` found that on the first run.

## A new connection had nothing to ask

`useCapabilities` is keyed on a saved connection, which was fine while the form hardcoded its fields
and stopped being fine the moment a driver started declaring them: a connection being created has no
name to look up. `GET /api/drivers/{driverType}/capabilities` is the question that screen actually has,
and it re-asks when the driver picker changes — which is the moment the settings on offer change.

## Three headings for one control

The properties declaration originally set `Layout.Card = "Custom properties"`, which rendered as a card
heading saying the same thing as the parameter's own label, inside a card already headed "Driver
settings". Seen in the screenshot the phase's verification asked for, which is what that verification
item is for. The declaration sets no card; a single parameter does not need one.

## What still renders the free-form table

A Kind that declares nothing, and a script manifest that declares nothing. Both are real: an option a
driver reads but has not got round to declaring is still an option somebody set, and every manifest
written before this phase declares nothing. Dropping the table for them would have been a migration
disguised as a UI change.

## Verification

- `ParameterValidationTests` (15) — required, whitespace-as-blank, each type's parsing, dropdown
  options named in the message, an undeclared key reported rather than dropped, every problem at once
  rather than the first, vararg keys found by prefix, min and max, the system ceiling capping an absurd
  declared max, each vararg value type-checked individually, and a bare declaration meaning single
  optional text.
- `ParameterCheckTests` (6) — the driver declaring its bag and its readers declaring their settings
  over the API; a save that honours them; a required setting missing, a wrong type, and a misspelled
  key each refused naming the offender; and a replication with no endpoints yet not checked against a
  driver it has not chosen.
- Playwright 11, rewritten: the change-tracking reader's setting is a labelled toggle with its
  description, not a key typed into a table, and the saved value is asked of the API rather than of the
  form that wrote it.
- Playwright 23 — all three callers of the one form: a driver's connection settings round-tripping
  through save, a script's parameters, and a stage's settings, plus the free-form table still there for
  a Kind that declares nothing.
- Full .NET suite green: 586 tests. Playwright: 25 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions, all three answered

- ~~**Do handler values inherit?**~~ No — per-binding, confirmed against the three that exist.
- ~~**Do the hardcoded connection fields migrate?**~~ No, and the credential is the reason.
- ~~**The vararg ceiling.**~~ 100. A guardrail against a manifest asking for a million, not a statement
  about what is reasonable — a parameter's own declared `max` still applies below it.

## What this leaves for phase 43

`ColumnPicker` renders as a dropdown and takes its choices from whatever screen hosts it; nothing
supplies them yet, because nothing hosts one — the watermark reader's is on the Overview, which has no
single mapping to resolve columns against. Phase 43's verification-query editor is the screen that
does, and wiring the choices there is its work.
