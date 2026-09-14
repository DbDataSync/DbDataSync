# Phase 109j — library compatibility checking (planned)

**Status**: Planned, not started. **No ordering dependency on 109h/109i completing first** — this
phase needs `LibraryRegistry` (109c, already shipped) and the concept of a driver's required library
id, which this phase itself introduces. It can be built before, after, or alongside 109h/109i; whichever
order makes each easier to test is fine. It exists *because of* the risk 109h's own "Open questions /
risks" item 1 named, not because it's downstream of 109h landing.
**Plan reference**: answers 109h's "Version fidelity at runtime, not compile time" risk and 109i's
identical one, both inherited from `nuget-loaded-drivers.md`'s own "Version conflicts with the host."

## Why

`ExcludeAssets="runtime"` (109h/109i) means the exact library version a driver actually runs against is
whatever's installed at `<repo>/libraries/<id>/lib/` — which can differ from the version the driver was
compiled against. .NET's default load context accepts a different version by simple name rather than
refusing it, so a genuine breaking change in the installed version surfaces as a runtime
`MissingMethodException`/`TypeLoadException` at first use, not a build error, and not necessarily at
the moment `library sync` runs — it can surface hours later, on whatever code path first touches the
missing member.

Two real techniques close this gap, and they answer different questions:

1. **Does the installed version have every member the driver's compiled IL actually calls?** —
   answerable *without running anything*, cheaply, the moment a library is installed.
2. **Does the actual thing an operator's replication will do — bulk-copy staging, typed parameter
   binding — work against their real server with this installed version?** Answerable only by actually
   doing it, against a connection, which is why this half is scoped to a connection rather than to a
   library in the abstract — the user's own framing for asking for this, revisited from an earlier
   draft of this phase doc that scoped it to the library alone.

This phase builds both, and is explicit about where each one surfaces.

## What this builds

### 1. `IDriver.RequiredLibraryId` — connecting a driver to the library it depends on

```csharp
/// <summary>The KnownLibraries id this driver's typed API surface depends on, or null — every
/// descriptor-driven/generic driver has nothing to check here. Phase 109j.</summary>
string? RequiredLibraryId => null;
```

`MsSqlDriver.RequiredLibraryId => "microsoft-data-sqlclient"`; `PostgresDriver.RequiredLibraryId =>
"npgsql"`; once 109i lands, `DuckDbDriver.RequiredLibraryId => "duckdb"` (and 109i's own new
`KnownLibraries` entry, per its own doc, is what this resolves against). Nothing in `KnownLibraries`
or `LibraryCatalogEntry` states this mapping today — checked, not assumed — so this is new, not a
rename of something that already exists.

### 2. Static surface checking — no execution, ever

Two steps, both runtime-computed rather than a build-time artifact (neither runs often enough — a
`library install`/`sync` or a `config check` pass, not a hot path — to justify a build step that could
itself go stale):

- **Extract what the driver actually uses.** Open the driver's own already-built DLL (`MsSqlDriver.dll`)
  and walk its member/type references into the target assembly (`Microsoft.Data.SqlClient`). This needs
  a real metadata-reading library — raw `System.Reflection.Metadata` can do it but is low-level and
  verbose; **Mono.Cecil** or the more modern **AsmResolver** are the natural choice, both mature,
  widely-used libraries built for exactly "read a compiled assembly's structure without executing it."
  This is a genuinely new package dependency for whichever project hosts this (likely
  `DbDataSync.Libraries`) — a real, if modest, cost, the same class of tradeoff `DuckDB.NET`'s own
  package-size question already is elsewhere in this plan.
- **Check the candidate.** `System.Reflection.MetadataLoadContext` (a real, shipped .NET API, distinct
  from `AssemblyLoadContext.Default` and never touching it) loads the candidate library at
  `LibraryPaths.LibDir(LibraryPaths.LibraryDir(root, id))` purely as metadata — no JIT, no execution,
  nothing resident in the real process afterward once disposed. For each member the extraction step
  found, confirm the type and a matching member exist in the candidate. Needs a resolver that also
  covers the current runtime's own core assemblies (a documented, known setup step for
  `MetadataLoadContext`, not a surprise to discover mid-implementation).

**This step carries zero pollution risk to the host process** — `MetadataLoadContext` is its own
disposable context, never `AssemblyLoadContext.Default`. It's always safe to run in-process, any time.

### 3. Wired into the two places 109h/109i's own seams already exist

- **`library install`/`sync`** (`LibraryCommand.cs`) — after a successful install, for every registered
  driver whose `RequiredLibraryId` matches, run the check and print any missing members immediately —
  the earliest possible feedback, before anything tries to use the library for real.
- **`dbdatasync config check`** (`ReadinessChecks.cs`) — a new check alongside `StateStoreCheck`/
  `CertificateCheck`, `Warn` (not `Fail` — a missing member might sit on a code path this particular
  deployment never exercises) naming exactly which members are missing from which installed library.

Neither is a hard refusal. A library that fails this check still loads and still might work for
whatever an operator actually does with it — this is a warning surfaced early, not a new way to block
a deployment.

### 4. The deeper check: real staging and writing against a real connection

Static checking has a real ceiling: a member that still exists with the same signature can behave
differently. The question worth actually answering isn't "do these types construct" — it's "does the
exact thing a replication pass will do (bulk-copy a batch into a staging table, bind a typed parameter,
write it to the target) actually work, against this operator's real server, with the version that's
actually installed." That's inherently connection-scoped, not library-scoped — it needs real
credentials and a real server to run against, which only a configured connection has. (An earlier draft
of this phase scoped the deep check to the library alone, probing type construction with no connection
at all; revised here to what was actually asked for.)

**Reuses the driver's own real pipeline — no new per-driver interface to write and keep in sync.**
Rather than a hand-authored probe naming a few types to touch (which duplicates knowledge the driver's
own registration already states, and can drift from what the driver actually does), the validator
drives the driver's own `IStagingProvider`/`IChangeWriter` — the exact objects `RunExecutor` uses for a
real pass — against a small, fixed, synthetic row set and a scratch table it creates and drops itself:

1. Create a scratch table (`DbDataSync_LibraryValidation_<guid>`) on the target connection, a small
   fixed schema chosen for this check alone (an int key, a string, a timestamp) — not derived from any
   real mapping, so no live schema discovery is needed first.
2. Resolve the driver's **native** staging provider and writer specifically — the first entries in
   `MsSqlDriver.StagingProviders`/`.Writers` (`MsSqlStagingTableProvider`, `MsSqlMergeWriter`), not the
   generic portable fallbacks later in the same lists. The generic ones are exactly what would keep
   working even if `Microsoft.Data.SqlClient` broke — they prove nothing about the risk this phase
   exists for.
3. Hand-construct a few synthetic `ChangeRow`s (2-3 rows) and the matching `ColumnMapping`/
   `CachedColumn` values for the scratch schema — known up front, not discovered — and call
   `IStagingProvider.StageAsync` for real. This is `SqlBulkCopy` actually running, against a real
   server, through whichever `Microsoft.Data.SqlClient` version is actually installed — the single
   highest-risk item named in `nuget-loaded-drivers.md`'s own compatibility table.
4. Call the writer's `ApplyAsync` for real, exercising typed `SqlDbType` parameter binding the same way.
5. Confirm the row count landed, then `IStagingProvider.CleanupAsync` and drop the scratch table in a
   `finally` — deterministic teardown even on failure.

**Requires the connection's credentials to have DDL rights** (create/drop table), not just read/connect
— a real precondition worth stating plainly, since the existing lightweight `IConnectionTester.TestAsync`
needs none. This is exactly why it's a separate, heavier, explicitly-triggered action rather than
folded into the existing quick "Test" click.

**Isolation still matters, for the same reason as before, and more so now.** A `MissingMethodException`
is an ordinary catchable exception, not a crash risk — the real reason for a child process is that
`AssemblyLoadContext.Default` never unloads, so actually exercising a version this way and then deciding
not to use it shouldn't leave it resident in the long-running API host for the rest of its life. Doing
real writes, even to a scratch table, with whatever's installed makes this more worth isolating, not
less.

- **`dbdatasync config library validate <id> --connection <name>`** — a new, real, documented verb on
  `LibraryCommand.cs`, alongside its existing `install|sync|list|uninstall`. **Not**
  `dbdatasync internal ...` — checked `InternalCommand.cs` directly: it's explicitly documented as
  *"commands for the build, not for an operator... deliberately absent from `Help.Print`... the
  Dockerfile is the only caller"* — the wrong semantic home for something an operator (or the SPA, on
  their behalf) triggers. `--connection` resolves through the same config-repo + secret-store path
  every other CLI command already uses to reach a real, credentialed connection.
- **Spawned as a child process from the API**, reusing `ProcessSupervisor.BuildStartInfo`'s existing
  `dotnet exec <dll>` shape (the same one that spawns `DbDataSync.TaskRunner`) rather than a new
  mechanism — the API resolves the connection (including its secret) the same way it already does
  before spawning a runner, and passes what the child process needs to open it for real.

### 5. Where this surfaces in the product

Back on the **Connections page**, per the actual reason it was asked for — a "Validate library" action
beside the existing lightweight "Test" button on a connection, not folded into the same click (the DDL
requirement and the child-process cost both argue for a separate, deliberate action, not a silent
addition to every ordinary connection test) and not moved to the Libraries screen, since running it
needs one specific connection's real credentials, not just the library in the abstract.

The **static check's result** still surfaces through `IConnectionTester.TestAsync`'s existing flow —
it's already computed (from `config check`/`install` time), cheap, in-process, and worth surfacing
immediately: *"Connected, but the installed Microsoft.Data.SqlClient (8.1.0) is missing 2 members this
driver uses — Validate library for a full check."* — pointing at item 4's deeper, explicit action for
anyone who wants to actually prove it end to end.

## What this does not build

- **A hard block on installing or using an incompatible library version.** Every result here is a
  warning with detail, never a refusal — consistent with `library install`'s own existing posture (a
  missing `--factory-type` guess is a request for one, not a hard error either).
- **Compatibility checking for descriptor-driven engines.** `RequiredLibraryId` is null for all of
  them by default — a descriptor's provider is resolved by name through `DbProviderFactories`, not
  through a compiled driver's own typed member references, so there's no IL to extract a surface from
  in the first place.
- **A build-time artifact caching the extracted surface.** Computed at runtime, both directions, each
  time — see item 2's own reasoning. A future optimization if the cost ever actually matters, not
  assumed to matter here.
- **Validating against the operator's own real tables or mappings.** Item 4's scratch table is a fixed,
  synthetic schema chosen for this check alone — it proves the mechanism (bulk-copy staging, typed
  write) works, not that a specific real mapping will. That's what an actual replication run or Test
  Connection already answer, each for its own question.
- **A per-driver `ILibraryCompatibilityProbe`.** An earlier draft of this phase specified one; dropped
  in favor of reusing the driver's own real `IStagingProvider`/`IChangeWriter` — see item 4 for why.

## Open questions

1. **Mono.Cecil vs. AsmResolver** — both real, mature choices for item 2's extraction step; pick one
   during implementation based on which reads more naturally against this codebase's own style, not
   decided here.
2. **The scratch table's exact synthetic schema, and how the validator picks "the native staging
   provider/writer" when a driver registers more than one** — item 4 names `MsSqlStagingTableProvider`/
   `MsSqlMergeWriter` as the first, native entries in `MsSqlDriver`'s own lists; whether "first
   registered" is a reliable enough signal in general, or whether a driver should state its own
   default explicitly for this purpose, is an implementation-time call.
3. **Guaranteed cleanup if the child process itself dies mid-validation** (a genuine crash, not a
   caught exception) — the scratch table could be left behind. A known-name prefix
   (`DbDataSync_LibraryValidation_`) at least makes an orphaned one identifiable and safe to drop by
   hand; whether `validate` should also sweep for and remove any stale ones from a previous run before
   creating a new one is worth deciding at implementation time.
4. **Whether `config check`'s new line should be per-driver or per-installed-library** — a library used
   by more than one driver (unlikely today, real once a third engine shares one) would otherwise report
   the same finding twice. Lean toward per-library, deduplicating drivers that share one, but not
   decided here.

## How to verify when built

- **A unit test with two fixture assemblies** (a "driver" referencing a few members of a "library," and
  two versions of the "library" — one complete, one missing a member the driver uses) — the extraction
  step correctly lists the used members, and the check correctly passes the complete version and fails
  the incomplete one naming the specific missing member. No real SqlClient/Npgsql needed to prove the
  mechanism.
- **Real-world confirmation**: run the check against the actual installed `microsoft-data-sqlclient`
  library and `MsSqlDriver.dll` — passes clean (proves the extraction doesn't false-positive on the
  driver's own real, current usage).
- **`dbdatasync config library validate microsoft-data-sqlclient --connection <name>`**, run standalone
  against a real SQL Server (the containers already used throughout this repo's own integration suite):
  reports success, and confirms the scratch table is gone afterward, whether the run succeeded or was
  interrupted. Against a deliberately incompatible library version (a much older pinned one known to
  lack a member the native staging provider/writer actually calls), reports the specific failure from
  the real exception the staging/write call threw, not a generic crash.
- **`config check`** — the static check's new line appears, `Warn`s with specific member names when
  triggered, silent when the installed library is clean. Unchanged by this revision — still the
  no-execution half.
- Confirm the child-process `validate` run leaves no `Microsoft.Data.SqlClient` assembly resident in
  the calling API host's own process afterward — the acceptance test for why this needed isolation in
  the first place, not just an assertion that it "should."
- Confirm a connection with only connect/read rights (no DDL) fails `validate` with a clear message
  naming the missing privilege, not a confusing staging-provider error — the real precondition item 4
  states should be a named, diagnosable failure, not a mystery.

## References

- `architecture/implementation/todo/phase-109h-builtin-drivers-off-provider-packages.md`,
  `phase-109i-duckdb-decoupling.md` — the risk this phase resolves for both.
- `src/DbDataSync.Libraries/LibraryRegistry.cs`, `LibraryPaths` — the existing resolution/on-disk-layout
  this phase reads from, unchanged.
- `src/DbDataSync.Api/Services/ProcessSupervisor.cs` (`BuildStartInfo`) — the `dotnet exec` shape this
  phase's child-process spawn reuses rather than reinvents.
- `src/DbDataSync.Drivers.Abstractions/IConnectionTester.cs`, `ConnectionsController.cs:126` — the
  existing Test Connection flow the static check's result surfaces through, and the model for
  item 4's own new, heavier action beside it.
- `src/DbDataSync.Drivers.Abstractions/IStagingProvider.cs`, `IChangeWriter.cs`,
  `src/DbDataSync.Drivers.MsSql/MsSqlStagingTableProvider.cs`, `MsSqlMergeWriter.cs` — the real,
  already-shipped pipeline item 4 drives with synthetic data, rather than a new per-driver probe.
- `src/DbDataSync.Cli/InternalCommand.cs` — read in full to confirm it's the wrong home for `validate`,
  not guessed.
