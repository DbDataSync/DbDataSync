# Phase 79 — a standardized datasync.config.yaml, and command-shared config resolution

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/standardized-config-file.md` — format (YAML), secrets
handling (gitignored by default), and directory lookup (walk up from cwd, like git) were agreed there.
This doc resolves the remaining questions and lays out the concrete build.

## What this phase will build

**A shared root resolver**, new in `DataSync.Cli`, used by `serve`, `invite`, `health` and `service
install` alike: explicit `--repo` wins outright; otherwise walk up from the current directory (checking
each parent in turn) looking for `datasync.config.yaml`; otherwise fall back to
`CliOptions.DefaultRoot`, unchanged from today. Today each of the four commands does its own
`CliOptions.Read(args, "--repo")` independently and none of them search — this is new plumbing shared
across all four, not a tweak to existing plumbing.

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

**`Url` becomes a real `DataSync:Url` key.** It has no `DataSync:*` key today — `ServeCommand` turns
`--url` straight into ASP.NET Core's own built-in `--urls` (Kestrel's bind address), never into a
`DataSync` setting. `datasync.config.yaml` sets `DataSync:Url`; `ServeCommand` reads it (via the
resolver) the same way it reads `--url` today and does the same translation to `--urls` it already does
— one key, one meaning, everywhere a URL is configured.

**`ServeCommand.Prepare` writes a starter file.** For a genuinely fresh repo root (the `Repository.Init`
branch — never touches an existing one), it also writes a starter `datasync.config.yaml` with the common
keys present but commented out (`# StateEngine: MsSql`, `# StateConnectionString: ...`, `# Url:
http://localhost:5080`) so an admin finds the knob rather than having to know the key names from
`CONFIG.md`, plus a `.gitignore` entry for `datasync.config.yaml` in that same fresh repo — this is the
repo `Prepare` creates and `git init`s, not this development repo, and it's the file that can carry a
connection string.

**`InviteCommand` stops hardcoding SQLite.** It currently does `new StateDatabase(stateDb)`
unconditionally — no branch for `DataSync:StateEngine` being `MsSql` or `Postgres` at all, which means
today, an admin running a non-SQLite state store who locks themselves out cannot use `invite` to recover.
`DataSyncHost.cs` already has the right branch (SQLite-by-path vs. engine+connection-string) for building
a `StateDatabase` from `ApiOptions` — extracted into a small shared factory (e.g. `StateDatabase
.FromOptions(ApiOptions)` in `DataSync.State`) so `DataSyncHost.cs` and `InviteCommand` make this decision
once between them, not twice. `InviteCommand` resolves `ApiOptions`-equivalent values through the shared
resolver/reader above, then calls the same factory.

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
- The concrete regression this phase exists to fix: an integration test that `datasync invite` succeeds
  against a `StateEngine: MsSql`-configured repo (it would throw or silently talk to the wrong store
  today).
- `HealthCommand`: a test that a resolved config `Url` is used with no `--url` passed, and that `--url`
  still wins when both are present.
- `ServeCommand.Prepare`: a test that a fresh repo root gets a starter `datasync.config.yaml` and a
  `.gitignore` entry for it, and that an existing repo root's file is left untouched on a second `serve`.

## Decisions made

- Format YAML, gitignored by default, walk-up-from-cwd lookup — settled in the planning doc.
- `Url` gets its own `DataSync:Url` key rather than letting the file set ASP.NET Core's `urls` key
  directly, so "how to set the URL" stays one concept (`--url` / `DataSync__Url` / `DataSync:Url` in the
  file) instead of two depending on where you're setting it.
- `datasync service install` is explicitly out of scope for changes — it keeps working exactly as it
  does today.
- Fixing `InviteCommand`'s SQLite-only assumption is in scope for this phase, not split out, since the
  shared resolver this phase builds is what makes the fix straightforward (it needs `StateEngine`/
  `StateConnectionString` from the same place `serve` gets them).

## What this phase will not build

- Config import/export — separate, unresolved planning doc (`config-import-export.md`); not this.
- Hot-reload of any setting. `ApiOptions` stays a singleton resolved once at startup, same as every other
  configuration source today — a changed `datasync.config.yaml` needs a restart, exactly like a changed
  `appsettings.json` or environment variable does now.
- Anything Linux/systemd-specific. Because this slots in as an ordinary `IConfiguration` source, it works
  identically wherever `datasync` runs — no separate design needed for non-Windows hosting.

## Open questions to resolve during implementation

- Whether the upward directory walk stops at the user's home directory or genuinely goes to the
  filesystem root — a minor bound, not a design question, and worth picking whatever's simplest to
  implement correctly rather than deciding it here.
