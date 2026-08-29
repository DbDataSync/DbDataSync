# Rebuild the connections dialog on the parameter system

**Status: resolved 2026-08-28 — direction settled, two real design gaps flagged rather than guessed at.**

## The ask

Rebuild `ConnectionEditPage.tsx` to use the declarative parameter system (phase 42) throughout, not just
for the driver-declared extras it already covers.

## What's already migrated, and what isn't

`ConnectionEditPage.tsx` today is a partial migration. Its "Driver settings" card already renders
`driverCapabilities.connectionParameters` through `ParameterForm`, and the `properties` free-form bag
already travels as a `Property`-typed vararg parameter (flattened/unflattened at the edges) — this is
exactly phase 42's `property`-is-just-another-parameter design working as intended.

But `IDriver.ConnectionParameters` today only ever declares one thing:
`[DriverParameters.ConnectionProperties]` (`IDriver.cs`). Every other field — **Name, Address mode
(host/port vs. connection string), Host, Port, Driver, Database, Auth mode, User ID, Password** — is
still hand-written `<Field>` + `<input>`/`<select>` JSX in the page, exactly the hardcoding phase 42 set
out to remove. This doc is the plan for closing that gap: declaring those fields too, and rendering them
through `ParameterForm` like everything else.

## Two things the parameter system does not have yet, needed to do this properly

### 1. No masked/secret parameter type

`ParameterType` has no `Password`/`Secret` variant. `Control`'s `default` case renders a plain
`<input>` — migrating Password onto a generic `Text` parameter would show it in plaintext, a real
regression, not a cosmetic one. **This needs a new `ParameterType` value** (`Secret`, rendered as
`type="password"`) before Password can move onto the system at all. It should also carry the existing
"leave blank to keep the stored value" behavior — likely via the parameter's own semantics (a `Secret`
value is never round-tripped back to the client, so blank always means "unchanged" the same way it does
today) rather than a special case in the connection page.

### 2. Conditional/dependent field visibility — resolved: server-computed, not a client expression language

Today's Address field is a mode toggle: choosing "Host & port" vs. "Connection string" shows one set of
fields and hides the other. Auth mode does the same for User ID/Password. `ParameterDescriptor` has no
way to express this today — every declared parameter renders unconditionally.

**Decided against both options this doc originally weighed** (a client-side `visibleWhen` expression
language, or page-level filtering logic). Instead: the parameter *provider* — the driver's
`ConnectionParameters` — stops being a static list and becomes something invoked **with the current draft
values**, so the driver's own C# gets to decide what's visible using real code, not a condition
mini-language the client has to interpret. Two new fields on `ParameterDescriptor`:

- **`visible: bool`** — computed by the provider for *this* call, given the values it was handed. The
  client renders only parameters where this is `true`.
- **`recalc: bool`** — declared per parameter, meaning "if this parameter's value changes, ask the
  provider again" — because changing address mode is exactly the moment Host/Port's visibility needs to
  be recomputed, and the client has no other way to know which fields matter to that recomputation.

This is strictly more capable than a `visibleWhen` schema (arbitrary logic, not just equality checks) and
adds no new concept to the client beyond "some fields, when they change, mean re-ask the server" — no
expression language to design, parse, or keep in sync between client and server. It also generalizes past
connections for free: `ParameterDescriptor` is the same type scripts and handler settings already use, so
any future consumer gets `visible`/`recalc` too, even though nothing else needs it yet.

The cost is real and worth naming: the capabilities fetch stops being a static, cacheable-forever lookup
(`useCapabilities` today sets `staleTime: Infinity`) and becomes a call that must be re-issued — with the
current values — whenever a `recalc` field changes. That's a real shape change to the capabilities
endpoints and hooks, not just a rendering change, and phase 50 has to account for it.

## What becomes declared, and what stays structural

- **Declared** (via `IDriver.ConnectionParameters`, replacing the hardcoded fields): Address mode, Host,
  Port, Connection string, Database, Auth mode, User ID, Password (as `Secret`) — plus whatever a driver
  already contributes as extras. A driver's default port moves from the client's `DEFAULT_PORTS` constant
  into each parameter's own `default` value, which retires that lookup table entirely — a real instance
  of the hardcoding this system exists to remove.
- **Stays structural, not a parameter**: **Name** (the config's identity/key, not a setting) and
  **Driver** (the selector that decides which `ConnectionParameters` even apply — it can't be one of the
  things it's choosing between). Both keep their existing bespoke fields, including the "disabled after
  creation" behavior neither should lose.

## What this does not change

- The persisted `ConnectionInput` shape for Name/Host/Port/ConnectionString/Database/AuthMode/UserId —
  this is a rendering change, not a config-format migration. (Password already round-trips as "blank
  means unchanged.")
- `ConnectionTestCard`, `ScriptBindingsCard`, or anything about connection testing.

---

# Outcome

Agreed, as `implementation/todo/phase-050-connections-dialog-on-parameter-system.md`.
