# Phase 109g — the state store off the provider packages (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*How little actually
couples to a provider*, §*The provider layer*. Depends on 109c (provider layer) and 109f
(`StateDialectRegistry`, non-built-in connection path). **This is the first phase that removes a
dependency** — do it only once 109a–109e are in production and the provider layer has been exercised.

## What this builds

`DbDataSync.State` stops referencing `Microsoft.Data.SqlClient` and `Npgsql`. Its SQL Server and
PostgreSQL backends resolve their `DbConnection` through the provider layer instead. SQLite stays a
hard reference — it is the default, it is offline and serverless, and decoupling it buys nothing.

### `src/DbDataSync.State/`

- `MsSqlStateDialect.cs` — remove `using Microsoft.Data.SqlClient`. `CreateConnection(cs)` →
  `ProviderRegistry.GetFactory("Microsoft.Data.SqlClient").CreateConnection()` with
  `ConnectionString = cs`. `ShouldRetry` already returns `false`, so nothing else touched.
- `PostgresStateDialect.cs` — same, factory id `"Npgsql"`.
- Both dialects take an injected `ProviderRegistry` (or a `Func<DbProviderFactory>`) rather than a
  static `Instance` singleton — a small constructor change threaded through
  `StateDialectRegistry` registration.
- `DbDataSync.State.csproj` — delete the `Microsoft.Data.SqlClient` and `Npgsql`
  `PackageReference`s. Keep `Microsoft.Data.Sqlite`.
- `SqliteStateDialect` / `StateDatabase` — unchanged (still `new SqliteConnection`, still
  `SqliteConnectionStringBuilder`, still the `SqliteException` retry codes).

### `StateDatabase.Factory`

- For `MsSql` / `Postgres`, require the corresponding provider to be installed; a missing one fails
  at startup with: *"State engine 'MsSql' needs the Microsoft.Data.SqlClient provider. Run
  `dbdatasync provider install Microsoft.Data.SqlClient`."*
- The credential splice (`;Password=…` appended, per phase 79) is string-only and unchanged.

### Installer default

- `dbdatasync provider install` (or the first-run flow) seeds `Microsoft.Data.SqlClient` and
  `Npgsql` provider manifests by default, so a deployment that already used a SQL Server / Postgres
  state backend keeps working after a `provider sync` with no manual step.

### Release note

- A deployment on `StateEngine: MsSql` or `Postgres` must run `dbdatasync provider sync` (or
  `provider install`) once after upgrading to this version. Document prominently; consider a startup
  check that prints the exact command rather than a bare "assembly not found".

## What this phase does not build

- Decoupling the **replication** MsSql/Postgres drivers — 109h. They still reference their providers,
  so a deployment that does replication *and* uses one for state still carries the package via the
  driver; the state csproj just no longer names it.
- DuckDB decoupling — 109i.
- Any change to SQLite.

## How to verify when built

- `dotnet build` clean — `DbDataSync.State` now compiles with no server-provider reference (the
  compiler is the proof the coupling was as small as claimed).
- **The full cross-engine state suite green**, now with `Microsoft.Data.SqlClient` / `Npgsql`
  *restored into a provider dir* by test setup rather than referenced:
  `tests/DbDataSync.State.Tests` and the state-backend `Category=Integration` tests in
  `DbDataSync.Api.Tests`, run against the SQL Server and Postgres containers.
- **New — `StateProviderMissingTests`**: configuring `StateEngine: MsSql` with no
  `Microsoft.Data.SqlClient` provider installed fails at startup with the `provider install` message.
- A migration test: a SQLite state database opened by the SQLite constructor path is byte-identical
  to before — no accidental change to the default.

## Open questions

- Whether the two default provider manifests ship *in the repo template* (so a fresh `dbdatasync
  init` writes them) or are seeded lazily on first `provider sync`. Leaning: ship in the template;
  a `provider.json` is small and it makes the default state backends work with zero extra steps.
- Version pinning: the provider manifests should pin the versions `DbDataSync.State` was tested
  against (currently SqlClient 7.0.2, Npgsql 9.0.3), and `provider sync` upgrades on an explicit
  version bump only.
