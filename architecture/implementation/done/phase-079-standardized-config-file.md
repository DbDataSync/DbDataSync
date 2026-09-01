# Phase 79 — a standardized datasync.config.yaml, and command-shared config resolution

**Status**: Complete.
**Plan reference**: `architecture/planning/done/standardized-config-file.md`. Format (YAML) and directory
lookup (walk up from cwd, like git) were agreed there. **Revised 2026-09-01**: the file is git-tracked,
not gitignored — a credential-bearing setting is rejected outright and has to go through the secret
store instead, the same rule `ConfigValidation.RejectEmbeddedCredential` already enforces for connection
strings. This doc reflects that revision throughout.

## What was built

**A shared root resolver**, `DataSync.Cli.DataSyncRoot` (`src/DataSync.Cli/DataSyncRoot.cs`): explicit
`--repo` wins outright; otherwise walks upward from the current directory, checking each parent for
`datasync.config.yaml` — the same shape `git` uses to find `.git`; otherwise falls back to
`CliOptions.DefaultRoot`, unchanged. Used by `serve`, `invite` and `health`. The walk stops at the
filesystem root (`DirectoryInfo.Parent` returning `null`), not at the user's home directory — the doc
left this bound open as "whatever's simplest to implement correctly," and the filesystem root needs no
extra detection logic.

`datasync service install` was **not** touched, per the doc's explicit scope boundary — its `--repo`/
`--url` handling, and the values it bakes into `binPath`, are byte-for-byte what they were before this
phase.

**`datasync.config.yaml` reader/writer**, `DataSync.Core.Config.DataSyncConfigFile`
(`src/DataSync.Core/Config/DataSyncConfigFile.cs`). `Read` parses the file with `YamlDotNet`'s
object-mapping deserializer into a flattened `Section:Key` dictionary — ASP.NET Core's own configuration
key shape — recursing through nested maps and lists (list handling is speculative: no current
`DataSync:*` key is an array, but `DataSync:Auth:Passkeys:Origins` elsewhere in this codebase is one, so
the flattener handles that shape too rather than assuming it will never appear here).

`SetValue(repoRoot, section, key, value)` is **not** a round trip through YamlDotNet's serializer —
that was tried first and abandoned. YamlDotNet's object-mapping API (what `YamlConfigSerializer`
already uses elsewhere in this codebase) has no comment-preserving round trip; only the low-level
parser/emitter event stream sees comments at all, and reconstructing a document from that stream while
splicing in one new value is a lot of machinery for a file with a handful of keys. `SetValue` instead
edits the text directly: it finds the file's section header line and the target key's live (uncommented)
line if one exists, replaces just that line's value, or inserts a new line right after the section
header if the key isn't there yet — every other line, comments included, passes through untouched. The
tradeoff, stated in the method's own doc comment: this assumes the flat, two-level shape the starter
file and every documented `DataSync:*` key actually use (`Section:` then two-space-indented `Key: value`
lines); it does not handle arbitrary YAML nesting on the write side the way `Read` does on the read side.
Sufficient for this phase and for phase 81's admin screen, which needs to write the same flat keys.

`WriteStarter(repoRoot)` writes the doc's literal starter content (every key present, commented out,
including the secret-ref comment) if no file exists yet; a no-op otherwise.

**Credential rejection is reused, not duplicated.** `ConfigValidation.RejectEmbeddedCredential` changed
from `private` to `internal` (not `public` — nothing outside `DataSync.Core` needs it, and `internal` is
enough for `DataSyncConfigFile` in the same assembly). `SetValue` calls it on `StateConnectionString`
before anything hits disk; a rejected write leaves the file exactly as it was.

**`SecretRefs.ForAppSetting(string key)`** added alongside the existing `ForConnection`, fixed and
non-overridable as specified: `datasync:config:{key}`. The one ref this phase actually mints is
`datasync:config:stateConnectionString`.

**`StateDatabase.FromOptions`**, in `src/DataSync.State/StateDatabase.Factory.cs` — a second partial-class
file for `StateDatabase` rather than a new type, so it reads as part of the same class's public surface.
Signature: `FromOptions(StateEngine engine, string stateDbPath, string? stateConnectionString,
SecretStore secrets)`. **Placement decision the doc left open**: it lives in `DataSync.State`, not
`DataSync.Core`, and does *not* take `ApiOptions` — `ApiOptions` is defined in `DataSync.Api`, which
already depends on `DataSync.State` (for `StateDatabase` itself), so an `ApiOptions` parameter here would
be a dependency cycle. It takes the same values as plain parameters instead. `ClrKernel.Core.Secrets`
needed no new `PackageReference` in `DataSync.State.csproj` — it's already reachable transitively through
the existing `DataSync.Core` project reference (confirmed by building `DataSync.State` in isolation).
`DataSyncHost.cs`'s state-database registration (previously an inline branch at the line numbers the
doc cites) and `InviteCommand.cs` both call this factory now; neither constructs a `StateDatabase`
directly any more, which tightened `DataSync.Api.Tests.StateOwnershipTests`' invariant from "two named
exceptions" to "exactly one legitimate construction site" (see "A real bug found," below).

**`DataSyncHost.Build` wiring**, `src/DataSync.Api/DataSyncHost.cs`. `InsertConfigFile(builder)` runs
immediately after `WebApplication.CreateBuilder(args)` returns: it reads `DataSync:RepoRoot` straight off
`builder.Configuration` (not through `ApiOptions`, which isn't resolvable yet — it comes from DI, built
later in the same method), defaulting the same way `ApiOptions.FromConfiguration` does, and — only if
`datasync.config.yaml` exists there — wraps the flattened dictionary in a `MemoryConfigurationSource` and
inserts it at the index of the first `EnvironmentVariablesConfigurationSource` in
`builder.Configuration.Sources`, reproducing `appsettings.json`'s own precedence slot: behind environment
variables and the command line, ahead of nothing but code defaults. Proven by
`tests/DataSync.Api.Tests/DataSyncConfigFilePrecedenceTests.cs`, which calls `DataSyncHost.Build`
directly (not through `WebApplicationFactory`, since this is about what `IConfiguration` resolves to, not
about serving a request) and checks all three cases: file-only, file-plus-CLI-arg, file-plus-env-var.

**`InviteCommand`'s SQLite-only bug is fixed.** It now resolves `DataSync:StateEngine`/
`DataSync:StateConnectionString` through `DataSyncConfigFile.Read` plus `DataSync__*` environment
variables (no dedicated `--state-engine`/`--state-connection-string` CLI flags were added — this
command's surface stays small, and both are ordinary `DataSync:*` settings already reachable the way
every other one is), then calls `StateDatabase.FromOptions`. Proven end to end by
`tests/DataSync.Cli.Tests/InviteCommandTests.cs`, which writes the secret via `datasync secret set` (the
actual CLI command, not a direct `SecretStore` call) and then runs `datasync invite` against a real
`StateEngine: MsSql` repo. This needs a live SQL Server and fails with a connection error in this sandbox
(no Docker available) — see "How it was verified," below.

**`HealthCommand`** falls back to `--url`, then the resolved config's `DataSync:Url`, then the hardcoded
`http://127.0.0.1:8080`. It now goes through `DataSyncRoot.Resolve`, which means it also gained an
(undocumented-until-now) `--repo` flag — used only to find a config file, nothing else. The container's
own `HEALTHCHECK` always passes `--url` explicitly, so it's unaffected either way.

**`DataSync:Url`** is a real key now. `ServeCommand` resolves it as `--url` flag → `DataSync__Url` env var
→ the resolved config file's `DataSync:Url` → the `http://localhost:5080` default, then does the same
translation to `--urls` it always did. The env-var check is a direct `Environment.GetEnvironmentVariable`
call rather than a full `IConfiguration` chain — `ServeCommand` needs this one value *before*
`DataSyncHost.Build` (and its own `IConfiguration` chain) exists, since the translation to `--urls` has to
happen first.

**Starter file.** `ServeCommand.Prepare` writes it — via `DataSyncConfigFile.WriteStarter` — only on the
branch where `Repository.IsValid(root)` was false before this run (a genuinely fresh root), and commits
it through a throwaway `GitCommitService(root)` attributed to `CurrentUser.SystemAuthor` (`DataSync
<datasync@localhost>`), the same fixed identity `DataSyncHost` already falls back to when nobody is
signed in. `Prepare` changed from `private` to `internal` with `InternalsVisibleTo` added to
`DataSync.Cli.csproj` for `DataSync.Cli.Tests`, so this could be tested directly rather than through a
full `serve` run (which starts Kestrel).

**`datasync secret set|list|remove`**, `src/DataSync.Cli/SecretCommand.cs`, wired into `Program.cs`'s
dispatch and `Help.cs`. `set`/`remove` are direct `SecretStore.Store`/`Delete` wrappers. `list` needed a
real design decision the doc didn't fully resolve: **`SecretStore` has no enumeration API** — reflecting
its actual public surface (`Store`, `TryResolve`, `Resolve`, `Delete`, `AddProvider`, `ProviderNames`;
confirmed by loading the installed `ClrKernel.Core.Secrets` package and reflecting its types) confirmed
there is no "list every stored ref" method, which makes sense for a thin layer over an OS credential
store that doesn't offer one either. `datasync secret list [<ref> ...]` therefore reports presence
(`TryResolve`, never the value) for the refs it's given, or — given none — for the one fixed ref this
build defines, `datasync:config:stateConnectionString`. This is an honest answer to what `SecretStore`
can actually say, not a simulated directory listing.

## Decisions made

- Format YAML, walk-up-from-cwd lookup, filesystem-root bound — settled per the doc.
- **Git-tracked, not gitignored**, per the doc's revision — nothing sensitive is ever in the file, so it
  gets ordinary diff/revert history instead of a weaker untracked guarantee. Committed by
  `ServeCommand.Prepare` directly (a throwaway `GitCommitService`), since the moment the starter file is
  written is before `DataSyncHost`'s DI container — and the `GitCommitService`/`SecretStore` singletons
  it owns — exists.
- **`StateDatabase.FromOptions` lives in `DataSync.State`, taking plain values rather than `ApiOptions`**
  — resolving the doc's open question by dependency direction: `DataSync.Api` already depends on
  `DataSync.State`, so the reverse would cycle.
- **The config-file writer edits text in place rather than round-tripping YamlDotNet's serializer** —
  the doc explicitly allowed this fallback if comment preservation "makes it easy" and to note the
  tradeoff otherwise; it wasn't easy (no comment-preserving round trip exists in the API this codebase
  already uses elsewhere), so this was the deliberate choice, not a shortcut taken silently.
- **`datasync secret list` reports presence for named/known refs, not a full listing** — forced by
  `SecretStore`'s actual API surface, confirmed by inspection rather than assumed.
- **`InviteCommand` gained no new CLI flags** (`--state-engine`, `--state-connection-string`) — resolved
  through the config file and `DataSync__*` environment variables only, consistent with every other
  `DataSync:*` setting and with the doc's "resolves ApiOptions-equivalent values through the shared
  resolver/reader" phrasing (not "add flags for them").
- **`datasync service install` is untouched**, per the doc.
- **`Prepare` (in `ServeCommand`) and `Resolve(args, startDirectory)` (in `DataSyncRoot`) are `internal`**,
  exposed to a new `DataSync.Cli.Tests` project via `InternalsVisibleTo` — the same pattern
  `DataSync.Scripting.csproj` already uses for `DataSync.Scripting.Tests`.

## A real bug found (in this codebase's own tests, not the product)

`DataSync.Api.Tests.StateOwnershipTests.Only_the_api_opens_the_state_file` scans `src/**/*.cs` for the
literal text `new StateDatabase(` and asserts an exact, named list of files allowed to contain it. Moving
both real construction sites into `StateDatabase.FromOptions` should have made this list *shrink* to one
file — but the first run left `InviteCommand.cs` in the failing diff anyway, because a comment this
phase wrote there (explaining what the code used to do) happened to contain the literal substring
`` `new StateDatabase(stateDb)` ``. The test doesn't distinguish code from comments. Fixed by rewording
the comment (it no longer needs to quote the old call verbatim to make its point) rather than loosening
the test — and the test's expected list was updated to the one legitimate file,
`src/DataSync.State/StateDatabase.Factory.cs`, which is a **stronger** invariant than the two-file
exception list it replaced.

## How it was verified

- **Resolver** (`tests/DataSync.Cli.Tests/DataSyncRootTests.cs`): explicit `--repo` wins outright, even
  short-circuiting a `datasync.config.yaml` on the walk-up path; cwd has the file; a parent several
  levels up has it; neither has it and `CliOptions.DefaultRoot` is used. All against real temp
  directories, not mocked.
- **Precedence** (`tests/DataSync.Api.Tests/DataSyncConfigFilePrecedenceTests.cs`): a file-only value is
  read; the same key set via `--DataSync:Url` wins; the same key set via `DataSync__Url` wins; no file at
  all still builds a host with ordinary defaults.
- **Credential rejection** (`tests/DataSync.Core.Tests/DataSyncConfigFileTests.cs`): a
  `StateConnectionString` containing `Password=`/etc. is rejected with nothing written to disk; one
  without a credential succeeds and reads back correctly; comments and other keys survive a `SetValue`
  call untouched.
- **`datasync secret set` → state store connects with it** (`tests/DataSync.Cli.Tests/InviteCommandTests.cs`):
  writes the secret through `SecretCommand.Run(["set", ...])`, then runs `InviteCommand.Run` against a
  real `StateEngine: MsSql` repo and asserts success and a well-formed invite URL. **Needs a live SQL
  Server** (`DATASYNC_TEST_MSSQL_SERVER`, default `localhost,14330` — the same Docker container every
  other MsSql-backed test in this repo expects) and fails with a connection-timeout `SqlException` in
  this sandbox, which has no Docker available. Same failure mode as every other MsSql-backed test here
  (`MsSqlTestDatabase`, `StateEngineFixture`) — not a new gap.
- **`StateDatabase.FromOptions`** (`tests/DataSync.State.Tests/StateDatabaseFactoryTests.cs`): SQLite uses
  the by-path constructor and ignores a garbage "connection string"; a non-SQLite engine with no
  connection string throws naming `DataSync:StateConnectionString`; a stored secret is spliced onto the
  connection string (proven by connecting to an address nothing listens on and checking the failure is
  network-shaped, not an `ArgumentException` from a malformed connection string — i.e., the splice
  produced something SqlClient accepted as well-formed).
- **`HealthCommand`** (`tests/DataSync.Cli.Tests/HealthCommandTests.cs`): a resolved config `Url` is used
  with no `--url` passed (against a real loopback `HttpListener`, not a mock); `--url` still wins when
  both are present.
- **`ServeCommand.Prepare`** (`tests/DataSync.Cli.Tests/ServeCommandPrepareTests.cs`): a fresh repo root
  gets a starter file containing the secret-ref comment, committed to git; a second `Prepare` call against
  an already-initialized root leaves an operator-edited file untouched; a root with a pre-existing git
  repository but no config file never gets one written.
- **`datasync secret set|list|remove`** (`tests/DataSync.Cli.Tests/SecretCommandTests.cs`): set-then-list
  reports "set" without ever printing the value; an unset ref reports "not set"; remove reverts it to
  "not set"; a malformed `set` invocation fails without storing anything.
- **Phase 35's config-history question** (an "open question to resolve during implementation" in this
  doc's original version): **not resolved as a widening of phase 35's scope** — `GitCommitService.GetHistory`
  filters by a relative path *prefix* (e.g. `config/replications/<name>`), and `datasync.config.yaml`
  sits at the repo root, one level above `config/`, so it is git-tracked and diffable at the command line
  (`git log -- datasync.config.yaml` inside the repo root) but does **not** currently surface in the
  Config History UI's `config/`-scoped queries. Widening that query scope is phase 35's or phase 81's
  call to make, not implied by this phase landing — noted here so neither has to rediscover it.
- Full-solution `dotnet build DataSync.slnx` — clean, 0 warnings introduced (a handful of pre-existing
  warnings elsewhere, unchanged).
- Full test run, all projects, compared against an unmodified-`HEAD` baseline built in a throwaway `git
  worktree` (not `git stash`, which this environment's tooling declined to run) to separate real
  regressions from this sandbox's pre-existing environmental failures:
  - `DataSync.Api.Tests`: baseline 174 failed / 59 passed / 233 total; this branch 174 failed / 63 passed
    / 237 total. **Identical failure count**, four new passing tests (the precedence suite) — confirming
    the ~174 pre-existing failures (a `System.NotSupportedException: Negotiate authentication requires a
    server that supports IConnectionItemsFeature like Kestrel` cascading through most controller tests
    under `TestServer`) are unrelated to this phase and were already present on `main`.
  - `DataSync.Core.Tests`: 132 passed / 35 failed, all 35 in `ConfigRepositoryTests` — a pre-existing,
    environment-specific teardown issue (`libgit2` writes read-only object files; plain
    `Directory.Delete(recursive: true)` refuses to remove them on Windows in this sandbox), not a
    regression. The 12 new `DataSyncConfigFileTests` — which touch no git repository — all pass.
  - `DataSync.State.Tests`: 151 passed / 27 failed, all 27 in `CrossEngineStateTests` against MsSql/
    Postgres (no server reachable in this sandbox). The 4 new `StateDatabaseFactoryTests` pass.
  - `DataSync.Cli.Tests` (new project): 15 passed / 1 failed — the MsSql-backed `InviteCommandTests`
    case described above.
  - The `libgit2` read-only-object teardown issue was worked around in this phase's *own* new tests
    (`tests/DataSync.Cli.Tests/GitTempDirectory.cs`, a small `File.SetAttributes(..., Normal)` sweep
    before `Directory.Delete`) so they report cleanly regardless of this sandbox quirk — the pre-existing
    `ConfigRepositoryTests` was left as-is, since fixing test infrastructure this phase doesn't own is
    out of scope for it.

## What's explicitly still not built

- Config import/export — separate, unresolved planning doc; not this.
- Hot-reload of any setting — `ApiOptions` stays a singleton resolved once at startup, unchanged.
- The admin-facing UI for viewing/editing this file — phase 81, which depends on this one landing (it now
  has).
- Widening phase 35's Config History UI to surface `datasync.config.yaml` commits — investigated (see
  above) and left as a decision for phase 35 or phase 81, not assumed here.
- Any Linux/systemd-specific handling — unnecessary, since this slots in as an ordinary `IConfiguration`
  source and works identically wherever `datasync` runs.
