# Phase 109i — DuckDB decoupling

**Status**: Complete.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*Package size — not a lever
here*. Depended on 109c (the library layer) and reused 109h's own `ExcludeAssets="runtime"` decision
verbatim — the same mechanism, not a separate design, exactly as this doc's own "Decided 2026-09-13"
line promised.

## What this built

### 1. The three consumers, unchanged in every `.cs` file

```xml
<PackageReference Include="DuckDB.NET.Data.Full" Version="1.5.5" ExcludeAssets="runtime" />
```

on `DbDataSync.Drivers.DuckDb.csproj`, `DbDataSync.Verification.csproj`, and
`DbDataSync.Scripting.csproj` — the three projects that construct `DuckDBConnection` directly
(`DuckDbDriver.cs`, `VerificationResultQuery.cs`, `SegmentingStrategyRunner.cs`). Confirmed by reading
all three before touching anything: none of them go through `DbProviderFactories`/a `DbProviderFactory`
today — they `new DuckDBConnection(...)` directly — so `LibraryRegistry`'s
`AssemblyLoadContext.Default.Resolving` hook (109c, reused verbatim by 109h) satisfies the load however
the requesting code reached it. **Zero `.cs` file in any of the three consumers changed** — the diff for
this item is three `.csproj` lines.

One correction to the original doc while confirming this: `SegmentingStrategyRunner.cs` lives in
`DbDataSync.Scripting`, not `DbDataSync.Verification` as the doc's own consumer list implied by
juxtaposition — the two engine names were always right, just worth being explicit that they're two
different projects (`DbDataSync.Verification.csproj` for `VerificationResultQuery.cs`,
`DbDataSync.Scripting.csproj` for `SegmentingStrategyRunner.cs`), both of which already carried a plain
`DuckDB.NET.Data.Full` `PackageReference` before this phase.

### 2. The `KnownLibraries` factory type — confirmed by reflection, not guessed

The doc left this open. Resolved by publishing a throwaway console app referencing
`DuckDB.NET.Data.Full` `1.5.5` for real and reflecting the assembly:

```csharp
var asm = typeof(DuckDB.NET.Data.DuckDBConnection).Assembly;
foreach (var t in asm.GetTypes())
    if (typeof(System.Data.Common.DbProviderFactory).IsAssignableFrom(t) && !t.IsAbstract)
        Console.WriteLine(t.FullName);
```

Exactly one match: `DuckDB.NET.Data.DuckDBClientFactory` (with a public static `Instance` field, the
usual shape, though `DbProviderFactories`' string-registration path doesn't need it). The new catalog
entry:

```csharp
new(
    "duckdb", "DuckDB.NET.Data.Full", "DuckDB.NET.Data.DuckDBClientFactory, DuckDB.NET.Data",
    "DuckDB (embedded)", "The embedded analytical engine DbDataSync's own verification and custom " +
        "segmenting strategies run on.",
    PinnedVersion: "1.5.5"),
```

in `src/DbDataSync.Libraries/KnownLibraries.cs`, the eighth entry — same shape as the other seven,
including the `PinnedVersion` field phase 121 added after this doc's original table (the doc's own
sketch predates that field; the real entry carries it like every other one now must).

### 3. Test projects needing their own direct reference — a wider set than the doc's own grep found

Confirmed for all four candidates by grepping `DuckDBConnection` (not just `DuckDB.` — the doc's own
grep pattern, which happens to miss the no-dot identifier `DuckDBConnection` itself) across every test
project, *and* by actually running each suite after the exclusion landed rather than trusting a static
grep alone:

- **`DbDataSync.Drivers.DuckDb.Tests.csproj`** — needed one. `DuckDbDriverTests.cs` `new
  DuckDBConnection(...)`s directly, previously supplied transitively through the driver project's own
  (now-excluded) reference.
- **`DbDataSync.Api.Tests.csproj`** — needed one. `DuckDbQueryPreviewTests.cs` and
  `PositionCapturingContractTests.cs` both `new DuckDBConnection(...)` directly in their own scaffolding.
  Confirmed by grep, matching the doc's own claim.
- **`DbDataSync.Scripting.Tests.csproj`** — **needed one, contradicting the doc's own grep.** No `.cs`
  file in this project names `DuckDBConnection` (a grep for the literal type name finds nothing but a
  prose comment), but `SegmentingStrategyRunnerTests.cs` exercises `SegmentingStrategyRunner`'s
  `DuckDb`-kind path (`OnlyDuckDbNeedsNoConnection_AndItGetsNone`, `DateBounds_AreWrittenRoundTrippable`,
  `AMissingRequiredColumn_IsNamedRatherThanFailingOnAnOrdinal`), which `new DuckDBConnection(...)`s
  *inside `DbDataSync.Scripting`* — a runtime dependency this project's own tests genuinely exercise,
  invisible to a grep that only looks for the type name in the test project's own source. Verified the
  hard way: removed the reference, ran the suite, watched 7 of 58 tests fail with
  `FileNotFoundException: Could not load file or assembly 'DuckDB.NET.Data...'`, restored the reference,
  reran — 58/58 green.
- **`DbDataSync.Verification.Tests.csproj`** — **needed one, same contradiction.** No `.cs` file here
  mentions DuckDB at all (a plain grep for `DuckDB` finds zero matches), but
  `VerificationResultQueryTests.cs` drives `VerificationResultQuery`, which `new DuckDBConnection(...)`s
  *inside `DbDataSync.Verification`* to page the parquet a check produced. Verified the same way:
  removing the reference took 27/27 to 11 passed/16 failed with the identical `FileNotFoundException`;
  restoring it brought it back to 27/27.

**This is the real, repeat-instance finding of the phase** — the same shape of gap 109h's own
retrospective flagged for `Microsoft.Data.SqlClient` (test scaffolding using a type directly, invisible
to the driver-under-test framing), but one level more subtle here: it isn't the *test's own* source using
the type, it's the *test exercising a production code path in another project* that uses the type. "Grep
this test project's `.cs` files for the type name" is not the same question as "does running this
project's tests reach a `new DuckDBConnection(...)` anywhere in the call graph" — the doc's original
"weren't found... confirm at implementation time" caveat was exactly the right level of caution, and the
right answer to the caveat was to actually break it first (comment out the new reference, watch the
suite fail with a `FileNotFoundException`) and only then trust the fix, not to re-grep and stop there.

### 4. Unconditional install — `ServeCommand.EnsureDuckDbInstalledAsync`

```csharp
// src/DbDataSync.Cli/ServeCommand.cs
try { Prepare(root); }
catch (...) { ...; return 1; }

await EnsureDuckDbInstalledAsync(root);
```

right after `Prepare(root)`, exactly where the doc's own pseudocode put it — `RunAsync` was already
`async Task<int>`, so no signature change was needed anywhere, confirmed true. `EnsureDuckDbInstalledAsync`
(`internal`, `DbDataSync.Cli.Tests`-visible via the project's existing `InternalsVisibleTo`):

- **Idempotency check**: a plain `Directory.Exists(LibraryPaths.LibDir(LibraryPaths.LibraryDir(root,
  "duckdb")))` — not a `LibraryRegistry.LoadAll()` — so a second `serve` start does the one filesystem
  stat and returns, no network, no `dotnet publish` subprocess. Verified for real (see below), not just
  argued.
- **On a cache miss**, resolves the catalog entry (`KnownLibraries.TryGetById("duckdb")`) and calls
  `LibraryInstaller.InstallAsync` with its `PackageId`/`PinnedVersion`/`FactoryType` — the same
  `catalogEntry.*` idiom every other call site in this repo already uses
  (`DriverConnectionFactory.EnsureLibraryInstalledAsync`, `SetupSteps.ApplyStateDatabaseAsync`).
- **On failure**, catches `Exception` broadly, writes one line to `Console.Error` naming `dbdatasync
  config library sync` as the fix, and returns normally — `RunAsync` proceeds to build and run the host
  exactly as if the install had succeeded. This is `CertificateExpiryService.CheckAsync`'s own posture
  (catch, log, continue — "this shouldn't be allowed to take the whole process down"), copied
  deliberately rather than reinvented.
- Reaches both entry points named in the doc with the one change: `dbdatasync serve` directly, and
  `Tui/SetupScreen.cs`'s Start button, which was confirmed (by reading it, not assumed) to call
  `ServeCommand.RunAsync(["--repo", root, "--url", savedUrl])` at its one call site.

### 5. `tests/DbDataSync.Web.Tests/playwright.config.ts` — a real gap found by actually running the golden path

Not in the original doc at all, and not a production file — but a real fix, found the way this phase's
whole "confirm at implementation time" posture asked for: by running the acceptance test, not by
inspecting the diff. See "Decisions made" below.

## How the open questions were actually resolved

- **The exact `DuckDB.NET.Data` factory type**: `DuckDB.NET.Data.DuckDBClientFactory, DuckDB.NET.Data` —
  found by reflecting the real, restored 1.5.5 assembly (see item 2 above), not guessed. It is the only
  public, non-abstract `DbProviderFactory` subclass in the assembly.
- **Whether `DbDataSync.Scripting.Tests`/`DbDataSync.Verification.Tests` need their own direct
  reference**: **yes, both do** — see item 3 above. The doc's own grep (for the literal type name in
  each project's own source) found nothing and flagged the absence as unconfirmed rather than settled;
  actually applying the exclusion and running both suites surfaced the real gap in each, which a type-name
  grep alone could never have found because the `DuckDBConnection` construction happens one project layer
  away from the failing test, inside the production code the test is exercising.

## Decisions made, and real bugs found

### The `ExcludeAssets` blast radius, again bigger than a same-project grep predicts

Covered under item 3. The specific, reproducible failure mode: `dotnet test` on `DbDataSync.Scripting.Tests`
or `DbDataSync.Verification.Tests` with the exclusion in place but no direct test-project reference threw
`System.IO.FileNotFoundException: Could not load file or assembly 'DuckDB.NET.Data, Version=1.5.5.0,
Culture=neutral, PublicKeyToken=1d0aa5325e915c3b'` from inside `SegmentingStrategyRunner.RunDuckDbAsync`/
`VerificationResultQuery.ReadPageAsync` respectively — a `LibraryRegistry` resolver never gets armed in a
plain `dotnet test` run (no `libraries/duckdb/` on disk, nothing calls `LoadAll()`), so the only thing
that can satisfy the load in a test process is the test project's own local copy of the DLL. Fixed by
giving both their own direct, un-excluded `PackageReference` to the identical package and version.

### The unconditional install's failure posture, exercised for real, not just read

Verified against a real, reproducible failure with no network flakiness in the loop: pointed
`KnownLibraries`' `duckdb` entry at a nonexistent version (`99.99.99-does-not-exist`) temporarily, ran
`dbdatasync serve` against a fresh repo root, and watched it print `Could not install 'duckdb'
automatically: ... Unable to find package DuckDB.NET.Data.Full with version (>= 99.99.99-does-not-exist)
... Verification and DuckDB-backed segmenting strategies will not work until \`dbdatasync config library
sync\` completes it; replication itself is unaffected.` to stderr and then **still start and bind the
port** (`Now listening on: http://localhost:5097`, `Application started`). Reverted the version
immediately after. This is the acceptance bar the doc set for "a failure here should log and continue
rather than refuse to start," proved against a genuine restore failure rather than only inspected in the
diff.

### The Playwright golden path's own webServer bypasses `ServeCommand` entirely — caught by running the suite

The doc's own acceptance bar asked for the golden-path Playwright specs to be run, not merely assumed
green because the `.cs` diff was empty. Running the full `golden-path.spec.ts` suite once, straight
after items 1–4 above, surfaced a real failure: test 24 ("a mapping can be checked against its target,
and the threshold decides what is flagged" — the current name of the scenario the doc's own screenshot
convention would have called a verification test) failed with `getByTestId('verification-result')`
never becoming visible, because the API process backing that test never had DuckDB installed.

The cause: `playwright.config.ts`'s `webServer` entry launches
`dotnet exec src/DbDataSync.Api/bin/Debug/net10.0/DbDataSync.Api.dll` **directly** — deliberately, per
that entry's own pre-existing comment, so killing the process reliably kills the real one rather than a
`dotnet run` wrapper that can leave an orphan behind. That command never goes through
`dbdatasync serve`/`ServeCommand.RunAsync`, so `EnsureDuckDbInstalledAsync` never runs for it. Before
this phase, this was invisible: `DbDataSync.Verification.csproj`'s plain (non-excluded)
`DuckDB.NET.Data.Full` reference meant the managed DLL was simply sitting next to `DbDataSync.Api.dll`
in its own build output regardless of any install step. After `ExcludeAssets="runtime"` landed, it
wasn't, and nothing seeded `<scratchRepoRoot>/libraries/duckdb/` before that process started.

Confirmed this is a **test-harness gap, not a production one**, before deciding where to fix it: grepped
the real `Dockerfile` (`ENTRYPOINT ["dotnet", "/app/DbDataSync.Cli.dll", "serve", ...]`, both images) —
production only ever starts through the CLI's `serve` command, so every real deployment gets
`EnsureDuckDbInstalledAsync` for free, in order, before `DbDataSyncHost.Build()` ever runs. The fix
therefore belongs in the test harness, not in `DbDataSyncHost`/`DbDataSync.Api`'s own entry point: added
a synchronous `execFileSync('dotnet', ['exec', '.../DbDataSync.Cli.dll', 'config', 'library', 'install',
'duckdb', '--version', '1.5.5', '--repo', scratchRepoRoot], ...)` step in `playwright.config.ts`, right
next to the existing synchronous scratch-repo-clearing code (same file, same "has to finish before
Playwright even reads `webServer` out of the config object" constraint the existing code already
documents). A real install through the real CLI command, not a hand-rolled fixture — this repo's own
"real, not mocked" precedent, applied to the one gap in the harness rather than the product. Re-ran the
full suite twice more after the fix: one run hit an unrelated, pre-existing local-sandbox flake on a
different test (14, a direct-URL-reload timeout — nothing to do with DuckDB, and it had passed cleanly
in the very first run before test 24's own failure was ever reached, and again in the third run), and a
final run with `--retries=1` went 45/45 green, including test 24 and test 41 ("a segmenting strategy is
authored, tested and used, without editing a config file" — the DuckDb-kind segmenting path, run through
a real `TaskRunner` worker spawn).

This also confirms, empirically rather than only by re-reading 109h's own reasoning, that
`DbDataSync.TaskRunner` needs no install hook of its own for DuckDB (mirroring 109h's identical
conclusion for MsSql/Postgres): test 41's backfill spawns a real `TaskRunner` worker against a
DuckDb-kind segmenting strategy, and it worked with no TaskRunner-side change, because by the time any
worker spawns, `duckdb` is already on the shared repo's disk — installed either by
`ServeCommand.EnsureDuckDbInstalledAsync` (a real deployment) or by the Playwright fixture's own
seed step (this test harness) — and `TaskRunner.Program.cs`'s existing `LibraryRegistry(repoRoot).LoadAll()`
at spawn finds and arms it like any other installed library.

### Idempotency, timed for real

First `serve` start against a wiped `libraries/` directory: `libraries/duckdb/lib/` appeared within 3
seconds (a `dotnet publish`-backed restore against the local NuGet cache, no network round trip needed
since the package was already cached from development). Second `serve` start against the same,
now-populated root: reached the `DbDataSync is starting.` banner in 420ms, and
`libraries/duckdb/lib/DuckDB.NET.Data.dll`'s mtime was byte-identical before and after — proving no
restore ran, not merely that it finished fast.

## What's explicitly out of scope / not built

- **DuckDB's native per-RID assets are still shipped, unchanged.** Confirmed in the publish check below:
  `runtimes/{linux-x64,linux-arm64,win-x64,win-arm64}/native/{lib}duckdb{.so,.dylib,.dll}` are present in
  `dotnet publish`'s output regardless of the `ExcludeAssets="runtime"` change, because NuGet's asset-type
  taxonomy treats `native` and `runtime` as distinct buckets — `ExcludeAssets="runtime"` only drops the
  managed `lib/` assembly (`DuckDB.NET.Data.dll`/`DuckDB.NET.Bindings.dll`), never the `runtimes/*/native/`
  ones. This is not a gap in the phase; it is the same thing 109h's own retrospective noted for
  `Microsoft.Data.SqlClient.SNI.dll`, and per the doc's own "What this phase does not build," removing
  native assets/trimming is separate, unstarted packaging work. **Closed by phase 146** — see
  `architecture/implementation/done/phase-146-duckdb-native-asset-exclusion.md`.
- **DuckDB is not made optional or not-shipped.** The unconditional install in item 4 exists precisely
  because it is not optional.
- **No `DbProviderFactory`-based rewrite of any of the three consumers.** Confirmed by the diff: the only
  `.cs` files this phase touched are `ServeCommand.cs` (the new install hook) and a new test file
  (`ServeCommandDuckDbTests.cs`); every other production change is a `.csproj` line or a
  `KnownLibraries.cs` entry, plus the one test-harness-only file, `playwright.config.ts` (see "Decisions
  made" above) — not a `.cs` file and not shipped in any DbDataSync build.

## How it was verified

- `dotnet build DbDataSync.slnx` — clean, 0 warnings/errors.
- `DbDataSync.Drivers.DuckDb.Tests` standalone: 33/33 passed.
- `DbDataSync.Api.Tests` standalone, non-integration: 422 passed, 23 skipped (Windows-only) — identical
  to 109h's own baseline, confirming no regression from this phase's `.csproj` changes.
- `DbDataSync.Scripting.Tests` standalone: 58/58 passed (with the new direct reference; 51/58 without it
  — see "Decisions made" above for the deliberate before/after check).
- `DbDataSync.Verification.Tests` standalone: 27/27 passed (11/27 without the new reference — same
  deliberate before/after check).
- `DbDataSync.Libraries.Tests`: 27/27 passed, unaffected by the new catalog entry.
- `DbDataSync.Cli.Tests`: 113 passed (110 before this phase, +3 new
  `ServeCommandDuckDbTests` cases: fresh-root real install, already-installed idempotency via a marker
  file surviving the call, and failed-install-does-not-throw against an unreachable path).
- `dotnet publish src/DbDataSync.Api -c Release` — output contains no `DuckDB.NET.Data.dll`/
  `DuckDB.NET.Bindings.dll` anywhere (confirmed by directory listing, not merely absence from one
  listing by chance), while `DbDataSync.Drivers.DuckDb.dll`/`DbDataSync.Verification.dll`/
  `DbDataSync.Scripting.dll` themselves are present and `DbDataSync.Api.deps.json` still lists
  `DuckDB.NET.Data.Full`/`DuckDB.NET.Bindings.Full 1.5.5` as dependencies with no `runtime` target to
  copy — the exclusion is real, not an accident of this one output directory.
- **Fresh-deployment check, done for real, not simulated**: wiped/nonexistent `libraries/` directory,
  ran `dbdatasync serve` against it — `libraries/duckdb/lib/` (with `DuckDB.NET.Data.dll`,
  `DuckDB.NET.Bindings.dll`, `libduckdb.so`, and `library.json` naming the confirmed factory type)
  appeared within 3 seconds, with no manual `library install duckdb` step. A second `serve` start against
  the same root reached its startup banner in 420ms with the installed DLL's mtime unchanged — the
  idempotence bar the doc asked for, met and timed, not merely asserted.
- **The failure-tolerance posture, exercised against a real restore failure**, and **the failure-tolerance
  logging**, both covered under "Decisions made" above.
- **The golden-path Playwright suite, end to end, 45/45 green** — including the two scenarios the doc
  named and the DuckDb-kind segmenting scenario the doc didn't specifically call out but which exercises
  the same dependency through a real `TaskRunner` worker spawn. See "What was run beyond the doc's own
  bar" below for the full story, including the real gap this run found and fixed.
- Full test suites for every touched assembly, confirming no regressions:
  - `DbDataSync.Drivers.DuckDb.Tests`: 33 passed (unchanged from before this phase — no new tests added
    here; the driver's own `.cs` files didn't change).
  - `DbDataSync.Api.Tests` non-integration: 422 passed, 23 skipped — byte-identical to 109h's baseline.
  - `DbDataSync.Scripting.Tests`: 58 passed (unchanged count; the new `.csproj` reference fixed a latent
    runtime gap, it didn't add tests).
  - `DbDataSync.Verification.Tests`: 27 passed (same).
  - `DbDataSync.Libraries.Tests`: 27 passed (the new eighth catalog entry didn't change any assertion
    about the other seven).
  - `DbDataSync.Cli.Tests`: 113 passed (110 + 3 new, see above).

## What was run beyond the doc's own bar, and what was not run

- **No `Category=Integration`-tagged suite exists in `DbDataSync.Scripting.Tests`,
  `DbDataSync.Verification.Tests`, or `DbDataSync.Drivers.DuckDb.Tests`** — grepped for
  `Trait("Category", "Integration")` across all three and found zero matches (52 matches exist elsewhere
  in the solution, e.g. the Postgres/MsSql driver test projects, none of them DuckDB-related). This makes
  sense: DuckDB is embedded and in-memory, so nothing in these three projects needs a real external
  container the way the MsSql/Postgres suites do — their full, unfiltered runs (58/58, 27/27, 33/33 above)
  are already complete coverage, not a subset with an Integration slice skipped.
- **The golden-path Playwright specs the doc named (`24-create-table-plan`/`25-mapping-preview`) were run,
  end to end, three times, and confirmed green.** The doc's own screenshot names map to the *current*
  spec's tests 18 ("a target table that does not exist is named, created from the plan, and replicated
  into") and 19 ("the mapping preview shows every statement a pass would run, and where each came from")
  in `tests/DbDataSync.Web.Tests/tests/golden-path.spec.ts` — the test numbering has shifted since the doc
  was written as more golden-path scenarios were inserted, but the screenshot filenames the doc actually
  named are unchanged and still identify the same two scenarios. `test.describe.serial` with `workers: 1`
  means these can't be run in isolation — the whole 43/45-scenario spec ran every time, end to end,
  against the real `dbdatasync-mssql-source`/`dbdatasync-mssql-target` containers already running in this
  sandbox. First run (before the `playwright.config.ts` fix): 25 passed, then a real failure at test 24
  (the verification/DuckDB scenario — see "Decisions made" above), 19 not run after that. Second run
  (after the fix): the DuckDB-dependent tests (24 and, later, 41) both passed, but a different, unrelated
  test (14, a direct-URL-reload timeout) hit a one-off local-sandbox timing flake — it had already passed
  cleanly in the first run, before ever reaching the point where 24 failed, so this was not a regression
  from anything in this phase. Third run, with `--retries=1`: **45/45 passed**, including 24 and 41.
  Screenshots this generated (`git status` showed ~40 regenerated `screenshots/golden-path/*.png` files
  after each run, none of them from a UI change — nothing in this phase touches frontend code) were
  reverted with `git checkout` rather than committed, to keep the diff honest about what this phase
  actually changed.
