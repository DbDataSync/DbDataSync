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
2. **Does calling into it actually work?** — answerable only by actually calling into it, which needs
   isolation for a real reason (below), not just as a precaution.

This phase builds both, and is explicit about which of 109h/109i's two identified seams — `library
install`/`sync` and the Libraries admin screen — each one belongs on.

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

### 4. The deeper check: an isolated runtime smoke-test

Static checking has a real ceiling: a member that still exists with the same signature can behave
differently. Answering "does calling into this actually work" needs to actually call into it.

**Where isolation matters, precisely — not "to prevent a crash."** A `MissingMethodException`/
`TypeLoadException` is an ordinary, catchable managed exception; it doesn't corrupt the process the way
a native fault would, so running the smoke-test in-process wouldn't be *unsafe* in that sense. The real
reason to isolate it in a child process: once a candidate version actually loads into
`AssemblyLoadContext.Default` to be called into for real, it's resident there for the rest of that
process's life — that context is never unloaded. Smoke-testing a version and then deciding not to use
it should not leave it permanently loaded in the long-running API host.

- **`ILibraryCompatibilityProbe`**, a new optional interface a driver implements once, beside its own
  code — the same shape `IConnectionTester`/`IStatementPreview` already establish for "opt-in behaviour
  a driver states, not every driver has":

  ```csharp
  public interface ILibraryCompatibilityProbe
  {
      Task<LibraryProbeResult> ProbeAsync(CancellationToken cancellationToken);
  }
  ```

  `MsSqlDriver`'s implementation touches the specific typed surface it actually depends on —
  constructing a `SqlBulkCopy`, referencing the `SqlDbType` values `MsSqlSegmentScope` uses — **needing
  no real database connection**: none of that typed surface requires an *open* connection to prove it
  resolves and constructs without throwing. This is a real, useful property worth stating plainly: the
  probe answers a pure CLR-level question, so it can run whether or not a real source/target is
  reachable right now.

- **`dbdatasync config library validate <id>`** — a new, real, documented verb on `LibraryCommand.cs`,
  alongside its existing `install|sync|list|uninstall`. **Not** `dbdatasync internal ...` — checked
  `InternalCommand.cs` directly: it's explicitly documented as *"commands for the build, not for an
  operator... deliberately absent from `Help.Print`... the Dockerfile is the only caller"* — the wrong
  semantic home for something an operator (or the SPA, on their behalf) is meant to trigger.
  `validate` runs the matched driver's `ProbeAsync` and reports structured pass/fail.
- **Spawned as a child process**, reusing `ProcessSupervisor.BuildStartInfo`'s existing `dotnet exec
  <dll>` shape (the same one that spawns `DbDataSync.TaskRunner`) rather than a new mechanism, from
  whichever API endpoint exposes this to the SPA.

### 5. Where this surfaces in the product — two different places, on purpose

Not both folded into Connections page "Test connection," despite that being the phase's own original
framing — the two checks have different natural scopes:

- **The static check's result** surfaces as part of `IConnectionTester.TestAsync`'s existing flow — it's
  already computed (from `config check`/`install` time), cheap, in-process, and a connection using a
  library with missing members is a genuinely relevant thing to say right there: *"Connected, but the
  installed Microsoft.Data.SqlClient (8.1.0) is missing 2 members this driver uses — see Libraries for
  detail."*
- **The `validate` smoke-test** belongs on the **Libraries admin screen** (`AdminLibrariesPage`,
  already in this product), not per-connection — a library like `microsoft-data-sqlclient` is shared
  across every MsSql connection, not scoped to one, and the child-process spawn cost is real enough
  that firing it automatically on every ordinary connection test would be the wrong default. A
  "Validate" action beside each installed library's row is the natural home; a connection's own Test
  result can still link to it.

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

## Open questions

1. **Mono.Cecil vs. AsmResolver** — both real, mature choices for item 2's extraction step; pick one
   during implementation based on which reads more naturally against this codebase's own style, not
   decided here.
2. **Exactly which typed members `MsSqlDriver`/`PostgresDriver`'s own `ILibraryCompatibilityProbe`
   implementations touch** — the compatibility table in `nuget-loaded-drivers.md` names the categories
   (`SqlBulkCopy`, `SqlDbType` ×28, `NpgsqlDbType` ×17, the connection-string builders); the exact probe
   code is an implementation-time task, not specified line-by-line here.
3. **Whether `config check`'s new line should be per-driver or per-installed-library** — a library used
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
- **`dbdatasync config library validate microsoft-data-sqlclient`**, run standalone — reports success
  against a healthy install; a deliberately corrupted/truncated install (or a much older pinned
  version known to lack a member `MsSqlDriver` uses) reports the specific failure, not a generic crash.
- **`config check`** — the new line appears, `Warn`s with specific member names when triggered, silent
  when the installed library is clean.
- Confirm the child-process `validate` run leaves no `Microsoft.Data.SqlClient` assembly resident in
  the calling API host's own process afterward — the acceptance test for why this needed isolation in
  the first place, not just an assertion that it "should."

## References

- `architecture/implementation/todo/phase-109h-builtin-drivers-off-provider-packages.md`,
  `phase-109i-duckdb-decoupling.md` — the risk this phase resolves for both.
- `src/DbDataSync.Libraries/LibraryRegistry.cs`, `LibraryPaths` — the existing resolution/on-disk-layout
  this phase reads from, unchanged.
- `src/DbDataSync.Api/Services/ProcessSupervisor.cs` (`BuildStartInfo`) — the `dotnet exec` shape this
  phase's child-process spawn reuses rather than reinvents.
- `src/DbDataSync.Drivers.Abstractions/IConnectionTester.cs`, `ConnectionsController.cs:126` — the
  existing Test Connection flow the static check's result surfaces through.
- `src/DbDataSync.Cli/InternalCommand.cs` — read in full to confirm it's the wrong home for `validate`,
  not guessed.
