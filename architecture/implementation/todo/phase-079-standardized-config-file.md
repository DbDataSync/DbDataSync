# Phase 79 — a standardized datasync.config.yaml, and command-shared config resolution

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/standardized-config-file.md`. Format (YAML) and directory
lookup (walk up from cwd, like git) were agreed there. **Revised 2026-09-01**: the file is git-tracked,
not gitignored — a credential-bearing setting is rejected outright and has to go through the secret
store instead, the same rule `ConfigValidation.RejectEmbeddedCredential` already enforces for connection
strings. This doc reflects that revision throughout; nothing below describes the gitignored version.

## What this phase will build

**A shared root resolver**, new in `DataSync.Cli`, used by `serve`, `invite`, `health` and `service
install` alike: explicit `--repo` wins outright; otherwise walk up from the current directory (checking
each parent in turn) looking for `datasync.config.yaml`; otherwise fall back to
`CliOptions.DefaultRoot`, unchanged from today. Today each of the four commands does its own
`CliOptions.Read(args, "--repo")` independently and none of them search — this is new plumbing shared
across all four, not a tweak to existing plumbing.

**`datasync.config.yaml` is git-tracked, at `<RepoRoot>/datasync.config.yaml`** — inside the same git
repository `ServeCommand.Prepare` already `git init`s (the whole `RepoRoot`, of which `config/` is one
tracked subdirectory), not a new one. Writes to it go through `GitCommitService`, the same commit-and-
attribute-per-user path `ConfigRepository` already uses for everything under `config/` — one open
question this raises is whether phase 35's config-history/diff-revert UI, which today shows the
`config/` subtree, needs widening to include this file too, or already would since it's the same repo;
worth confirming against phase 35's actual query scope during implementation rather than assumed here.

**A `datasync.config.yaml` reader.** `YamlDotNet` is already a dependency (the scripting host, phase 27),
so this is a small loader on top of it rather than a new package: parse the file into a flattened
`Section:Key` dictionary the same shape ASP.NET Core's own configuration providers use (`DataSync:Url`,
`DataSync:StateEngine`, `DataSync:StateConnectionString`, ...). Read in exactly one place, used two ways:

- **`DataSyncHost.Build`** wraps it as a small custom `IConfigurationSource`/`IConfigurationProvider`
  (a `MemoryConfigurationProvider` fed the flattened dictionary is enough — no need for a generalized
  YAML configuration provider). Inserted into `builder.Configuration.Sources` at the same position
  `appsettings.json` occupies — **not** appended after `WebApplication.CreateBuilder(args)` returns,
  which would wrongly put it *ahead* of environment variables and CLI args in precedence. Concretely:
  find the index of the first `EnvironmentVariablesConfigurationSource` in `builder.Configuration.Sources`
  and insert there, so a CLI flag or env var still overrides the file for a one-off run — this is the
  literal mechanism behind "url lives in config but still allows CLI flag/override," and it is easy to
  get backwards, which is why it is called out here rather than left as a detail.
- **`InviteCommand`/`HealthCommand`** read it directly through the shared resolver, since neither builds
  the full ASP.NET Core host — they need a couple of values, not the whole pipeline.

**No credential is ever written into the file.** `ConfigValidation` already has exactly the check this
needs — `RejectEmbeddedCredential`, which scans a connection string for `password`/`pwd`/`passwd`/
`secret`/`accountkey`/`apikey` as a whole key and throws, used today for `ConnectionConfig`. It needs to
stop being `private` so the config-file writer can call it on `StateConnectionString` (and any future
`DataSync:*` value that is itself a connection string) — one detector, reused, rather than a second copy
that can drift from the first.

**A standardized, uniquely-prefixed secret ref per setting**, resolved through the same
`ClrKernel.Core.Secrets.SecretStore` connections already use — `SecretRefs.ForConnection` gets a sibling:

```csharp
public static string ForAppSetting(string key) => $"datasync:config:{key}";
```

Fixed and non-overridable — `datasync:config:stateConnectionString` is the one name, not a config key an
admin points somewhere else, so there is exactly one thing to document (the starter file's comment,
below) and exactly one thing an admin ever has to type. At connect time, whatever resolves
`StateConnectionString` today (`DataSyncHost.cs:90-98`, and the same branch `InviteCommand` will call —
see below) resolves `SecretRefs.ForAppSetting("stateConnectionString")` through `SecretStore` and appends
it to the connection string it already builds, the same splice-at-connect-time shape
`DriverConnectionFactory` already uses for `ConnectionConfig.CredentialSecretRef`.

**A new CLI command, `datasync secret set|list|remove`**, thin wrappers over `SecretStore.Store`/
`TryResolve`/`Delete` — the same three calls `ConfigRepository.SaveConnection` already makes, just
reachable without going through a connection's own save flow. This is what "easily settable using the
CLI tool" means concretely: `datasync secret set datasync:config:stateConnectionString "Password=..."`.
Lives beside `ServiceCommand.cs` as its own `SecretCommand.cs`, added to `Program.cs`'s dispatch.

**`Url` becomes a real `DataSync:Url` key.** It has no `DataSync:*` key today — `ServeCommand` turns
`--url` straight into ASP.NET Core's own built-in `--urls` (Kestrel's bind address), never into a
`DataSync` setting. `datasync.config.yaml` sets `DataSync:Url`; `ServeCommand` reads it (via the
resolver) the same way it reads `--url` today and does the same translation to `--urls` it already does
— one key, one meaning, everywhere a URL is configured.

**`ServeCommand.Prepare` writes a starter file** for a genuinely fresh repo root (the `Repository.Init`
branch — never touches an existing one), with the common keys present but commented out, **and a comment
naming the exact secret ref and CLI command** for the one setting that needs one:

```yaml
# DataSync:
#   Url: http://localhost:5080
#   StateEngine: MsSql
#   # Connection string only — never a password. Set the password with:
#   #   datasync secret set datasync:config:stateConnectionString "Password=..."
#   StateConnectionString: "Server=sql01;Database=DataSyncState;"
```

**`InviteCommand` stops hardcoding SQLite.** It currently does `new StateDatabase(stateDb)`
unconditionally — no branch for `DataSync:StateEngine` being `MsSql` or `Postgres` at all, which means
today, an admin running a non-SQLite state store who locks themselves out cannot use `invite` to recover.
`DataSyncHost.cs` already has the right branch (SQLite-by-path vs. engine+connection-string, now also
resolving the secret) — extracted into a small shared factory (e.g. `StateDatabase.FromOptions
(ApiOptions, SecretStore)` in `DataSync.State`) so `DataSyncHost.cs` and `InviteCommand` make this
decision once between them, not twice. `InviteCommand` resolves `ApiOptions`-equivalent values through
the shared resolver/reader above, then calls the same factory.

**`HealthCommand` falls back to the resolved config's `Url`** when `--url` is not passed, before falling
back to its current hardcoded container default (`http://127.0.0.1:8080`) — so `datasync health` run
against a config-backed install doesn't need `--url` repeated on every invocation, while the container
health check (which has no config file to find) is unaffected.

**`datasync service install` is unchanged.** Its `--url`/`--repo` flags keep working exactly as today;
`binPath` still bakes `serve --repo ... --url ...`. Nothing here requires touching it — a config file
at the installed repo root is picked up by `serve` itself once this phase lands, so an admin who wants a
service to read `Url` from the file rather than the baked flag can simply not pass `--url` at install
time and let `serve`'s own resolution find the file, same as running it interactively.

## How it will be verified

- Resolver: unit tests over a temp directory tree — cwd has the file; a parent does and cwd is several
  levels below it; neither has it and `DefaultRoot` is used; `--repo` present short-circuits the search
  even when a `datasync.config.yaml` exists elsewhere on the way up.
- Precedence: an integration test against `DataSyncHost.Build` — a value set only in the file is read;
  the same value set in the file *and* as an env var or `--DataSync:*` CLI arg takes the env var/CLI
  value, not the file's.
- **A `StateConnectionString` containing `Password=`/`Pwd=`/etc. is rejected on save**, same assertion
  shape as the existing `ConnectionConfig` tests for `RejectEmbeddedCredential`, extended to cover this
  new caller.
- `datasync secret set` followed by a state-store connection that needs it: an integration test that
  writes the secret via the CLI command, then confirms `DataSyncHost.Build` (or `InviteCommand`) actually
  connects using it — the whole point is that the two are the same store, and the test proves it rather
  than asserting each side once each.
- The concrete regression this phase exists to fix: an integration test that `datasync invite` succeeds
  against a `StateEngine: MsSql`-configured repo, secret included (it would throw or silently talk to the
  wrong store today).
- `HealthCommand`: a test that a resolved config `Url` is used with no `--url` passed, and that `--url`
  still wins when both are present.
- `ServeCommand.Prepare`: a test that a fresh repo root gets a starter `datasync.config.yaml` with the
  secret-ref comment present, and that an existing repo root's file is left untouched on a second `serve`.
- Whatever phase 35's config-history tests assert about the `config/` subtree, confirmed (or extended) to
  also cover `datasync.config.yaml` once the "does it already show up" question above is answered.

## Decisions made

- Format YAML, walk-up-from-cwd lookup — settled in the planning doc and unchanged by this revision.
- **Git-tracked, not gitignored** — revised from the original plan. A credential is never in the file to
  begin with, so there is nothing sensitive to keep out of git; the file gets the same diff/revert
  history as the rest of `config/` instead of a weaker, untracked guarantee.
- Credential-bearing settings go through `SecretStore` under a fixed, standardized ref
  (`datasync:config:<key>`) — not an admin-chosen ref, not embedded in the file. One name to document,
  one CLI command to set it.
- `Url` gets its own `DataSync:Url` key rather than letting the file set ASP.NET Core's `urls` key
  directly, so "how to set the URL" stays one concept (`--url` / `DataSync__Url` / `DataSync:Url` in the
  file) instead of two depending on where you're setting it.
- `datasync service install` is explicitly out of scope for changes — it keeps working exactly as it
  does today.
- Fixing `InviteCommand`'s SQLite-only assumption is in scope for this phase, not split out, since the
  shared resolver this phase builds is what makes the fix straightforward.

## What this phase will not build

- Config import/export — separate, unresolved planning doc (`config-import-export.md`); not this.
- Hot-reload of any setting. `ApiOptions` stays a singleton resolved once at startup, same as every other
  configuration source today — a changed `datasync.config.yaml` needs a restart, exactly like a changed
  `appsettings.json` or environment variable does now.
- Anything Linux/systemd-specific. Because this slots in as an ordinary `IConfiguration` source, it works
  identically wherever `datasync` runs — no separate design needed for non-Windows hosting.
- The admin-facing UI for viewing/editing this file — that's its own phase
  (`architecture/implementation/todo/phase-081-admin-config-screen.md`), which depends on this one.

## Open questions to resolve during implementation

- Whether the upward directory walk stops at the user's home directory or genuinely goes to the
  filesystem root — a minor bound, not a design question, and worth picking whatever's simplest to
  implement correctly rather than deciding it here.
- Whether phase 35's config-history UI already covers `datasync.config.yaml` by virtue of being the same
  git repository, or needs its query/diff scope widened — noted above, confirm during implementation.
