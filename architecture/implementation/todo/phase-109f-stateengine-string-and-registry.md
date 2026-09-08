# Phase 109f — StateEngine as a string, and a StateDialect registry (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*Custom state dialects as
an extension*. Depends on 109a (the same enum→string move, applied one layer down) and 109c (the
provider layer, for the non-built-in path). **First phase below the line — but it removes no
dependency.** It is the seam that 109g and a future custom state backend need.

## What this builds

`StateEngine { Sqlite, MsSql, Postgres }` becomes a `string` id, and `StateDialect.For(engine)`'s
`switch` becomes a registry — so a `StateDialect` can be *registered* rather than added to a closed
list. The three built-in dialects still register themselves in code and still hard-reference their
providers; nothing about a running deployment changes.

### `src/DbDataSync.State/`

- `StateEngine.cs` — delete the enum; a `string` id (`"Sqlite"`, `"MsSql"`, `"Postgres"`).
- `StateDialect` — `Engine` : `string`. `For(string)` → a lookup on a `StateDialectRegistry`.
- `StateDialectRegistry` (new) — mirrors `DriverRegistry`: `Register(StateDialect)`, `Get(id)`,
  `TryGet(id)`. Built-ins registered by a static initialiser or by the host at startup.
- `StateDatabase` / `StateDatabase.Factory` — take the resolved `StateDialect` (already do) and,
  for a non-built-in id, obtain the connection factory from `ProviderRegistry` instead of the
  dialect's own `CreateConnection`. Built-in dialects keep `CreateConnection` as-is for this phase.
- `ApiOptions.StateEngine` : `string`; `StateEngine.cs`'s parse/validate updated. An unknown id at
  startup fails with a message naming `provider install` / the dialect that is missing.

### `StateDialect` as the extension contract

Document that a custom state dialect is a compiled `StateDialect` subclass (there is no descriptor
for it — see the plan doc). The abstract surface it fills is already stable and small:

- mechanical: `Sql` (compose a `SqlDialect`), `ParameterName`, `Limit`;
- structural: `InsertOrIgnore`, `Upsert`, `IdentityKey`, `Text`, `KeyText`, `Integer`, `AddColumn`,
  `DropIndex`;
- bookkeeping: `GetSchemaVersion`, `SetSchemaVersion`, `OnConnectionOpened`, `ShouldRetry`.

The `Migrations` token set (`{{text}}`, `{{key}}`, `{{int}}`, `{{identity:Id}}`, `{{addcolumn}}`,
`{{dropindex}}`) is what a new dialect renders — no migration file changes.

## What this phase does not build

- Removing `Microsoft.Data.SqlClient` / `Npgsql` from `DbDataSync.State.csproj` — 109g.
- Loading a `StateDialect` from a NuGet package — that reuses 109e's compiled-plugin loader and is
  folded into 109h (or a small follow-up) once there is a real reason to want one.
- Any change to SQLite, which stays hard-referenced and keeps its own constructor path.

## How to verify when built

- `dotnet build` clean; the existing state-store suite green with no assertion changes —
  `tests/DbDataSync.State.Tests` (~45) and the cross-engine tests in `DbDataSync.Api.Tests`.
- **New — `StateDialectRegistryTests`**: the three built-ins resolve by their string id; an unknown
  id throws a clear error; a fixture `StateDialect` (renders SQLite-shaped SQL) can be registered
  and `StateDatabase` runs the full migration set against it.
- A deployment configured `StateEngine: MsSql` still opens exactly as before (the built-in path is
  untouched) — covered by the existing `Category=Integration` state tests pointed at the SQL Server
  container.

## Open questions

- Whether built-ins register via a static module initialiser (works for `dotnet test` too) or an
  explicit call from each composition root. Leaning: explicit call — one obvious place, matches how
  `DriverRegistry` is populated.
- Migration note wording for `StateEngine` in `appsettings` / env — the value is still `MsSql` etc.,
  so this is a no-op for operators, but the docs should say the field is now open.
