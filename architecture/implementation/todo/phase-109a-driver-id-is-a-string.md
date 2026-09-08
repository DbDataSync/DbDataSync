# Phase 109a — driver identity is a string (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md`. First phase of the group;
nothing depends on it being done a particular way except that it lands alone.

## What this builds

`ConnectionDriverType { MsSql, Postgres, DuckDb }` becomes a `string` driver id everywhere it is
used. No new capability — this is the prerequisite that lets a later phase register a driver that
was not compiled in.

The built-in ids stay exactly `"MsSql"`, `"Postgres"`, `"DuckDb"` — the strings the enum already
serialises to — so an existing `connection.yaml` round-trips byte-for-byte and nothing observable
changes for a running deployment.

### Core — `src/DbDataSync.Core/Config/`

- `ConnectionConfig.DriverType` and `ConnectionInput.DriverType` : `string`. Delete the enum.
- The YAML serializer already emits/reads the bare token (`driverType: MsSql`); a string maps to the
  same text. Confirm `YamlConfigSerializer` needs no converter change.
- `ConfigValidation` — reject an unknown id at save with a message that names the not-yet-real
  install command: *"Unknown driver 'oracle'. Install it with `dbdatasync driver install <package>`."*
  Until phase 109d there is no way to add one, so in practice this only ever fires on a typo.

### Abstractions — `src/DbDataSync.Drivers.Abstractions/DriverRegistry.cs`

- `Dictionary<ConnectionDriverType, …>` → `Dictionary<string, …>` (ordinal comparer).
- `Get` / `TryGet` / `FindReader` / `Describe` / `SupportsReader` / … take `string driverType`.
- `IDriver.DriverType` : `string`.
- `DriverCapabilities.DriverType` (and the SPA DTO it feeds) : `string`.

### API — the ~dozen consumers

`DriverConnectionFactory`, `ChangeSourceResolver`, `ParameterCheck`, `MetadataService`,
`ProvisioningService`, `PreviewService`, `SchedulerService`, `ResyncService`, the controllers that
surface capabilities — all currently pass `config.DriverType` (enum) into `DriverRegistry`. The
change is mechanical: the value is now a string and flows unchanged. `grep -rn "ConnectionDriverType"`
is the worklist.

### SPA — `src/DbDataSync.Web/src/`

- `types.ts` : `export type DriverType = string` (keep the alias for readability at call sites).
- The connection editor's driver `<select>` keeps its hard-coded `['MsSql','Postgres','DuckDb']` for
  now — phase 109d replaces it with a fetch from `GET /api/drivers`.
- Anywhere the SPA branches on `driverType === 'DuckDb'` (e.g. `MappingSide.tsx` swapping pickers for
  the query editor) still works — a string equality check is unchanged.

## What this phase does not build

- `StateEngine` → string. Same problem, but it has no external consumer until phase 109f and it is
  cleaner to move it alongside the `StateDialectRegistry` that gives it a reason to be open.
- `GET /api/drivers`, any loader, any new package reference.

## How to verify when built

- `dotnet build DbDataSync.slnx` clean; the full `dotnet test` suite green with no assertion
  changes — the point of this phase is that behaviour does not move.
- `npm run build` + `npm run lint` in `src/DbDataSync.Web`.
- **New — `ConfigValidationTests`**: a `connection.yaml` with `driverType: oracle` is rejected at
  save with the install-command message; `driverType: MsSql` still loads.
- **New — `DriverRegistryTests`**: register under `"MsSql"`, resolve by `"MsSql"`, and `TryGet`
  returns false for `"oracle"` rather than throwing.
- The golden-path Playwright suite green — it creates MsSql and Postgres connections through the UI
  and is the end-to-end proof the string round-trips through the API and back.

## Open questions

- Whether to keep a `DriverIds` static class of the three built-in constants, or let the strings
  float. Leaning: a `DriverIds` class, so a rename is one edit and the built-ins are greppable.
