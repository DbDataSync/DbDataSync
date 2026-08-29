# Phase 50 — Rebuild the connections dialog on the parameter system (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/connections-dialog-on-parameter-system.md`

## What this covers

Finish migrating `ConnectionEditPage.tsx` onto phase 42's `ParameterForm`/`ParameterDescriptor` — today
only the "Driver settings" card (the free-form `properties` bag) uses it; every other field is still
hand-written JSX.

## 1. New `Secret` parameter type

- `ParameterType` (`DataSync.Drivers.Abstractions` and `types.ts`) gains `Secret`.
- `ParameterForm`'s `Control` gets a case rendering `type="password"`, matching the existing
  `connection-password-input`'s behavior and placeholder ("Leave blank to keep existing").
- Server-side: a `Secret` parameter's stored value is never sent back to the client on load — the same
  "blank means unchanged" contract `password: ''` already has today, generalized so any future secret
  parameter (not just the connection password) gets it for free rather than reinventing it per field.

## 2. Declare the rest of a connection's fields

`IDriver.ConnectionParameters` (currently `[DriverParameters.ConnectionProperties]` only) grows to
declare, for both MsSql and Postgres:

- **Address mode** — a `Dropdown` (`host` / `connectionString`).
- **Host**, **Port** (`Number`, `default` set per driver — retires the client's `DEFAULT_PORTS` constant),
  **Connection string** (`Text`).
- **Database** (`Text`).
- **Auth mode** — a `Dropdown` (`SqlAuth` / `IntegratedAuth` / `None`).
- **User ID** (`Text`), **Password** (`Secret`).
- Existing driver-declared extras stay exactly as they are today.

**Name and Driver stay out of this list** — Name is the config's identity, not a setting; Driver is the
selector that decides which parameter set applies, so it can't be declared alongside what it selects.
Both keep their current bespoke `<Field>` treatment, including "disabled after creation."

## 3. Conditional visibility: `visible`/`recalc` on `ParameterDescriptor`, computed server-side

- **`ParameterDescriptor` gains two fields** (`DataSync.Drivers.Abstractions` and `types.ts`):
  `visible: bool` and `recalc: bool`. Both default `true`/`false` respectively for every existing
  declaration that doesn't care about this (a parameter with no dependency is always visible and never
  needs a recompute).
- **`IDriver.ConnectionParameters` changes from a static property to a method taking current values**:
  `IReadOnlyList<ParameterDescriptor> ConnectionParameters(IReadOnlyDictionary<string, string> values)`.
  MsSql/Postgres's implementations inspect `values["addressMode"]` / `values["authMode"]` and set
  `visible` accordingly on Host/Port/ConnectionString and User ID/Password. Address mode and Auth mode
  themselves are declared `recalc: true`; everything else defaults `false`.
- **The capabilities endpoint(s) become values-aware.** `GET /api/drivers/{type}/capabilities` and
  `GET /api/connections/{name}/capabilities` need to accept the current parameter values (query string or
  a small POST body) and pass them through to `ConnectionParameters`. `useDriverCapabilities`/
  `useCapabilities` lose their `staleTime: Infinity` (capabilities are no longer static per driver/
  connection — they depend on live draft values) and their query keys need to include whichever values
  matter, so a re-fetch actually happens when a `recalc` field changes.
- **Client behavior**: `ConnectionEditPage` re-fetches capabilities whenever a parameter marked `recalc`
  changes (debounced if needed), replaces `declaredParameters` with the fresh response, and `ParameterForm`
  filters rendering to `parameter.visible !== false` — no client-side condition logic, no expression
  language, just "render what the server currently says is visible."

## 4. `ConnectionEditPage.tsx` rewrite

- Remove the hand-written Connection/Authentication card JSX for the fields now declared (Address mode,
  Host, Port, Connection string, Database, Auth mode, User ID, Password).
- Render them through `ParameterForm`, same pattern the Driver settings card already uses — likely
  merging into fewer cards now that layout is declared via `ParameterLayout` (`card`/`group`/`size`)
  rather than hardcoded JSX structure. Use `ParameterLayout` to reproduce today's actual layout (Host+Port
  sharing a row, User ID+Password each their own) rather than guessing at a new one.
- Name and Driver remain as their own small dedicated fields, unchanged in behavior.
- `flattenProperties`/`unflattenProperties` stay for the `properties` vararg specifically; the newly
  declared fields map directly to `ConnectionInput`'s existing top-level fields (no flattening needed —
  they're not a vararg).

## What this phase does not build

- `recalc`/`visible` support for any *other* `ParameterDescriptor` producer (scripts, reader/staging/
  writer settings) — the fields exist on the shared type now, but only connections actually sets or reads
  them non-trivially in this phase.
- Any change to `ConnectionInput`'s persisted shape, connection testing, or script bindings.
- Migrating any other screen's fixed fields onto the parameter system — scoped to connections only.

## How to verify when built

- Creating a new MsSql connection and a new Postgres connection both render the same fields as today,
  in the same effective layout, through `ParameterForm`.
- Switching address mode triggers a capabilities re-fetch (address mode declared `recalc: true`) and
  hides/shows Host+Port vs. Connection string exactly as today, driven by the server's `visible` answer.
- Switching auth mode triggers the same recompute and hides/shows User ID/Password exactly as today.
- A parameter with no dependency (e.g. Database) never triggers a re-fetch and is always visible.
- Password renders masked, is never pre-filled on load, and "leave blank" still means "keep the stored
  value" on save.
- A driver's declared default port pre-fills Port on a new connection, with `DEFAULT_PORTS` removed from
  the client.
- Name and Driver still disable after creation; everything else remains editable.
- Full suite green, including updated Playwright screenshots for the connection edit screen (both new
  and existing-connection states).

## Open questions

- Exact `ParameterLayout` card/group breakdown to match today's two-card (Connection / Authentication)
  visual structure, or whether one restructured card reads better now that layout is declared — a design
  call for implementation, not blocking the rebuild itself.
- Whether the capabilities re-fetch on `recalc` should debounce, and by how much — a text field being
  marked `recalc` (none are, today) would otherwise refetch on every keystroke; the two real cases here
  are both dropdowns, so this may be moot for phase 50 itself but worth deciding before a future `recalc`
  parameter is a free-text field.
- Whether the capabilities endpoint takes values via query string or a small POST body — GET-with-query
  is more cacheable but a values dictionary doesn't fit a query string as cleanly as a POST body would.
