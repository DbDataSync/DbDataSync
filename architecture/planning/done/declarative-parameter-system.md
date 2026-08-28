# A declarative parameter system for handlers, connections, and scripts

**Split out of `add-support-for-sync-verification-queries.md` as a predecessor (2026-08-28).** That doc
needed column pickers, typed parameters, and vararg cardinality for verification-query scripts. The same
shape is wanted in three more places, so it should be built once, generally, and consumed everywhere —
not built narrowly for verification queries and then rebuilt three more times.

## Where this applies

- **Connection editing.** `ConnectionEditPage.tsx` today hardcodes `host`, `port`, `database`,
  `authMode`, `userId`, `password` as fixed fields regardless of driver, plus a free-form
  `properties: Dictionary<string,string>` rendered as a generic key/value table (`KeyValueTable.tsx`) —
  the one place a driver-specific setting can already sneak in today, untyped. Adding a driver whose
  connection needs differ (a different auth shape, a required extra field) means editing this React
  page directly.
- **Reader / staging / writer ("Source, Staging, and Target handler") customization.** `architecture.md`
  already names this need — "any custom reader settings", "any custom caching options", "any custom
  writer settings" — and nothing implements it. `DriverCapabilities` today only advertises *which*
  readers/staging/writers exist (`ReaderCapability`, `StagingCapability`, `WriterCapability`), not what
  each one's own settings are.
- **Scripts**, verification queries included. Today a script parameter is `{ name, type: string,
  required }` (`csharp-scripting-host.md`'s manifest). Verification queries need column pickers
  (resolved against a table mapping, not raw columns — see that doc), typed parameters, and
  vararg-with-cardinality parameters. Row transforms and other script kinds would benefit from the same
  richness even though nothing has asked for it yet there.

## The shape

One declarative parameter description, defined once in C# by whoever exposes it (a driver, a reader, a
script), consumed generically by the SPA to render a form — the same relationship `DriverCapabilities`
already has with the Kind pickers: **the UI adapts to what's declared; nothing is hardcoded per driver
or per handler.**

A parameter carries:

- **Type** — text, number, bool, date, datetime, dropdown (enumerated text options), column picker
  (resolved against a table mapping — see the verification-queries doc for why mapping and not raw
  source/target columns), and **property** (a single key/value pair — its own type because a key is as
  open-ended as its value, so it isn't just "text, vararg"). Nothing about `property` is connection-specific:
  a connection's `properties` bag is simply **a vararg parameter of type `property`**, declared by the
  connection driver the same way any handler declares any other parameter. The UI's existing
  `KeyValueTable` rendering becomes that type's default renderer — not a connection special case, just
  the one place it's used today.
- **Required or optional**, per parameter.
- **Cardinality** — a single value by default; a vararg parameter additionally declares `min`/`max`
  count, replacing any fixed "up to N" limit with a per-parameter bound (plus a small system-wide
  guardrail so a form can't be built asking for something absurd like 100 values).
- **Layout hints**, so the generic renderer is also a *good* one, not just a correct one:
  - **Card** — which header-grouped section a parameter appears under.
  - **Group** — which line/row within a card it shares with other parameters.
  - **Size** — relative width within its group, in flex units, so related short fields (e.g. a number
    and a unit dropdown) can sit naturally side by side instead of each claiming a full row.

## Why this generalizes cleanly

`DriverCapabilities` already established the precedent this follows: a capability object is queried at
runtime and the SPA's pickers are built from it, so a new driver or a new reader Kind needs no SPA
change to appear correctly. A parameter schema is the same idea one level down — instead of *which
Kinds exist*, it's *what a chosen Kind (or driver, or script) needs filled in*. Connections, handler
settings, and script parameters are three different owners of the same question, which is why one form
renderer and one C# description type should serve all three rather than three form implementations
converging on each other by accident.

There's no special-casing needed for the "settings nobody anticipated" case either: a driver that wants
an open-ended bag for connection-string flags and vendor quirks just declares a `property`-typed vararg
parameter with a high or unbounded max, same mechanism as everything else. What made it feel special
before was that it's the *only* place this exists today — not that it needs different plumbing.

## Relationship to verification queries

`add-support-for-sync-verification-queries.md` depends on this rather than defining its own parameter
model — its column-picker, typed-parameter, and vararg needs are exactly this system's first three real
consumers, and its "still open" question of "scoped narrowly or built generally" is answered here:
**built generally**, with verification queries as the first slot to use it, not the only one.

## Still open

- Whether reader/staging/writer settings, once declared this way, also flow through `ScriptResolution`'s
  inheritance shape (connection → replication → mapping) or are simpler — most handler settings are
  probably per-binding, not layered.
- The generic form renderer's actual component design in the SPA (a `ParameterForm` beside `Field` and
  `KeyValueTable`, presumably) — implementation detail once the C# description type is settled.
- Whether existing hardcoded connection fields (`host`, `port`, `database`, `authMode`, `userId`,
  `password`) migrate onto this system too, or stay as fixed fields with only driver-specific *extras*
  declared this way. Migrating them gets consistency; keeping them fixed avoids touching a screen that
  already works. Worth a sentence before the implementation plan is written.

**Next step**: settle the C# description type (a record, mirroring `DriverCapabilities`'s style) and the
"still open" questions above, then this and the verification-queries doc are both ready for phase docs —
this one first, since verification queries builds on it.

---

# Outcome — resolved 2026-08-28

Agreed, as `implementation/todo/phase-042-declarative-parameter-system.md`. Builds first, ahead of
verification queries, since that doc's own parameter needs are this system's first consumer rather than
a separate design.

The "still open" questions above were not resolved here — they're carried into the phase doc as open
questions, to be settled during implementation rather than guessed at in advance:

- Whether handler (reader/staging/writer) parameter values flow through `ScriptResolution`'s inheritance
  or are simpler and per-binding.
- Whether the existing hardcoded connection fields migrate onto this system, or stay fixed with only
  driver-specific extras declared this way.
- The system-wide vararg cardinality guardrail's actual number.
