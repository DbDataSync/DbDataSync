# Phase 109a — driver identity is a string

**Status**: Done.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md`. First phase of the group;
nothing depended on it being done a particular way except that it landed alone.

## What this built

`ConnectionDriverType { MsSql, Postgres, DuckDb }` is gone. `ConnectionConfig.DriverType` and
`ConnectionInput.DriverType` are `string`; `DriverRegistry` re-keys on `string` (ordinal comparer);
`IDriver.DriverType` and `DriverCapabilities.DriverType` are `string`. A new
`DbDataSync.Core.Config.DriverIds` static class carries the three built-in ids (`MsSql`, `Postgres`,
`DuckDb`) as `const string`, replacing the enum's members at every call site — a rename is still one
edit, and the built-ins stay greppable.

The built-in ids stayed exactly `"MsSql"`, `"Postgres"`, `"DuckDb"` — the strings the enum already
serialised to — so an existing `connection.yaml` round-trips byte-for-byte and nothing observable
changed for a running deployment. `YamlConfigSerializer` needed no converter change: it never had a
custom enum converter, so a string maps to the same text it always emitted.

### Mechanical worklist

`grep -rn "ConnectionDriverType"` was the worklist, exactly as the plan predicted: ~50 files across
`src/`, `tests/`, and `tools/DbDataSync.DevHarness` (missed on the first pass — it isn't under `src/`
or `tests/`, and the plan doc didn't call it out). Every production consumer
(`DriverConnectionFactory`, `ChangeSourceResolver`, `ParameterCheck`, `MetadataService`,
`ProvisioningService`, `SchedulerService`, `ResyncService`, the controllers) already just flowed
`config.DriverType` into `DriverRegistry` — the type changed under them with no logic change. Two
`driver.DriverType.ToString()` calls (`ScriptedQueryRegistration`, `ScriptDialectAdapter`) — written
when `DriverType` was an enum and needed converting — became dead no-ops and were simplified to just
`driver.DriverType`.

`ProvisioningService.ResolveDialect`'s `switch` on the enum became a `switch` on the same values as
`DriverIds` `const string`s — C# pattern-matches a `switch` on string constants exactly like an enum,
so the switch body didn't change shape, only the type name and the case labels.

### `ConfigValidation` — the real behavior change

The phase doc's "reject an unknown id at save" turned out to already have a home:
`DbDataSync.Api.Services.ParameterCheck.ThrowIfInvalid(ConnectionInput)` already looked the driver up
in `DriverRegistry` before validating its parameters — but on a miss it silently `return`ed instead of
rejecting. (`ConfigValidation` itself, in `DbDataSync.Core`, can't do this check: `DriverRegistry`
lives in `DbDataSync.Drivers.Abstractions`, which references `Core`, not the other way around —
`ParameterCheck` in the API layer is the seam where both are in scope, per its own doc comment.) Fixed
to throw `ConfigValidationException($"Unknown driver '{input.DriverType}'. Install it with
`dbdatasync driver install <package>`.")`, caught by `ConnectionsController.Upsert` into a 400 exactly
like every other `ConfigValidationException`.

### SPA

`types.ts`: `DriverType` is now `export type DriverType = string` (kept as an alias for readability
at call sites, per the plan). The connection editor's driver `<select>` keeps its hard-coded
`['MsSql','Postgres','DuckDb']` for now, unchanged — `GET /api/drivers` is 109d's job. Every
`driverType === 'DuckDb'`-style branch (e.g. `MappingSide.tsx`) is untouched: string equality, same
as before.

## Decisions and real bugs found

- **`tools/DbDataSync.DevHarness` was not in the plan doc's worklist** (it names only `src/` and
  `tests/`) but references `ConnectionDriverType` in two files and would not have compiled. Caught by
  `dotnet build DbDataSync.slnx` failing after the first pass — the tool already references
  `DbDataSync.Core`, so it picked up `DriverIds` for free.
- **`ConnectionsController.ConnectionParameters`'s route parameter going from `ConnectionDriverType` to
  `string` changed a real, user-visible behavior**, not just a type: ASP.NET model binding used to
  reject an unparseable enum route value before the action ran, surfacing as an automatic 400. A
  string always binds, so the request now reaches the handler's own `driverRegistry.TryGet` check,
  which was already there and returns 404 with `{ error: "No driver is registered for '...'." }`. One
  existing test (`ConnectionParametersTests.AnUnregisteredDriver_IsNotFound`) asserted the old
  incidental 400 despite its own name already describing the 404 behavior; updated to assert 404 and
  documented why in a comment, rather than adding a redundant explicit check to preserve the old status
  code for a case the endpoint already answers correctly.
- **`DriverRegistry`'s two dictionaries now use `StringComparer.Ordinal`** explicitly (the plan's "no
  new capability" framing didn't call this out, but a driver id is not something to case-fold).

## How it was verified

- `dotnet build DbDataSync.slnx` clean; `npm run build` + `npm run lint` (in `src/DbDataSync.Web`)
  clean — lint's pre-existing warnings are unrelated (`set-state-in-effect`, an `exhaustive-deps`
  miss).
- Full `dotnet test` suite, both non-integration and `Category=Integration` (against the running
  `mssql-source`/`mssql-target`/`postgres` containers): all green, one assertion updated
  (`ConnectionParametersTests`, above) to match a real, correct behavior change rather than to paper
  over a regression.
- **New — `ParameterCheckTests.AConnectionNamingAnUnregisteredDriver_IsRefusedNamingTheInstallCommand`**:
  saving a connection with `DriverType = "oracle"` is refused 400, body names `oracle` and
  `dbdatasync driver install`.
- `DriverRegistryTests` (`tests/DbDataSync.Drivers.MsSql.Tests/`) already covered "register under
  `DriverIds.MsSql`, resolve by it, `TryGet` returns `false` rather than throwing for an unregistered
  id" — it existed before this phase (as an enum-keyed test) and needed only the mechanical string
  conversion, so no new file was needed for that half of the phase doc's ask.
- Golden-path Playwright suite (`tests/DbDataSync.Web.Tests`, 94 specs against the real API + worker +
  containers): 92 passed. Two failures — `monitoring-restructure.spec.ts` test 01 and
  `runs-watermarks-refresh.spec.ts` test 03 — reproduce on `main` unrelated to this change (a
  redirect-timing race and a 10-second polling-cadence count assertion); both pass individually and
  under `--repeat-each=2` in isolation, confirming pre-existing flakes rather than a driver-id
  regression.

## What's explicitly out of scope / not built

- `StateEngine` → string, `StateDialectRegistry` — phase 109f.
- `GET /api/drivers`, any loader, any new package reference, the connection editor reading a live
  driver list — phase 109d.
- Adopting `GenericDriver` (doesn't exist yet) for `PostgresDriver` — phase 109b, optional even then.

## Open questions — resolved

- **`DriverIds` static class vs. floating strings**: built, as leaned. It lives in
  `DbDataSync.Core.Config` beside `ConnectionConfig` (where the enum used to live), not in
  `DbDataSync.Drivers.Abstractions` — every one of its consumers already depends on `Core`, and
  `Abstractions` depends on `Core`, so this is the lower, shared home.
