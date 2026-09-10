# Phase 123 — `dbdatasync tool install` / `tool uninstall`, and machine-wide install docs (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/machine-wide-dotnet-tool-install.md`. Builds on
phase 112 (`CliOptions.DefaultRoot`, the machine-wide *data* directory this mirrors for the
*binary*), phase 111 (`SystemdService`), phase 110 (`SetupCommand`). Independent of the 034/035/038
queue and the phase-109g–109i series.

## Why

`dbdatasync` ships as a .NET global tool. `dotnet tool install --global` puts it in the installing
user's profile (`~/.dotnet/tools`, `%USERPROFILE%\.dotnet\tools`) — the wrong place for a service
deployment:

- `dbdatasync service install` bakes `Environment.ProcessPath` into the unit, so a service registered
  from an admin's profile points there forever — broken by a profile cleanup or a re-register from
  another account.
- It is **actively broken today** in one combination: `SystemdService.RenderUnit` sets
  `ProtectHome=yes` on a hardened unit, and systemd then hides an `ExecStart` under `/home` or
  `/root` (where `sudo dotnet tool install -g` puts it) — the service installs cleanly and fails on
  first start.
- Nobody but the installing user gets `dbdatasync` on `PATH`.

`dotnet tool install --tool-path <DIR>` installs to an arbitrary directory but does nothing about
`PATH` or permissions. This phase supplies a directory convention, a command that does the `PATH`
wiring, and the docs — the binary analog of phase 112.

## What this phase builds

### `CliOptions.DefaultToolDir` — `src/DbDataSync.Cli/CliOptions.cs`

A `static string` beside `DefaultRoot`, same `OperatingSystem.Is*` branching:

| platform | `DefaultToolDir` | (`DefaultRoot`, phase 112) |
| --- | --- | --- |
| Windows | `%ProgramFiles%\DbDataSync` (`Environment.SpecialFolder.ProgramFiles`) | `%ProgramData%\DbDataSync` |
| macOS | `/usr/local/dbdatasync` | `/Library/Application Support/DbDataSync` |
| FreeBSD / other Unix | `/usr/local/dbdatasync` | `/var/db/dbdatasync` / `/var/lib/dbdatasync` |
| Linux | `/opt/dbdatasync` | `/var/lib/dbdatasync` |

`/opt` and `%ProgramFiles%` for the binary; `/var/lib` and `%ProgramData%` for the data — the
idiomatic split on each platform. Not read by `serve`/`service` (those still resolve the executable
from `Environment.ProcessPath`). Consumed by `ToolCommand`, `ServiceCommand`/`SystemdService`, and
`SetupCommand`, and mirrored literally in `docs/` (a test asserts the doc text contains its value).

### `dbdatasync tool install` / `tool uninstall` — `src/DbDataSync.Cli/ToolCommand.cs` (new)

Dispatched from `Program.cs` as a nested group, exactly like `service`:

```csharp
"tool" => ToolCommand.Run(rest),
```
```
dbdatasync tool install   [--dir <path>]
dbdatasync tool uninstall [--dir <path>]
```

- **Directory inference.** `toolDir = Path.GetDirectoryName(Environment.ProcessPath)`, overridable
  with `--dir`. Warn (not refuse) if `toolDir` is under the user profile
  (`Environment.GetFolderPath(SpecialFolder.UserProfile)` / `$HOME`): "this looks like a per-user
  install; the docs point machine-wide installs at `<DefaultToolDir>` — see `docs/install.md`."
- **`tool install`:**
  - **Linux / macOS / other Unix — needs root.** `chmod -R a+rX <toolDir>` (defensive: root's `022`
    umask already yields `0755`; this covers a restrictive umask). Then `ln -sfn
    <toolDir>/dbdatasync /usr/local/bin/dbdatasync` (Linux/other) or write `/etc/paths.d/dbdatasync`
    containing `<toolDir>` (macOS). Not root → print `sudo <toolDir>/dbdatasync tool install`,
    exit 1.
  - **Windows — needs elevation.** Append `<toolDir>` to the **Machine** `Path` via
    `Environment.SetEnvironmentVariable("Path", …, EnvironmentVariableTarget.Machine)` (never
    `setx` — 1024-char truncation), guarded against a duplicate entry; then broadcast
    `WM_SETTINGCHANGE` (`SendMessageTimeout(HWND_BROADCAST, …)` via P/Invoke) so new processes pick
    it up. `icacls` grant only if a non-`Users` service account is later configured — reuse the
    exact shape of `ServiceCommand.GrantDataDirectoryAccess`. Not elevated → print the elevated
    command, exit 1.
  - Reports what changed and what's next (open a new terminal on Windows; `dbdatasync config check`
    everywhere).
- **`tool uninstall`:** removes exactly what `tool install` wrote — the `/usr/local/bin` symlink,
  the Machine `PATH` entry, or the `/etc/paths.d` file. **Leaves the `--tool-path` payload alone**
  and prints `dotnet tool uninstall --tool-path <toolDir> DbDataSync` to finish removal. Same
  elevation rules and same "not elevated → print the command, exit 1" behavior.
- **Both idempotent.** `tool install` twice → "already linked" / "already on PATH", changes
  nothing, exit 0. `tool uninstall` with nothing to remove → says so, exit 0.
- **Test seam.** An `IToolPathEnvironment` mirroring phase 111's `ISystemdEnvironment` —
  `IsElevated`, `CreateSymlink`/`RemoveSymlink`, `ReadMachinePath`/`WriteMachinePath`, `Chmod`,
  `WritePathsD`/`DeletePathsD` — so `ToolCommandTests` proves what each path *would* do without
  writing to the real `/usr/local/bin`, the real Machine `PATH`, or `/etc/paths.d`.
  `RealToolPathEnvironment` is the production implementation. Any pure formatting (the deduped
  `PATH` string, the symlink target) is a testable function, the way `SystemdService.RenderUnit` is.

### `service install` awareness — `src/DbDataSync.Cli/ServiceCommand.cs`, `SystemdService.cs`

- After `executable = Environment.ProcessPath`, test whether it is under the user profile
  (`SpecialFolder.UserProfile` on Windows; `$HOME` / `/home/` / `/root/` on Linux).
- **Windows, or Linux with an un-hardened unit (`--repo` outside the managed root):** a warning
  block — "this tool is installed in a user profile; a service pointing here breaks if that profile
  is removed or you re-register from another account. Install machine-wide first:" then the two
  lines from `docs/install.md`, built from `DefaultToolDir`. Continue.
- **Linux with a hardened unit (`--repo` is the managed root → `RenderUnit` emits
  `ProtectHome=yes`):** a **hard error** — the service will not start. Refuse, print the fix, exit
  non-zero.
- **`SystemdService.RenderUnit` gains `Environment=DOTNET_ROOT=<dir>`** in `[Service]`, where
  `<dir>` is the running install-time process's own .NET root — `DOTNET_ROOT` from the environment
  if set, otherwise derived from `RuntimeEnvironment.GetRuntimeDirectory()` walked up out of
  `shared/Microsoft.NETCore.App/<ver>/`. Scoped to this unit; no machine-global side effect. Fixes
  the case where `dotnet` was installed by `dotnet-install.sh` into a user's `~/.dotnet` and so is
  invisible to a `nologin` service account. `RenderUnit` stays pure and its unit test asserts the
  new line.
- `service install`'s output gains a "next: `dbdatasync config check`" line.

### `config check` — `src/DbDataSync.Cli/ReadinessChecks.cs`

One added check: **.NET runtime discoverable machine-wide** — `DOTNET_ROOT` set, or
`/etc/dotnet/install_location` present (Linux), or a runtime at a well-known path. A failure names
the fix (install .NET via the package manager, or set `DOTNET_ROOT` system-wide). Green on the
common case; only speaks up where a service would later fail with "You must install the .NET
runtime".

### `SetupCommand` step 6 — `src/DbDataSync.Cli/SetupCommand.cs`

When step 6's service prompt is accepted **and** `Environment.ProcessPath` is under the user
profile, print the two-line machine-wide `tool install` block (from `DefaultToolDir`) above the
existing `service install` line — consistent with the step's existing "print the command, don't
elevate mid-session" behavior.

### `tools/install-local-tool` + `tools/install-local-tool.cmd` (new)

A dev-loop helper in the `tools/dev-harness` mould — an `sh` script plus a `.cmd`, thin wrappers
over `dotnet`.

- **Pack** `src/DbDataSync.Cli` `-c Release -o "<repo>/bin/local-tool-feed"
  -p:Version=0.1.0-local.<timestamp>` (git-ignored — `bin/` already is). The timestamped version is
  what makes `dotnet tool update` see each build as new. Runs the real SPA build
  (`DbDataSync.Api`'s `BuildSpa` target — node required, first pack slow); a `--no-spa` flag adds
  `-p:SkipWebBuild=true`.
- **Install / update** `dotnet tool update --add-source "<repo>/bin/local-tool-feed" --version
  0.1.0-local.<timestamp> DbDataSync` at one of:
  - default **`--global`** — on the developer's `PATH` immediately, no elevation;
  - **`--machine-wide`** — `--tool-path <DefaultToolDir>` (elevated / `sudo`), then run
    `<DefaultToolDir>/dbdatasync tool install`;
  - **`--tool-path <dir>`** — passed straight through.
- **`--uninstall`** reverses whichever target the flags select (`dotnet tool uninstall`, plus
  `dbdatasync tool uninstall` first for `--machine-wide`).
- Packs every run — no staleness check of its own, the choice `tools/dev-harness` documents.

### Docs

- **`docs/install.md`** (new) — copy-paste blocks, one heading per platform:

  ```sh
  # Linux — install
  sudo dotnet tool install --tool-path /opt/dbdatasync DbDataSync
  sudo /opt/dbdatasync/dbdatasync tool install
  # Linux — update
  sudo dotnet tool update --tool-path /opt/dbdatasync DbDataSync
  ```
  ```powershell
  # Windows (elevated PowerShell) — install
  dotnet tool install --tool-path "$env:ProgramFiles\DbDataSync" DbDataSync
  & "$env:ProgramFiles\DbDataSync\dbdatasync.exe" tool install
  # Windows — update
  dotnet tool update --tool-path "$env:ProgramFiles\DbDataSync" DbDataSync
  ```

  Plus short notes: the SDK is needed to install/update, only the runtime to run; `--source <feed>`
  for air-gapped; `update` never re-runs `tool install` (the shim regenerates in place, the symlink
  / `PATH` entry point at the directory); which contexts see `dbdatasync` on `PATH` (login shells,
  the service via its absolute `ExecStart`, Windows tasks) vs. which need the absolute path (`cron`).
- **`docs/getting-started.md`** — if it does not exist yet, add its "Windows service" / "Linux
  service" sections here: machine-wide `tool install` → `dbdatasync service install` →
  `dbdatasync config check`. If `docs/getting-started.md` is still unwritten, `docs/install.md`
  stands alone and `getting-started.md` links it when it lands.
- **`src/DbDataSync.Cli/README.md`** — currently says `dotnet tool install -g DbDataSync` and a
  pre-phase-112 `%LOCALAPPDATA%` path. Add the machine-wide sequence and fix the stale path.
- **`Help.cs`** — a `dbdatasync tool install|uninstall` entry.

## How to verify when built

- `dotnet build` / `dotnet test --filter "Category!=Integration"` green.
- New `tests/DbDataSync.Cli.Tests/ToolCommandTests.cs` (driven through `IToolPathEnvironment`):
  - Linux `tool install` not-root → prints `sudo … tool install`, exit 1, nothing written.
  - Linux `tool install` as root → symlink `/usr/local/bin/dbdatasync` → `<dir>/dbdatasync`,
    `chmod a+rX`; second run is a no-op; `tool uninstall` removes the symlink and prints the
    `dotnet tool uninstall --tool-path` line.
  - Windows `tool install` → the Machine `PATH` gains `<dir>` once (not twice on re-run);
    `tool uninstall` removes exactly that entry, leaving the rest of `PATH` byte-for-byte.
  - `--dir` overrides inference; a user-profile `toolDir` warns but proceeds.
- `SystemdServiceTests` — `RenderUnit` now emits `Environment=DOTNET_ROOT=…`; a unit whose
  executable is under `/home` with the hardened profile is refused by `service install`.
- `ReadinessChecksTests` — the runtime-discoverability check passes on the dev/CI box and its
  failure message names the fix.
- `SetupCommandTests` — step 6 with a user-profile `ProcessPath` prints the `tool install` block.
- **Manual, real hosts:**
  - Ubuntu 24.04: `dotnet tool install --tool-path /opt/dbdatasync DbDataSync` (via a local feed),
    `sudo dbdatasync tool install`, `dbdatasync --help` as a second user, `sudo dbdatasync service
    install` + `systemctl start` + reach the console. Confirm SELinux is not a factor here and on
    a stock RHEL 9 (enforcing) — if `/opt` exec by the service user is denied, add `restorecon
    <toolDir>` to `tool install` (Open questions).
  - Windows Server: elevated `dotnet tool install --tool-path "$env:ProgramFiles\DbDataSync"`,
    `dbdatasync tool install`, new terminal sees `dbdatasync`, `dbdatasync service install` +
    start.
  - `dotnet tool update` on both → the service keeps running against the regenerated shim with no
    re-run of `tool install`.

## What this phase does not build

- **A native OS installer** (`.msi`, `.deb`, `.rpm`, Homebrew) — `phase-051` territory, removes the
  SDK dependency entirely, much larger.
- **Package acquisition or self-update.** `dotnet tool install|update --tool-path` does all of it;
  `tool install` never downloads anything.
- **The container** — it runs an absolute `dotnet /app/...` and has no `PATH` question.
- **A shared multi-tool directory** as the default — `--dir` allows one; DbDataSync manages only
  its own entry.
- **Changing `serve`/`service` executable resolution** — still `Environment.ProcessPath`; this only
  changes what that path is and whether a person can also type `dbdatasync`.

## Open questions to resolve during implementation

1. **SELinux / AppArmor** for executing `<DefaultToolDir>/dbdatasync` as a `nologin` service user.
   `/opt` service execs are normally fine; verify on stock RHEL 9 (enforcing) and Ubuntu 24.04. If
   denied, `tool install` runs `restorecon` (Linux, when available) and the doc carries the manual
   `chcon` line.
2. **Exact `DOTNET_ROOT` derivation.** `DOTNET_ROOT`/`DOTNET_ROOT(x86)` from the environment first;
   otherwise walk up from `RuntimeEnvironment.GetRuntimeDirectory()`. Confirm the walk-up is stable
   across the layouts .NET 10 actually ships (`/usr/share/dotnet`, `/usr/lib64/dotnet`, a
   `dotnet-install.sh` dir) before relying on it; fall back to omitting the line rather than
   emitting a wrong one.
3. **`docs/` location.** `docs/install.md` standalone now, folded into `docs/getting-started.md`
   later — or write `getting-started.md`'s skeleton here. Depends on whether that doc has landed by
   implementation time.
4. **`tools/install-local-tool` default target** — `--global` (leaning, zero friction for CLI
   iteration) vs. a repo-local `--tool-path`. Confirm `--global` doesn't surprise a developer who
   expected an isolated install.
