# Phase 109i — DuckDB decoupling (planned)

**Status**: Planned, not started — **explicitly deferred.** Separate from the rest of the group.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*Package size — not a
lever here*. Depends on 109c (provider layer).

## What this builds

`DuckDB.NET` stops being a compile-time `PackageReference` of anything except (optionally) a
first-party DuckDb driver plugin. It is resolved through the provider layer instead. This makes
DuckDB **independently updatable** — a DuckDB.NET fix is a `provider sync`, not a DbDataSync build —
but does **not** remove DuckDB from the shipped package: verification and segmenting use the embedded
engine, so its native libraries still travel with the tool.

### The three consumers

| project | DuckDB use | after |
| --- | --- | --- |
| `DbDataSync.Drivers.DuckDb` | `DuckDBConnection`, `DuckDBConnectionStringBuilder` | a first-party driver plugin, or a `GenericDriver` spec bound to the DuckDB provider |
| `DbDataSync.Verification` (`VerificationResultQuery`) | `DuckDBConnection` / `DuckDBCommand` to read run-result parquet | a `DbProviderFactory` from `ProviderRegistry.GetFactory("DuckDB.NET")` — the queries are `DbCommand`-shaped |
| `DbDataSync.Scripting` (`SegmentingStrategyRunner`, `CustomSegmentExpansion`) | `DuckDBConnection` to run an operator's DuckDB segmenting query | same |

`DuckDBConnectionStringBuilder` usage (the `:memory:` / bare-path normalisation in `DuckDbDriver`)
moves to string handling, the same way `MsSqlDriver`'s builder use would (109h).

### Why it is deferred

- DuckDB is not a replication concern; it is infrastructure for two subsystems. Touching it has no
  bearing on the "add a new engine from NuGet" goal.
- The benefit is only "update DuckDB.NET without a rebuild," which matters less than the same
  property for the server providers (a serverless embedded library has a smaller attack surface and
  a slower security cadence than `Microsoft.Data.SqlClient`).
- It is the most cross-cutting of the removals — three projects, and verification's parquet path is
  on the run's critical path — so it carries the most regression risk for the least urgency.

## What this phase does not build

- Removing DuckDB's native assets from the package (per-RID split, trimming) — that is a separate
  packaging exercise, noted in the plan doc.
- Making DuckDB optional / not-shipped — verification needs it.

## How to verify when built

- `dotnet build` clean; no `DuckDB.NET` `PackageReference` under `src/` except a `drivers/duckdb/`
  plugin csproj.
- **Verification** — the `Category=Integration` verification tests green: a run produces a parquet
  result and `VerificationResultQuery` reads it back through the provider factory.
- **Segmenting** — the DuckDB segmenting-strategy tests green (`SegmentingStrategyRunner`,
  `CustomSegmentExpansion`), including the golden-path Playwright `24-create-table-plan` /
  `25-mapping-preview` flows that exercise a DuckDB strategy.
- A `DuckDB.NET` version bump in its `provider.json` + `provider sync` takes effect with no rebuild.

## Open questions

- Whether the DuckDb *driver* becomes a `GenericDriver` descriptor or a small compiled plugin —
  `DuckDbQueryReader` is bespoke (a query, not a table), so likely a compiled plugin that reuses
  little of `Generic`.
- Verification's parquet reader currently assumes a specific DuckDB feature set; pin the provider
  version it is tested against and treat a bump as a deliberate, tested change.
