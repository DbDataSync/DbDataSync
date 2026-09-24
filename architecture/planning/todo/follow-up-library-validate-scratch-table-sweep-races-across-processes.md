# `LibraryValidationRunner`'s stale-table sweep could drop a sibling run's still-live scratch table

**Found and fixed 2026-09-24**, during a session-wide push to root-cause every currently-flaky test rather
than log another recurrence and move on (see `architecture/implementation/README.md`'s "A flaky test found
during any work gets fixed now, not logged and left"). Not related to whatever else that session's work
was — found while surveying recent red CI runs for already-catalogued flakes, and this one wasn't in the
catalogue yet.

## What failed

CI run `35885505054`, job `DbDataSync.Api.Tests.dll`:

```
LibraryValidateIntegrationTests.Validate_AgainstARealServer_SucceedsAndLeavesNoScratchTableBehind [FAIL]
Error Message:
 Validation failed: MsSqlStagingTable/MsSqlMerge failed against the real connection: Table
 'dbo.DbDataSync_LibraryValidation_4a1caeb57713403aaf916a8d54ac9c71' was not found.
```

at `LibraryValidateIntegrationTests.cs:88` (`Assert.True(report!.Succeeded, report.Output)`). Nothing in
that commit touched `LibraryValidationRunner`, the staging/writer path, or this test.

## Root cause, checked against the real code and the real CI log timing, not guessed

`LibraryValidationRunner.RunAsync` (`src/DbDataSync.Cli/LibraryValidationRunner.cs`) creates a scratch
table named `DbDataSync_LibraryValidation_<guid>`, stages and applies a handful of synthetic rows into it
through the connection's own real staging provider/writer, then drops it. Before creating its own table,
it calls `SweepStaleTablesAsync` — a best-effort cleanup that lists every table matching the
`DbDataSync_LibraryValidation_` prefix and drops it, meant to clean up an orphan left by a *previous*,
crashed run against the same connection.

**The sweep could not tell "orphaned by a crashed previous run" from "created moments ago by a sibling run
that is still actively using it."** It matched on prefix alone, with no notion of age or ownership. Two
different, real `Category=Integration` test suites — `DbDataSync.Api.Tests`
(`LibraryValidateIntegrationTests`) and `DbDataSync.Cli.Tests` (`LibraryValidateCommandTests`) — both
exercise this exact code path against the same shared `master` database on the same
`dbdatasync-mssql-source` container (`Data Source=localhost,14330`). Each disables test parallelization
only *within its own assembly* (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`), which
does nothing to serialize against a sibling assembly — and `dotnet test` on the full solution runs
different test projects as separate, concurrent processes. The CI log's own timestamps for run `35885505054`
show `DbDataSync.Cli.Tests.dll`'s summary line landing fully inside `DbDataSync.Api.Tests.dll`'s own run
window, confirming the two genuinely overlapped, not just theoretically could.

So: process A creates its scratch table, starts staging into it; process B's own validate call runs its
sweep in that window, sees process A's table (indistinguishable from a real orphan by name alone), and
drops it out from under the in-progress `MsSqlMerge` — surfacing as "Table ... was not found," a real
product-code path (the sweep) doing exactly what it was written to do, against a case it was never scoped
to handle.

## Why this isn't purely a test-isolation artifact

The sweep runs from real production code (`LibraryCommand.ValidateAsync` → `LibraryValidationRunner.RunAsync`),
against whatever database the connection under validation actually points at. Two different connections
in a real deployment that happen to target the same physical database (a staging and a "shared scratch"
connection pointed at the same server, say) validating around the same time would hit the identical race.
Narrow, but real — worth fixing at the source rather than only in test setup.

## Applied (2026-09-24)

The scratch table name now embeds its own creation time: `DbDataSync_LibraryValidation_<unixSeconds>_<guid>`
instead of `DbDataSync_LibraryValidation_<guid>`. `SweepStaleTablesAsync` parses that segment back out and
only drops a table whose embedded timestamp is older than `StaleTableAge` (5 minutes) — comfortably longer
than any real validate run takes (this repo's own CI evidence: these tests complete in low single-digit
seconds), so a table that stale can only be a genuine crash orphan, never a concurrent sibling's live one,
regardless of how many validate runs race each other. A name that doesn't parse (an unexpected format) is
left alone rather than guessed at — the sweep is a best-effort convenience, not the only way an orphan ever
gets cleaned up; one is still identifiable by prefix and safe to drop by hand.

No `IDriver`/`ListTablesAsync` interface change — the age check is pure string parsing inside
`LibraryValidationRunner`, kept out of the generic cross-engine table-listing abstraction.

**Verified**: built `DbDataSync.Cli` and both test projects, then ran
`LibraryValidateIntegrationTests`/`LibraryValidateCommandTests`' validate-calling tests concurrently (two
separate `dotnet test` processes launched together, mirroring what CI actually does) five times back to
back — all ten runs (5×2 processes) green. This environment's own containers didn't reproduce the original
race even before the fix (three earlier concurrent attempts, not logged in detail here, also passed) — so
this is proof of no regression under the exact concurrent shape that failed in CI, not proof the race is
gone; the real proof is whether this specific failure (or any "was not found" against a
`DbDataSync_LibraryValidation_` table) recurs.

## How to verify when closed

- Several consecutive CI runs where `DbDataSync.Api.Tests` and `DbDataSync.Cli.Tests` both ran
  `LibraryValidate*` tests with no "Table ... was not found" failure from either.
- This doc moves to `planning/done/`.
