# Phase 112 — one machine-wide data directory per platform

**Status**: Done.
**Plan reference**: `architecture/planning/todo/app-and-service-setup.md`. Related to phase 111
(systemd service registration on Linux), which this phase's own doc originally assumed would land
first — it didn't (see Decisions/Sequencing below).

## What this built

`CliOptions.DefaultRoot` is machine-wide now, not per-user:

| platform | default |
| --- | --- |
| Windows | `%ProgramData%\DbDataSync` (`SpecialFolder.CommonApplicationData`) |
| macOS | `/Library/Application Support/DbDataSync` |
| FreeBSD | `/var/db/dbdatasync` |
| Linux (and any other Unix-like OS) | `/var/lib/dbdatasync` |

`CliOptions.LegacyDefaultRoot` keeps the old per-user answer
(`Environment.SpecialFolder.LocalApplicationData` + `DbDataSync`) around, used only by the migration
check below.

### Root resolution — `DbDataSyncRoot.Resolve`

Now: explicit `--repo` → walk-up for `dbdatasync.config.yaml` (unchanged) → **`DbDataSync__RepoRoot`**
(new) → `CliOptions.DefaultRoot`. The env var is the existing `DbDataSync:RepoRoot` config key's
environment-variable form, read directly here (the same reason `ServeCommand` already reads
`DbDataSync__Url` and `InviteCommand` reads `DbDataSync__StateEngine` directly — these commands
resolve a root before the raw API's own configuration chain exists to do it for them).

### Migration detection — `src/DbDataSync.Cli/LegacyRootMigration.cs` (new)

`DetectAt(resolvedRoot)` (and a three-argument overload taking the default/legacy paths explicitly,
for testing without touching the real OS-defined locations — the same reasoning
`DbDataSyncRoot.Resolve(args, startDirectory)` already takes an explicit start directory for) is null
unless: resolution fell all the way through to `CliOptions.DefaultRoot` (no `--repo`, no walk-up hit,
no env var — an operator who set any of those has already made an explicit choice), the new default
has no real configuration yet, and the old per-user location does. Wired into three call sites, each
treating a hit differently:

- **`ServeCommand.RunAsync`** — prints the notice and **refuses to start** (exit 1) rather than
  silently bootstrapping an empty second repo next to a working one. This is a deliberate reading of
  the phase doc's "print, do not act" — see Decisions.
- **`SetupCommand`**'s Step 0 — the "Config folder" prompt defaults to the legacy path instead of the
  platform default when one is found (with the notice printed first), so pressing Enter reviews the
  existing configuration in place; typing something else starts fresh at the new location
  deliberately.
- **`ConfigCommand.CheckAsync`** — printed as an informational line before the check list, in the
  human-readable path only (never mixed into `--json` output, which a CI reader parses as JSON).

### Windows service — `icacls` grant

`ServiceCommand.Install` now creates the data directory and grants a named (non-`LocalSystem`)
`--account` `Modify` access to it via `icacls`, the same "issuing and granting are one operation"
principle phase 82 already applied to a certificate's private key. Needed now that the directory is
machine-wide (`%ProgramData%`) rather than the account's own profile.

### Docker image and docs

- `Dockerfile` — `ENV DBDATASYNC_HOME=/var/lib/dbdatasync` (never read by anything) → `ENV
  DbDataSync__RepoRoot=/var/lib/dbdatasync` (now genuinely honoured by the resolver above); the
  redundant `CMD ["--repo", "/var/lib/dbdatasync"]` removed. Verified by building the image and
  running it: `serve` resolved `/var/lib/dbdatasync` from the env var alone, `/api/health` returned
  200, `FIRST-RUN.txt` was written, and `dbdatasync health` inside the container exited 0.
- `CONFIG.md` — "Repo root resolution" section rewritten for the new four-step chain and the
  platform table; the container-image section's stale `DBDATASYNC_HOME`/`CMD` note replaced; the
  `service install` flag table's default corrected.

### Tests

- **`CliOptionsTests`** (new) — `DefaultRoot` matches the expected path for the running platform and
  is not under the user profile; `LegacyDefaultRoot` is still the old per-user answer.
- **`DbDataSyncRootTests`** — two new tests: `DbDataSync__RepoRoot` beats `DefaultRoot` but loses to
  `--repo` and to a walk-up hit; unset behaves exactly as before this phase.
- **`LegacyRootMigrationTests`** (new) — every branch of `DetectAt`'s logic (not fired when the
  resolved root isn't the default, when the default already has a configuration, when the legacy
  location has none, fired when the default is empty and the legacy location has a real one), plus
  `Message` naming both paths. All driven through the three-argument overload against fake temp
  directories — never the real OS-defined default/legacy paths, which would be unsafe to create,
  populate, or delete in a shared environment.
- Manual: a full Docker build and run (above) proving the image-level behavior end to end.

## Decisions

- **Sequencing: phase 111 (systemd service) had not landed when this phase was implemented**, despite
  the original doc assuming it would (`ServiceCommand`'s Linux `install` "already creates the
  directory and chowns it," referenced in the doc's §2 and §4). Nothing here depends on that: the
  Linux/FreeBSD first-run permission story is a plain `Directory.CreateDirectory` failing with a clear
  message where it already would have (via `ServeCommand.Prepare`'s existing exception handling) — no
  systemd-specific directory creation was invented to fill the gap, since phase 111's own `install`
  is exactly where that belongs. Phase 111, when it lands, should not add the "serve run interactively
  is unchanged" carve-out its planning doc once assumed — this phase already removed the need for one.
- **`serve` refuses to start rather than "print, do not act" while continuing.** The phase doc's own
  example message says *"or keep it where it is: `dbdatasync serve --repo ...`"* — advice that only
  makes sense if the current invocation (without `--repo`) does **not** proceed to create a second,
  empty configuration at the new default. Read literally, "do not act" scopes only to "do not
  auto-move the folder," but silently bootstrapping a duplicate repo right after warning about an
  existing one elsewhere is the more confusing outcome, not the safer one. `config check` and
  `setup`'s Step 0 stay informational/corrective respectively, since neither of them writes anything
  irreversible on its own.
- **No macOS `sudo mkdir`/`chown` elevation was built**, despite the phase doc's §2 macOS bullet
  describing one. Shelling to `sudo` interactively for a first-run directory is real, privileged,
  platform-specific behavior this sandbox has no way to exercise or verify — even minimally — the same
  reasoning phase 110 applied to Windows-only cert/service steps (print the exact command, don't
  invoke it blind). `ServeCommand.Prepare`'s existing exception handling already produces a clear
  permission-denied message on any platform; a `setup` step that shells to `sudo` on the operator's
  behalf is left for whenever there's a real macOS environment to validate it against.
- **No `ServiceCommandTests.cs` was added for the `icacls` grant.** `ServiceCommand.Run` gates its
  entire body behind `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`, so nothing under it — the
  new grant included — is reachable in this Linux sandbox without bypassing that gate directly; doing
  so would still crash on `Process.Start("icacls")`, which does not exist here. Consistent with this
  repo's existing pattern for Windows-only code with no Windows CI runner backing it (see the several
  `[Trait]`-skipped `AdminCertificateServiceWindowsTests` in `DbDataSync.Api.Tests`): implemented and
  reasoned through, not unit-tested, with the container smoke test above as the actual end-to-end
  proof for everything Linux-side.

## What this phase does not build

- Moving anyone's data — the tooling detects and instructs; the operator moves.
- `ApiOptions`' own raw default (`<cwd>/dbdatasync-repo`) — the "ran the API project directly" dev
  path, not a distribution concern.
- A `dbdatasync migrate` command.
- Per-OS branches for Solaris/illumos, NetBSD, OpenBSD — `DbDataSync__RepoRoot` is their answer.
- The macOS `sudo` elevation and the Linux/FreeBSD systemd-integrated directory creation described in
  the original doc — see Decisions above.

## How it was verified

- `dotnet build DbDataSync.slnx` clean.
- Full `dotnet test --filter "Category!=Integration"` green solution-wide (`DbDataSync.Cli.Tests`: 57,
  up from 47 before this phase).
- Full `dotnet test --filter "Category=Integration"` green solution-wide.
- A real `docker build` + `docker run` of the updated `Dockerfile`, confirming `serve` resolves
  `/var/lib/dbdatasync` from `DbDataSync__RepoRoot` alone (no `--repo`), `/api/health` answers 200,
  and `FIRST-RUN.txt` is written — the image-level behavior this phase changed, proven rather than
  assumed.

## Open questions — resolved or deferred

1. **Windows interactive first-run ownership** — left as the doc's own leaning (Modify is enough;
   `install` does not additionally take ownership).
2. **macOS creation UX** — deferred; see Decisions (no `sudo` elevation built).
3. **FreeBSD `/var/db` vs `/usr/local/var`** — resolved as leaned: `/var/db/dbdatasync`.
4. **The migration check's cost** — one `Directory.Exists`-shaped check (via `ExistingSetup.DetectedAt`)
   only on the path where resolution already fell through to the bare default; not gated further, per
   the doc's own "negligible" assessment.
