# Phase 109h — built-in replication drivers off the provider packages (planned)

**Status**: Planned, not started — **decision deferred until 109a–109g are done.**
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*What still can't be
decoupled, and why*. Depends on 109c (provider layer) and 109e (compiled-plugin loader, if option B).

## The problem

`DbDataSync.State` needed its providers in one line each (109g). The **replication** drivers use the
typed provider API meaningfully, so this phase is genuinely harder and its shape is a decision, not a
mechanical port:

| driver | typed provider use | has a `System.Data.Common` form? |
| --- | --- | --- |
| `MsSqlDriver` / `MsSqlStagingTableProvider` | `SqlBulkCopy` (×8) | **no** — `BatchInsertStagingProvider` (multi-row INSERT) is the only portable staging |
| `MsSqlSegmentScope` | `SqlDbType` (×28) — typed segment-bound binding | partial — `System.Data.DbType` covers most, loses `Decimal` precision/scale nuance |
| `MsSqlChangeTrackingReader` | `SqlException.Number == 3952` | `DbException` has `SqlState` / provider-specific reflection |
| `MsSqlDriver` | `SqlConnectionStringBuilder` (×6) | yes — `DbConnectionStringBuilder` base + string keys |
| `PostgresDriver` / `PostgresValueBinding` | `NpgsqlDbType` (×17), `NpgsqlConnectionStringBuilder` | partial / yes |

`DuckDbDriver` is light (`DuckDBConnection`, `DuckDBConnectionStringBuilder`) but is 109i's concern
along with verification and scripting.

## The two options (pick when the cost is concrete)

### Option A — keep them compiled and hard-referenced

- `MsSqlDriver` etc. stay in the solution, keep `<PackageReference>`.
- A `Microsoft.Data.SqlClient` advisory means a DbDataSync patch build — **but only for the compiled
  fast paths.** The state store (109g) and every descriptor-driven engine (109d) are already covered
  by `provider sync`.
- Zero new work. The honest default if the compiled drivers are updated rarely and the security
  exposure of a bundled provider is acceptable.

### Option B — first-party plugins

- `MsSqlDriver` / `PostgresDriver` / `DuckDbDriver` move to `<repo>/drivers/mssql/` etc., loaded
  through 109e's loader, each carrying its own provider in `lib/`.
- A SqlClient bump is `dbdatasync driver sync` — the complete version of the goal.
- Cost: the compiled drivers become external-shaped (they reference `Abstractions` as a package, not
  a project); the default `dbdatasync init` seeds the three first-party driver manifests; CI builds
  and packs them; the golden-path suite now runs against loaded-not-linked drivers. Non-trivial.
- Typed binding (`SqlDbType`) and `SqlBulkCopy` are fine here — a plugin *is* allowed its provider
  reference; the point is only that it is not the *core's* reference.

## What this phase does not build

Nothing is committed here yet. This doc exists so the decision is not made implicitly. Revisit after
109g ships and answer:

- How often do the compiled drivers actually change relative to a provider CVE cadence?
- Does the golden-path suite tolerate driver-loading indirection without becoming flaky?
- Is `SqlDbType` → `System.Data.DbType` loss acceptable, or does Option B (keep the typed ref inside
  the plugin) become the only real choice?

## How to verify when built (Option B)

- The full golden-path Playwright suite and every `Category=Integration` MsSql/Postgres test green,
  now with the drivers restored into `drivers/` rather than compiled in.
- `grep` shows no `Microsoft.Data.SqlClient` / `Npgsql` `PackageReference` in any project under
  `src/` except a `drivers/*/` plugin csproj.
- `dbdatasync driver list` shows `mssql`, `postgres`, `duckdb` as `builtin`-flagged plugins.
- A SqlClient version bump in `drivers/mssql/driver.json` + `driver sync` takes effect with no
  DbDataSync rebuild — the acceptance test for the whole group.
