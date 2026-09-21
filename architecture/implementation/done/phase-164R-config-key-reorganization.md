# Phase 164 — reorganizing `DbDataSync:*` config keys into groups, replacing bare booleans with named modes

**Status: built and verified, 2026-09-21.**

## Why

Audited directly against the real code (see `architecture/planning/todo/cli-setup-and-api-parity.md` for
the related writability gap): the `DbDataSync:*` key surface grew flat and inconsistent — `Auth:Passkeys:*`
is well-grouped, but `StateEngine`/`StateConnectionString`/`StatePort`/`StateDbPath`, every retention
setting, and `Url`/`RepoRoot`/`TaskRunnerDllPath`/`CliDllPath` all sit directly under `DbDataSync:` with no
grouping at all. Separately, several settings are bare booleans (`NotesRichMarkdown`, `NuGetSearchEnabled`,
`SelfUpdateEnabled`, `Auth:Disabled`) that don't self-document and don't leave room to grow past two
states. This phase regroups the whole surface and replaces every bare boolean with a named mode string,
so the Admin config screen and `config get/set --help`-shaped output can group and describe keys
consistently going forward.

Two real behavior bugs surfaced while designing this (not cosmetic — see "Bugs fixed along the way").

## New key shape

| Old | New |
|---|---|
| `DbDataSync:Url` | `DbDataSync:App:Url` |
| *(none — see below)* | `DbDataSync:App:AlternateUrls` (new) |
| `DbDataSync:RepoRoot` | `DbDataSync:App:RepoRoot` |
| `DbDataSync:TaskRunnerDllPath` | `DbDataSync:App:TaskRunnerDllPath` |
| `DbDataSync:CliDllPath` | `DbDataSync:App:CliDllPath` |
| `DbDataSync:StateEngine` | `DbDataSync:State:Engine` |
| `DbDataSync:StateConnectionString` | `DbDataSync:State:ConnectionString` |
| `DbDataSync:StatePort` | `DbDataSync:State:Port` |
| `DbDataSync:StateDbPath` | `DbDataSync:State:DbPath` |
| `DbDataSync:RunRetentionDays` | `DbDataSync:State:Retention:RunDays` |
| `DbDataSync:RunRetentionMaxPerMapping` | `DbDataSync:State:Retention:RunMaxPerMapping` |
| `DbDataSync:RunPruningIntervalMinutes` | `DbDataSync:State:Retention:PruningIntervalMinutes` |
| `DbDataSync:ChangeCheckRetentionDays` | `DbDataSync:State:Retention:ChangeCheckDays` |
| `DbDataSync:Auth:Disabled` (bool) | `DbDataSync:Auth:Network:Admin` = `loopback \| disabled` |
| *(none — see below)* | `DbDataSync:Auth:Network:Viewer` = `remote \| loopback \| disabled` (new) |
| *(none — inferred from group names)* | `DbDataSync:Auth:Windows:Mode` = `enabled \| disabled` (new, explicit) |
| `DbDataSync:Auth:AdminGroup` | `DbDataSync:Auth:Windows:AdminGroup` |
| `DbDataSync:Auth:ViewerGroup` | `DbDataSync:Auth:Windows:ViewerGroup` |
| *(none — always active)* | `DbDataSync:Auth:Passkeys:Mode` = `enabled \| disabled` (new, explicit) |
| `DbDataSync:Auth:Passkeys:RelyingPartyId` | unchanged — stays singular, see "What this does not do" |
| `DbDataSync:Auth:Passkeys:RelyingPartyName` | unchanged |
| `DbDataSync:Auth:Passkeys:Origins` (array) | **removed** — replaced by the always-implicit `App:Url` origin plus `App:AlternateUrls` |
| `DbDataSync:NotesRichMarkdown` (bool) | `DbDataSync:Notes:MarkdownRenderer` = `basic \| rich` |
| `DbDataSync:SelfUpdateEnabled` (bool) | `DbDataSync:Updates:Mode` = `manual \| disabled` (room for a future `auto`) |
| `DbDataSync:SelfUpdateChannels` | `DbDataSync:Updates:Channels` |
| `DbDataSync:SelfUpdateDrainTimeoutSeconds` | `DbDataSync:Updates:DrainTimeoutSeconds` |
| `DbDataSync:SelfUpdateConfirmAfterSeconds` | `DbDataSync:Updates:ConfirmAfterSeconds` |
| `DbDataSync:NuGetSearchEnabled` (bool) | `DbDataSync:Nuget:Search:Mode` = `enabled \| disabled` |
| `DbDataSync:Certificates:*` | unchanged — already well-grouped |

Every new mode-style value is lowercase, matching the existing `SelfUpdateChannels`/`ReleaseChannel`
convention (`stable`/`beta`/`snapshot`).

## Bugs fixed along the way

Both found while checking whether this reorg was even safe, not invented for it — see the conversation
this phase came out of for the full trace.

1. **`service install` bakes `--url` into the persisted service unit, permanently overriding the config
   file.** `serve` already resolves `Url` with the right precedence (flag → env var → file,
   `ServeCommand.cs:51`), but `ServiceCommand.Install`/`SystemdService.Install` hardcode a `--url` flag
   into `ExecStart`/`binPath` at install time and never touch `dbdatasync.config.yaml`. Once installed,
   editing `App:Url` through the Admin screen is silently ignored on every restart — the install-time
   value always wins. **Fix**: stop baking `--url` into the persisted command line; seed
   `dbdatasync.config.yaml`'s `App:Url` once at install time if the file has none yet (mirroring what
   `setup`'s General tab already does), then let `serve`'s existing precedence chain do the rest.
2. **`Auth:Passkeys:Origins` is a one-time copy of `Url`, not a live relationship, and its unset default
   is hardcoded to localhost regardless of the real `Url`.** `SetupSteps.ApplyAuthentication` copies `url`
   into `Origins` only at the moment Authentication is saved (`SetupSteps.cs:110`); change `Url` later and
   `Origins` goes stale, breaking passkeys against `PasskeyOptions.Problem()`'s own mismatch check. If
   `Origins` was never set at all, `PasskeyOptions.FromConfiguration` falls back to a hardcoded
   `localhost` list with no relationship to the configured `Url`. **Fix**: `App:Url`'s own origin is
   always implicitly trusted at runtime (computed, never stored); `App:AlternateUrls` is a purely additive
   list for any origin besides the primary. `PasskeyOptions.Problem()`'s domain-match check extends to
   cover the implicit primary origin, not just the list.

## Auth:Network — design and where it plugs in

Replaces the single global `Auth:Disabled` (which today grants **Admin, from any network**, to every
unauthenticated request — `SessionAuthenticationHandler.cs:36`) with two independent, role-scoped,
network-trust fallbacks:

- `Auth:Network:Admin`: `disabled` (default) or `loopback` — an unauthenticated request whose remote
  address is loopback is granted Admin.
- `Auth:Network:Viewer`: `disabled` (default), `loopback`, or `remote` — `remote` trusts any origin as
  Viewer; `loopback` restricts that to loopback only.

Deliberately narrower than today's `Disabled`: there is no "Admin from anywhere" option at all. Loopback
trust checks the connection's remote address the same way `RunnerStateGuard` already does for its own
unrelated loopback check (`IPAddress.IsLoopback`) — plain loopback, no extra token, matching what today's
`Auth:Disabled` already permits (and narrowing it, since today's flag has no loopback restriction
whatsoever).

**Fallback order in `SessionAuthenticationHandler.HandleAuthenticateAsync`**: a real session cookie still
wins first if present and valid (unchanged). Only when there is no session at all does the network-trust
check run: Admin trust checked first (admin wins over viewer, matching `WindowsSignIn`'s own "both
checked, admin wins" precedent), then Viewer trust. This lets `Auth:Network:*` act as a fallback alongside
Windows/Passkeys auth being configured, not only as a replacement for it — e.g. Windows group auth
configured for remote users, plus `Auth:Network:Viewer: loopback` so a local health check or admin at the
console itself doesn't need to negotiate Windows auth just to view.

## `Auth:Windows:Mode` / `Auth:Passkeys:Mode`

Both new, explicit, `enabled | disabled`. Windows auth today is inferred purely from `AdminGroup`/
`ViewerGroup` being non-empty; Passkeys has no toggle at all (always active). Explicit modes let an
operator configure a group name or relying-party id and still turn the method off without clearing it —
and keep the "no bare booleans" rule consistent with `Updates:Mode`/`Nuget:Search:Mode`.

## What this does not do

- **No relying-party id migration support.** `Auth:Passkeys:RelyingPartyId`/`RelyingPartyName` stay
  exactly as they are today, on purpose — see
  `architecture/planning/todo/passkey-relying-party-migration.md` for why a config-only change can't
  actually solve that, and what real support would need (per-credential relying-party tracking in
  `UserCredentials`, a genuinely separate and heavier piece of work).
- **No change to `Kestrel:Certificates:Default:AllowInvalid`** (also a bare boolean) — it lives in the
  ASP.NET-standard `Kestrel:*` namespace, not `DbDataSync:*`, and isn't in the Admin catalog.
- **Does not flip every currently-unwritable key to writable** — only the ones this reorg's rename already
  touches (`Auth:Windows:*`, `Auth:Passkeys:Mode`/`RelyingPartyId`/`RelyingPartyName`). Confirmed safe to
  do while renaming: `DbDataSyncConfigFile.SetValue`'s text-editing writer tolerates a colon-containing
  plain-scalar key/section (verified with a throwaway round-trip test), which is exactly the mechanism
  `SetupSteps.cs` already relies on for the TUI path — the phase 79/81 docs' "only flat top-level keys are
  file-writable" claim was never actually true for the writer, only for what `AdminConfigService`'s
  catalog chose to expose.

## Migration for existing installs

`dbdatasync.config.yaml` is the common case and the one this phase heals automatically: a new
`LegacyConfigMigration` step (in `DbDataSync.Core.Config`, called from `ServeCommand`/`SetupCommand`
startup, same place `WriteStarter` already runs from) detects any old-shaped key still present in the
file, rewrites each to its new location via the existing `SetValue`/`RemoveValue` primitives, and commits
the result with `GitCommitService` — the same one-line-per-change history every other config edit already
produces. Boolean-to-mode keys translate their value (`NotesRichMarkdown: true` → `Notes:MarkdownRenderer:
rich`, `SelfUpdateEnabled: false` → `Updates:Mode: disabled`, `Auth:Disabled: true` → `Auth:Network:Admin:
loopback` — the closest equivalent, though deliberately narrower than the old flag's "admin from anywhere,"
named in the migration's own commit message so an operator relying on remote unauthenticated admin access
notices the narrowing rather than being silently locked out).

Environment variables and CLI flags using old key names are **not** migrated (nothing to rewrite) — this
phase does not add dual-read support for those, since the file is the common, persistent case and the
other two are typically set per-invocation rather than left stale for years.

## Files expected to change

Core options/catalog: `ApiOptions.cs`, `AuthOptions.cs`, `PasskeyOptions.cs`, `AdminConfigService.cs`,
`ConfigValueCommand.cs`, a new `LegacyConfigMigration.cs`. Auth pipeline:
`SessionAuthenticationHandler.cs`. CLI/TUI: `SetupSteps.cs`, `AuthenticationTab.cs`, `GeneralTab.cs`,
`StateDatabaseTab.cs`, `ServiceCommand.cs`, `SystemdService.cs`, `ServeCommand.cs`, `HealthCommand.cs`,
`InviteCommand.cs`, `ReadinessChecks.cs`. Docs: `docs/configuration.md`. Tests: every test referencing an
old flat key name (broad — a full-repo grep after the rename, relying on the compiler and a full test run
to catch what a grep misses).

## Verified

Full solution build (`dotnet build DbDataSync.slnx`) clean, 0 warnings, 0 errors. Full test suite
(`dotnet test DbDataSync.slnx --filter "Category!=Integration"`) green: 2,208 passed, 0 failed, 43
skipped (Windows-only tests, expected on this Linux dev box), across all 18 test projects. SPA
(`npm run build`) and its unit suite (`npx vitest run`, 76 passed) also clean.

Two real, independent bugs surfaced while verifying the rename — both fixed and covered by new
regression tests, not just the reorg's own key renames:

1. **`ServeCommand.BuildHostArgs` still passed the pre-reorg flat `--DbDataSync:RepoRoot`/
   `--DbDataSync:StateDbPath` after every other key had moved** — nothing in the suite caught it because
   nothing calls `ServeCommand.RunAsync` (it actually starts listening) and the Playwright suite launches
   `DbDataSync.Api.dll` directly rather than through `dbdatasync serve`. Found only by writing a new test
   for it (`ServeCommandHostArgsTests`).
2. **That same test found a second, genuinely pre-existing bug, unrelated to this phase**: stripping a
   CLI-only flag (`--repo`/`--state-db`/`--url`) without also stripping its separate value token leaves
   an orphan token that desyncs .NET's command-line parser for every `--DbDataSync:*` pair appended after
   it, for an *odd* count of such orphans — verified directly against the real
   `CommandLineConfigurationProvider`. `dbdatasync serve --repo X` (no `--url`) left exactly one orphan
   and would have silently dropped every `--DbDataSync:*` argument appended after it — `App:RepoRoot`
   and `State:DbPath` included, a deployment silently running against the wrong repo. Fixed in the same
   pass (`BuildHostArgs` now strips the value token too) and covered by
   `EveryRealisticFlagCombination_StillResolvesTheRepoRootAndStateDbPath`.

**Not verified**: the Playwright E2E suite (`tests/DbDataSync.Web.Tests`) was updated (key names, testids,
the caution-count assertion) but not run in this pass — it needs a real SQL Server/Postgres via
docker-compose and downloaded browser binaries neither available in this session. `dotnet-integration`
(Integration-tagged tests) likewise not run here for the same reason. Both should run in CI before this
merges.

## Release plan

Once built and green, this ships as the next stable release (no snapshot/beta staging needed — see the
`nuget-release` skill).
