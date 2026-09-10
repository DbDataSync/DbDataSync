# Screenshots

Captured by the Playwright suite in `tests/DbDataSync.Web.Tests/` — one folder per spec, so a
numeric prefix reused across specs never collides. Regenerate them all with:

```sh
dotnet build src/DbDataSync.Api
cd tests/DbDataSync.Web.Tests && npx playwright test
```

Each spec writes only into its own folder via the shared `screenshotDir(...)` helper
(`tests/DbDataSync.Web.Tests/screenshots.ts`).

| folder | what it shows | spec |
| --- | --- | --- |
| `original-ui/` | the console before the SPA rewrite — kept for comparison | (none) |
| `golden-path/` | the full walkthrough, `01`–`50` in narrative order: connections, a replication, table mappings, provisioning, the first run, verification, scripts, monitoring | `golden-path.spec.ts` |
| `backfill-progress/` | the backfill progress card during a running backfill | `backfill-progress.spec.ts` |
| `duckdb-query-source/` | a DuckDB query as a replication source, and its preview | `duckdb-query-source.spec.ts` |
| `lag-monitoring/` | the Monitoring tab and the Replications-list lag column | `lag-monitoring.spec.ts` |
| `mapping-column-add/` | a planned column-add on an existing mapping | `mapping-column-add.spec.ts` |
| `mapping-metadata-cache/` | the cached-metadata card, before and after a refresh | `mapping-metadata-cache.spec.ts` |
| `monitoring-intent-and-hold/` | a held mapping, its recovery, the data-loss confirm, pause under a replication pause | `monitoring-intent-and-hold.spec.ts` |
| `monitoring-restructure/` | Runs as a Monitoring sub-tab, the sub-tab strip, Schedule on Overview, per-card refresh countdowns | `monitoring-restructure.spec.ts` |
| `replication-wide-provisioning/` | the replication-wide provisioning plan grouped by connection, a prerequisite warning, run results | `replication-wide-provisioning.spec.ts` |
| `run-details-dialog/` | the run-status column and the succeeded / failed run-details dialogs | `run-details-dialog.spec.ts` |
| `run-history-filtering-and-paging/` | the run-history status filter and older / newer paging | `run-history-filtering-and-paging.spec.ts` |
| `runs-watermarks-refresh/` | the runs list with pid and watermarks, and the per-panel refresh countdowns | `runs-watermarks-refresh.spec.ts` |
| `admin-drivers-libraries/` | Admin → Drivers and Admin → Libraries (phase 118) | `admin-drivers-libraries.spec.ts` |
| `library-search/` | NuGet search results on the Libraries screen, and a curated quick-add chip (phase 119) | `library-search.spec.ts` |
| `admin-library-install/` | the non-catalog package trust dialog and the restart-required banner after an install (phase 120) | `admin-library-install.spec.ts` |
