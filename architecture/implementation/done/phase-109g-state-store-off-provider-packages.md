# Phase 109g — the state store off the hard package references

**Status**: Done.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md` §*How little actually
couples to a provider*, §*The provider layer*. Depended on 109c/109f in the original plan; both of
those, and the "provider" layer itself, no longer exist under those names — see the terminology note
immediately below.

## Terminology note — read this before the rest of the doc

This doc was written before phase 116 renamed "provider" → "library" throughout the codebase. As
originally planned, it described a `ProviderRegistry` class and a `dbdatasync provider install` CLI
verb — **neither exists**. The real, current mechanism — fully built and shipped by phases 116–122,
already in production use by the driver layer before this phase touched anything — is
`DbDataSync.Libraries.LibraryRegistry` (`src/DbDataSync.Libraries/LibraryRegistry.cs`), with a
`GetFactory(id)` method returning a `System.Data.Common.DbProviderFactory`, backed by
`KnownLibraries.cs`'s catalog (which already carried `microsoft-data-sqlclient` →
`Microsoft.Data.SqlClient` and `npgsql` → `Npgsql` entries), and the CLI verb is `dbdatasync config
library install <id>` (`src/DbDataSync.Cli/LibraryCommand.cs`). Every "provider" reference below has
been rewritten to match what was actually built; the underlying architectural goal the original doc
described — decouple `DbDataSync.State`'s MsSql/Postgres backends from hard `PackageReference`s, the
same way the driver layer already had — was unchanged and is what this phase delivered.

## What this built

`DbDataSync.State` no longer references `Microsoft.Data.SqlClient` or `Npgsql`. Its SQL Server and
PostgreSQL backends resolve their `DbConnection` through `LibraryRegistry` instead — the same mechanism
`DriverConnectionFactory`/`DriverRegistry` already used for the driver layer, and the same
"`GetFactory(id).CreateConnection()`, then set `ConnectionString`" shape `GenericDriver` already used
for a descriptor-defined driver's connection. SQLite is a hard reference still — it is the default, it
is offline and serverless, and decoupling it buys nothing (unchanged from the original plan).

### `src/DbDataSync.State/`

- `MsSqlStateDialect.cs` — no more `using Microsoft.Data.SqlClient`. `CreateConnection(cs)` is now
  `_libraries.GetFactory("microsoft-data-sqlclient").CreateConnection()` with `.ConnectionString = cs`
  set afterward. The library id is `MsSqlStateDialect.LibraryId`, a public const, rather than a string
  repeated at each call site.
- `PostgresStateDialect.cs` — same shape, library id `"npgsql"` (`PostgresStateDialect.LibraryId`).
- **Both dialects stopped being stateless static singletons.** The original doc's own words for this —
  "a small constructor change threaded through `StateDialectRegistry` registration" — undersold how
  real a design decision it turned out to be: `StateDialectRegistry.Default` is a static, process-wide,
  eagerly-built singleton (`BuildDefault()`, run once at class-load time), and `MsSqlStateDialect`/
  `PostgresStateDialect` used to be parameterless (`private ctor` + `static Instance`) precisely because
  they had no per-process state to hold. A `LibraryRegistry` *is* per-process state — it knows which
  repo root's `libraries/` directory to trust — so it cannot be baked into a static field built before
  any repo root is known. Resolved by:
  - Giving both dialects a real constructor: `MsSqlStateDialect(LibraryRegistry libraries)` /
    `PostgresStateDialect(LibraryRegistry libraries)`.
  - Adding `StateDialectRegistry.RegisterLibraryBackedEngines(LibraryRegistry)`, which constructs and
    `Register`s both against the given registry. `BuildDefault()` now registers only
    `SqliteStateDialect.Instance` (which stays a stateless singleton, unchanged).
  - Calling `RegisterLibraryBackedEngines` once, inside `StateDatabase.FromOptions` — the one place
    every real caller (`DbDataSyncHost.cs`, `InviteCommand`, `ReadinessChecks`, the setup wizard's
    `SetupSteps.ApplyStateDatabase`) already goes through to open a non-SQLite state store, and the one
    place that already had (or could trivially build) a `LibraryRegistry` for its own repo root, since
    all four already construct or resolve one for the driver layer. `FromOptions` gained a required
    (not optional/defaulted) `LibraryRegistry libraryRegistry` parameter — the same "no automatic
    magic, every dependency explicit" preference `DriverConnectionFactory` already applies to
    `DriverRegistry`.
  - `Register` (and so `RegisterLibraryBackedEngines`) is idempotent — it just replaces the dict entry
    for that engine id — so calling it more than once per process (a second `FromOptions` call; several
    test fixtures in the same run) is harmless *as long as nothing races it*, which is the real finding
    below.
- `DbDataSync.State.csproj` — the `Microsoft.Data.SqlClient` and `Npgsql` `PackageReference`s are gone;
  `Microsoft.Data.Sqlite` stays; a new `ProjectReference` to `DbDataSync.Libraries` was added.
- `SqliteStateDialect` / `StateDatabase` — unchanged, still `new SqliteConnection`, still
  `SqliteConnectionStringBuilder`, still the `SqliteException` retry codes.

### `StateDatabase.FromOptions` (`StateDatabase.Factory.cs`)

- Requires `LibraryRegistry libraryRegistry` now (see above). For `MsSql`/`Postgres`, calls
  `StateDialectRegistry.Default.RegisterLibraryBackedEngines(libraryRegistry)` before constructing the
  `StateDatabase`, so `StateDialect.For(engine)` resolves against *this* registry inside the
  constructor that follows. Ignored (parameter present but untouched) for SQLite, same as
  `stateConnectionString` already was.
- A missing library now fails exactly where the original plan wanted "at startup, with a clear
  message naming the fix" — but the message is `LibraryRegistry.GetFactory`'s own, surfaced unmodified
  by `CreateConnection` rather than reinvented here: *"Library 'microsoft-data-sqlclient' is not
  installed. Install it with `dbdatasync config library install microsoft-data-sqlclient`."* The plan's
  draft wording ("State engine 'MsSql' needs the Microsoft.Data.SqlClient provider...") was deliberately
  not used — reusing the one message this codebase already has for "a library isn't installed" beats
  inventing a second phrasing of the same fact.
- The credential splice (`;Password=…` appended, per phase 79) is unchanged — string-only, doesn't
  touch the library layer at all.

### The four real callers, updated to pass a `LibraryRegistry`

- `DbDataSyncHost.cs` — `sp.GetRequiredService<LibraryRegistry>()`, the same singleton the driver layer
  already resolves through (registered earlier in `Build()`), so a missing library fails during the DI
  singleton graph's construction — i.e. at process startup, before the host serves anything — not on
  first HTTP request.
- `InviteCommand.cs` — builds its own `new LibraryRegistry(root).LoadAll()`, matching the exact idiom
  `LibraryCommand.List()` and `TaskRunner/Program.cs` already use for a one-shot process.
- `ReadinessChecks.cs` (`ReadinessContext.TryOpenStateDatabase`) — same idiom; a missing library now
  shows up as `dbdatasync config check`'s existing "State store: FAIL" line, with `GetFactory`'s message
  as the detail — no new check needed, since `StateStoreCheck` already just tries to open the database
  and reports whatever exception comes back.
- `Tui/SetupSteps.cs` (`ApplyStateDatabase`) — same idiom. Its old comment ("installing the library
  itself is skipped here — before phase 109g lands...") is rewritten; see "installer default" below for
  why it stays a comment about a manual step rather than becoming an install call.

## Installer / first-run default — decision made, and why

The original doc's open question ("do the two default provider manifests ship in the repo template,
or get seeded lazily") doesn't survive contact with the actual mechanism: there is no "manifest," a
library install is a real `dotnet publish`-backed NuGet restore (`LibraryInstaller.InstallAsync`), and
nothing in this codebase ships a *restored* package inside the repo itself (the closest precedent,
phase 121's build-catalog-cache, restores into the *build image*, not into a deployed repo's working
tree).

There **is** a live precedent for "the setup wizard installs a library automatically as part of a
step": `SetupSteps.InstallMySqlDriverAsync`, which takes an `installLibrary` delegate
(`Func<string,string,IReadOnlyList<PackageRef>,string,string?,CancellationToken,Task<LibraryManifest>>`)
threaded down from `SetupCommand.RunAsync` (production: a method-group reference to
`LibraryInstaller.InstallAsync`) through `SetupScreen.RunAsync` → `DriversTab.SaveAsync`. Extending
`SetupSteps.ApplyStateDatabase` (and `StateDatabaseTab.Save`) the same way was considered and
deliberately **not done in this phase**: `StateDatabaseTab.Save(string root)` is synchronous today, and
threading an async install through it means the same signature change `DriversTab`/`SetupScreen`
already absorbed once — new progress affordance, new failure-reporting shape, a second call site in
`SetupScreen.cs` to rewire — which is a real, separable UX decision, not a "small constructor change"
this phase's scope should absorb by the way.

**What was actually chosen**: the manual step stays manual. `dbdatasync config library install
microsoft-data-sqlclient` / `npgsql` once, after upgrading (or before first choosing that
`StateEngine`), is the documented fix — named automatically, every time it's needed, by
`LibraryRegistry.GetFactory`'s own exception message surfacing through `StateStoreCheck`
(`dbdatasync config check`) and through `ApplyStateDatabase`'s existing "Could not connect yet: …"
report in the setup wizard. No new seeding machinery was built. If the setup-wizard auto-install is
ever wanted, `InstallMySqlDriverAsync`'s shape is the template to copy — sketched above so the next
phase that wants it doesn't have to rediscover the seam.

## How it was verified

- `dotnet build DbDataSync.slnx` clean.
- `dotnet list package` against `src/DbDataSync.State/DbDataSync.State.csproj` shows exactly one
  direct package reference (`Microsoft.Data.Sqlite`) — `Microsoft.Data.SqlClient`/`Npgsql` are gone.
- Full non-integration `DbDataSync.State.Tests` suite: 187 passed.
- Full `Category=Integration` `DbDataSync.State.Tests` suite, against the real `dbdatasync-mssql-source`
  (SQL Server) and `dbdatasync-postgres` containers already running locally: 45 passed — every store
  test in `CrossEngineStateTests` now reaches its MsSql/Postgres connection through a
  `LibraryRegistry`-resolved `DbProviderFactory` rather than a direct reference, via a new
  `LibraryInstallFixture` (`IClassFixture`, install-once-per-class, the same shape
  `DbDataSync.Api.Tests`' `DescriptorDriverApiFactory` already uses for MySqlConnector) that restores
  both libraries for real once per test class.
- Full `DbDataSync.Cli.Tests` (109, including the fixed `InviteCommandTests` — see below) and full
  `DbDataSync.Api.Tests` non-integration (422 passed, 23 skipped — Windows-only, expected on this Linux
  sandbox) suites green. `DbDataSync.Api.Tests`' full `Category=Integration` filter turned out to be the
  wrong target to run wholesale — on inspection it is almost entirely MySQL/DuckDB/replication-driver
  end-to-end tests unrelated to the state store (a full run exceeded a 590s budget without finishing,
  which is itself the evidence it isn't "the state-backend Integration tests" the plan doc's phrasing
  assumed exists as a distinct group — that coverage actually lives entirely in
  `DbDataSync.State.Tests`, run above). Ran `DescriptorDriverTests` (`Category=Integration`, 2 tests)
  instead, as a targeted smoke test that the API host still boots correctly end-to-end (DI graph,
  `LibraryRegistry` singleton, `StateDatabase` factory lambda) under `Category=Integration` conditions —
  green. Every non-integration `Api.Tests` run already boots the same `DbDataSyncHost.Build()` graph
  (via `TestApiFactory`, a `WebApplicationFactory`) against SQLite, so the changed
  `sp.GetRequiredService<LibraryRegistry>()` line in `DbDataSyncHost.cs` was exercised 422 times over
  before `DescriptorDriverTests` added the one real-library-install case.
- New `StateLibraryMissingTests`: `StateEngine` `MsSql`/`Postgres` with nothing installed under a fresh
  repo root fails `StateDatabase.FromOptions` with an `InvalidOperationException` naming the library id
  and the exact `dbdatasync config library install <id>` command, and fails *before* any network attempt
  (asserted with a connection string that would otherwise hang for its full 30-second timeout).
- SQLite regression check: `SqliteStateDialect`/`StateDatabase`'s SQLite path was not touched at all;
  every existing SQLite-backed test in the suite (the overwhelming majority — single-arg
  `new StateDatabase(path)` constructor, never touching `StateDialectRegistry` for a non-default engine)
  passed unmodified, which is the byte-identical proof the plan asked for — there was no SQLite-specific
  code path to introduce a difference in.

## Decisions made, and a real bug found

- **A real race, caught before it shipped, not after.** `StateDialectRegistry.Default` is one shared,
  mutable, process-wide slot per engine id. Binding `MsSqlStateDialect`/`PostgresStateDialect` instances
  — each closed over a specific `LibraryRegistry` — into that slot means two tests running concurrently
  with *different* `LibraryRegistry`s (one with the library installed, one without, say) could each
  overwrite the other's registration between one test's `RegisterLibraryBackedEngines` call and its own
  `StateDialect.For(engine)` lookup a few lines later inside `StateDatabase`'s constructor. This is not
  hypothetical: xUnit runs test classes in parallel by default, and `DbDataSync.State.Tests` was the one
  integration-bearing test assembly in this repo that had never needed
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]` before — every other one
  (`Api.Tests`, `Cli.Tests`, `MsSql.Tests`, `Postgres.Tests`, `TaskRunner.Tests`) already carries it, for
  its own, unrelated reasons (mostly SQL Server server-scoped lock contention). Added a new
  `AssemblyInfo.cs` there for the same attribute, which fully removes the race (different test
  *assemblies* already run in separate `dotnet test` processes, so no cross-assembly exposure exists;
  within the assembly, sequential execution means each test's register-then-resolve pair can never be
  interleaved by another). This was flagged and fixed during implementation, not discovered by a flaky
  CI run — reasoned through from `StateDialectRegistry.Default`'s own doc comment, which already
  described it as "the process-wide registry every `StateDialect.For` call consults" before this phase
  gave that registry anything that could meaningfully vary per caller.
- **`FromOptions`'s new parameter is required, not optional/defaulted.** A nullable, defaulted
  `LibraryRegistry? = null` would have meant fewer test-file edits, but would also have meant a second,
  silent failure mode ("you forgot to pass one") indistinguishable from "you passed one but the library
  really isn't installed" unless it grew its own message — and every real caller already has a
  `LibraryRegistry` sitting right there for the driver layer's sake, so there was no legitimate caller
  this would have been doing a favour for.
- **`StateDialectRegistryTests` deliberately does not use `LibraryInstallFixture`.** It only asks
  `StateDialect.For` which *type* is registered under an engine id — it never calls `GetFactory` — so an
  empty `LibraryRegistry` (nothing restored) is enough, and using one keeps this a fast, network-free
  unit test rather than quietly turning it into an `Integration`-shaped one that happens not to be
  tagged as such.
- **`InviteCommandTests`' existing MsSql integration test needed a real library install added to its
  fixture**, not just a compile-time signature update — it asserts `dbdatasync invite` actually succeeds
  end-to-end against a real SQL Server, which now requires `microsoft-data-sqlclient` to be restored
  under the test's scratch repo root first, the same as a real admin would need to have run `config
  library install` once. Found by running the full `Cli.Tests` suite, not anticipated up front.
- **`SetupStepsTests`/`TabWiringTests`' existing MsSql-setup tests needed no changes at all** — they only
  assert `Assert.Contains("Could not connect yet", result.Message)`, which holds whether the underlying
  cause is a real network failure or (now, with nothing installed in a fresh test repo) a "library not
  installed" message; both are still `InvalidOperationException`s `ApplyStateDatabase`'s existing catch
  clause already handles identically.

## What's explicitly out of scope / not built

- Decoupling the **replication** MsSql/Postgres drivers (`DbDataSync.Drivers.MsSql`/`.Postgres`) — that
  is phase 109h, separate and not started; they still carry direct `Microsoft.Data.SqlClient`/`Npgsql`
  references, so a deployment doing replication *and* using one of these engines for state still pulls
  the package in via the driver project even though `DbDataSync.State.csproj` no longer names it.
- DuckDB decoupling — 109i, untouched.
- Any change to SQLite or `SqliteStateDialect` — confirmed unchanged, see verification above.
- Automatic library seeding on first run / inside the setup wizard — considered (see "Installer / first-
  run default" above), deliberately left a documented manual step rather than building new machinery a
  future phase can size and design on its own terms.
- A cross-engine state migration tool — was never in scope for 109g and remains untouched by it.
