# Phase 182N: tell the operator when a connection's driver failed to load, instead of a silently empty form

**Status: done (2026-09-23).** Fifth and last of five phases for the JDBC/driver-editing UI round
requested 2026-09-23.

## What happens today, traced through the actual code

A driver whose `driver.yaml` fails to parse or build is never registered — `DriverLoader.LoadDescriptorDrivers`
catches, logs to stderr via `onError`, and skips it (`DriverLoader.cs:39-46`); this is deliberate, so one
broken driver doesn't take the whole host down. But nothing about a *connection* that already points at
that driver type knows this happened. Opening `ConnectionEditPage` for such a connection:

- `useDrivers()` won't list it (`DriverRegistry.All` only has registered drivers) — the "Driver" `<select>`
  shows `draft.driverType` as a value with no matching `<option>`, rendering blank/unselected with no
  explanation.
- `useCapabilities(name)` hits `GET /api/connections/{name}/capabilities`
  (`ConnectionsController.cs:58-74`), which 404s (`driverRegistry.Describe` returns `null`,
  `DriverRegistry.cs`'s own `Describe` doc). `canTest`/`canValidateLibrary` both silently resolve `false`
  — the buttons just don't appear, no different from a driver that simply doesn't support testing.
- `useConnectionParameters(...)` hits `POST /api/connections/parameters` (or the equivalent capability
  lookup), which **already returns a real, specific error**: `NotFound(new { error = $"No driver is
  registered for '{driverType}'." })` (`ConnectionsController.cs:117-118`). `ConnectionEditPage.tsx:144`
  destructures `const { data: declaredParameters = [] } = useConnectionParameters(...)` — the query's
  `.error` is never read, so this real, already-informative message is discarded and the page just shows
  an empty `ParameterForm` with no fields and no hint why.

So the actual problem isn't "no error exists" — one already comes back from the network today — it's that
the page throws it away, and even if it didn't, "No driver is registered for 'X'" doesn't say *why*, which
is what an operator actually needs to go fix it.

## What ships

**1. Surface the existing error.** `ConnectionEditPage` reads `useConnectionParameters`'s `error` (not just
`data`) and, when present, shows a banner above the `ParameterForm` instead of silently rendering it
empty — reusing `ErrorBanner`, already imported in this file. This alone is a small, real improvement with
no backend change: the "No driver is registered" message stops being swallowed.

**2. A real reason, not just "not registered."** Add `GET /api/drivers/{id}/status` (`DriversController`,
`[Authorize(Policies.Viewer)]` — matching every other endpoint `ConnectionEditPage` already calls, per
`ConnectionsController`'s own consistent `Viewer` policy on `Capabilities`/`Parameters`/etc.):

```csharp
[Authorize(Policies.Viewer)]
[HttpGet("{id}/status")]
public ActionResult<DriverStatusResult> Status(string id)
{
    if (driverRegistry.TryGet(id, out _))
        return Ok(new DriverStatusResult(true, null));

    var yamlPath = Path.Combine(apiOptions.RepoRoot, "drivers", id, DriverLoader.DescriptorFileName);
    if (!System.IO.File.Exists(yamlPath))
        return Ok(new DriverStatusResult(false, $"No driver.yaml exists for '{id}'."));

    var (_, _, error) = TryBuild(System.IO.File.ReadAllText(yamlPath));
    return Ok(new DriverStatusResult(false, error ?? $"'{id}' failed to register for an unrecorded reason — check the server log."));
}

public sealed record DriverStatusResult(bool Registered, string? Error);
```

Reruns the exact same `TryBuild` (`DriversController.cs:158-183`) `Create`/`UpdateYaml`/181N's `Validate`
already share — so the message an operator sees here is identical to what they'd see fixing it through the
driver editor, not a third, differently-worded version of the same fact. Cheap: one directory, one file
read, only called when a connection's driver isn't already in the registered list (not a background scan,
not run for every driver on every page load).

**3. Wire it into `ConnectionEditPage`.** A new `useDriverStatus(driverType)` hook
(`api/hooks.ts`, `useQuery`), called only when `!isNew && draft.driverType` is truthy — its result feeds a
banner, above the "Connection" card, shown only when `registered === false`:

> This connection's driver **`{driverType}`** failed to load: *{error}*. Fix it in the
> [driver editor](/drivers/{driverType}/edit) — 181N's Validate tool there can confirm the fix before you
> save.

The link to the driver edit page is the actual point — this doc's whole premise is that today's failure
mode is a dead end with no path forward. Linking straight to `/drivers/{id}/edit` puts the operator exactly
where 178N-181N's other four phases just made editing and diagnosing that file tractable.

**4. Don't hide the rest of the form.** The connection's own saved values (name, host, port, etc., whatever
is in `existing`) should stay visible/editable even while the driver is broken — an operator may need to
correct or delete this connection regardless of the driver's state, and "Delete" already works
unconditionally today. Only the driver-dependent parts (the parameter form's fields, Test, Validate
library) are the ones with nothing to show.

## Out of scope here

- A "broken drivers" list on `DriversPage`/`ConnectionsPage` (a listing-level indicator) — this phase is
  scoped to the moment named in the request: opening a connection whose driver is broken, not a general
  dashboard of driver health across the whole console.
- Retrying/reloading a fixed driver without a server restart — `restartRequired.Touch()`'s existing
  contract (`Create`/`UpdateYaml` already call it) is unchanged; fixing the file through the editor and
  saving already re-registers it live (`driverRegistry.Register(driver!)` in both, `DriversController.cs:110,142`)
  — this phase doesn't need to add anything for that case, it already works once the operator *can* find
  and fix the file, which is what 178N-181N + this phase's link exist to make possible.

## Applied — one simplification from the design above

Item 1 ("surface the existing error") and item 2 ("a real reason") were designed as two separate steps,
cheapest first. Implemented as one: once `GET /api/drivers/{id}/status` exists, showing its richer,
already-consistent-with-the-driver-editor message supersedes showing the bare "No driver is registered
for 'X'" from `useConnectionParameters` — displaying both would just be two differently-worded messages
for the same fact. The banner only renders once `useDrivers()` has confirmed the driver truly isn't
registered (never on a normal page load before that list arrives), so it costs nothing for the common
case and never flashes.

## How to verify when closed

- A connection pointing at a driver id with no `driver.yaml` at all shows "No driver.yaml exists for
  'X'.", not a generic 404.
- A connection pointing at a driver id whose `driver.yaml` fails to build (e.g. references a `base` type
  that can't resolve) shows that build error's own message, matching what `Create`/`UpdateYaml`/`Validate`
  (181N) would show for the same file.
- The connection's own name/host/port/etc. fields remain visible and editable while this banner is shown.
- A connection whose driver loads fine shows no banner and behaves exactly as before this phase.
