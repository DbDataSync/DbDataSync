# A machine-wide install location for the `dbdatasync` tool

**Resolved 2026-09-09.** The design is agreed. It builds as a single phase —
`architecture/implementation/todo/phase-123-machine-wide-tool-install.md`: `CliOptions.DefaultToolDir`,
a nested `dbdatasync tool install` / `tool uninstall` command that infers its own directory and wires
`PATH` + permissions, `service install` awareness (a warning, and a hard error in the hardened-Linux
+ home-directory case) with `DOTNET_ROOT` baked into the systemd unit, a `config check` runtime probe,
a `SetupCommand` step-6 hint, a `tools/install-local-tool` dev-loop script, and `docs/install.md`.

The rest of this document is the thinking that produced that plan — kept for the rationale and the
walked-through decisions.

---

Scope: where the `dbdatasync` executable lives on a Windows or Linux host so that a service account
**and** every interactive user can run it — the binary analog of phase 112, which moved the *data*
directory from a per-user profile to one machine-wide location per platform. A new `dbdatasync
install` command that wires an already-restored `--tool-path` install into the system, plus
copy-paste install/update docs per platform.

Out of scope: the container image (it runs `dotnet /app/DbDataSync.Cli.dll` directly — no tool
install), and a real OS package (`.deb`/`.msi`/`.pkg`) — a bigger, separate question. Also **not**
a self-copy / self-updating mechanism — `dotnet tool install|update --tool-path` does all package
acquisition; this doc only adds what that command leaves undone.

Cross-references `architecture/planning/todo/app-and-service-setup.md` (its *Acquisition* bullet and
its step 6, "Windows only — service", should point here once this lands).

---

## The problem

`dbdatasync` ships as a .NET global tool (`PackAsTool=true`, `ToolCommandName=dbdatasync`). The
documented install is `dotnet tool install --global DbDataSync`, which puts the executable in the
**current user's** profile:

| platform | global-tool location |
| --- | --- |
| Linux / macOS | `~/.dotnet/tools` |
| Windows | `%USERPROFILE%\.dotnet\tools` |

That is fine for evaluating locally. It is the wrong place for a service deployment, for the same
reason phase 112 moved the data directory:

- **`dbdatasync service install` bakes an absolute path into the unit.** `ServiceCommand` /
  `SystemdService` register the service with `ExecStart` / `binPath` set to
  `Environment.ProcessPath` — the apphost shim of *whichever copy ran `service install`*. Install
  from your admin profile and the service points at `/home/you/.dotnet/tools/dbdatasync` or
  `C:\Users\you\.dotnet\tools\dbdatasync.exe` forever. Remove that profile, run the next
  `service install` from a different account, or let a cleanup job touch `~/.dotnet`, and the
  service is broken.
- **It can be actively broken today, not just fragile.** `SystemdService.RenderUnit` applies
  `ProtectHome=yes` when `--repo` is the managed default. A unit whose `ExecStart` is under `/home`
  or `/root` — exactly where `sudo dotnet tool install -g` puts it (`/root/.dotnet/tools`) — cannot
  start: systemd hides that path from the service. The combination "hardened unit + tool in a home
  directory" produces a service that installs cleanly and then fails on first start.
- **Not on `PATH` for anyone else.** `dotnet tool install -g` edits the installing user's shell
  profile only. A second admin, a `cron` job running `dbdatasync health`, an Ansible play — none of
  them find `dbdatasync` without knowing the absolute path.

The fix is the same shape as phase 112: **one documented machine-wide location per platform**, on the
system `PATH`, owned by administrators, readable-and-executable by everyone including the service
account.

---

## What `dotnet tool install --tool-path` gives us, and what it doesn't

`dotnet tool install --tool-path <DIR> DbDataSync --version <v>` installs into an arbitrary directory
instead of the per-user global store. Several tools can share one `--tool-path` directory. But:

- **It does not touch `PATH`.** That is entirely on us.
- **It is managed by its own verbs** — `dotnet tool {list,update,uninstall} --tool-path <DIR>`, never
  the `-g` ones. A machine-wide tool is invisible to `dotnet tool list -g`.
- **Install and update need the .NET SDK; running the tool needs only the runtime** (the ASP.NET
  Core shared framework, since `dbdatasync` hosts Kestrel). Same split phase 120/121 already reason
  about — an SDK box installs/updates; a runtime-only box can still run what's installed.
- **Elevation is required** to write the machine-wide directory (`/opt`, `%ProgramFiles%`), which is
  correct — a machine-wide install is an administrative act.

So the mechanism exists; what's missing is a directory convention, the `PATH` wiring, and a
permissions safety net. The deployer runs one `dotnet tool install` line; a new `dbdatasync install`
command does the rest.

---

## Recommendation up front

1. **`dbdatasync tool install` / `dbdatasync tool uninstall`** — a nested `tool` group, the shape of
   `service install` / `service uninstall`. `tool install` wires an already-restored `--tool-path`
   install into the system; it **infers its own directory** from `Environment.ProcessPath` (it is
   running from the tool directory), so there is nothing to pass. On Linux it symlinks
   `/usr/local/bin/dbdatasync` and fixes permissions defensively; on Windows it appends the
   directory to the Machine `PATH`. `tool uninstall` reverses exactly that. Both are
   elevation-aware: if they can't act, they print the exact elevated command and exit non-zero.
   `tool install` is step 2 of a two-line install.

2. **One default location per platform**, embedded as `CliOptions.DefaultToolDir`, parallel to
   phase 112's data-directory table:

   | platform | tool directory (`DefaultToolDir`) | data directory (phase 112) |
   | --- | --- | --- |
   | Linux | `/opt/dbdatasync` | `/var/lib/dbdatasync` |
   | Windows | `%ProgramFiles%\DbDataSync` | `%ProgramData%\DbDataSync` |
   | macOS | `/usr/local/dbdatasync` | `/Library/Application Support/DbDataSync` |

   `/opt` and `%ProgramFiles%` for the binary; `/var/lib` and `%ProgramData%` for the data — the
   idiomatic split on each platform. The constant is the single source of truth for the docs, for
   `dbdatasync tool install`'s own help/error text, and for `service install`'s warning.

3. **Copy-paste install/update docs, per platform.** Two lines to install, one to update:

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

   Update never re-runs `dbdatasync tool install`: `dotnet tool update` regenerates the apphost shim
   in place, and the symlink / `PATH` entry point at the directory, not a version.

4. **`dbdatasync service install` warns when run from a user-profile copy**, and hard-errors in the
   one genuinely broken case (Linux, hardened unit, `ExecStart` under a home directory). It names the
   machine-wide install commands from (3).

---

## What to build

### `dbdatasync tool install` / `dbdatasync tool uninstall` — `src/DbDataSync.Cli/ToolCommand.cs` (new), a nested group dispatched from `Program.cs` like `service`

```
dbdatasync tool install   [--dir <path>]
dbdatasync tool uninstall [--dir <path>]
```

> Below, `install` / `uninstall` are shorthand for `tool install` / `tool uninstall`.

- **Directory inference.** `toolDir = Path.GetDirectoryName(Environment.ProcessPath)`, overridable
  with `--dir`. Sanity: warn (not refuse) if `toolDir` is under the user profile
  (`Environment.GetFolderPath(UserProfile)` / `$HOME`) — "this looks like a per-user install; the
  docs point machine-wide installs at `<DefaultToolDir>`."
- **`install`, Linux** — needs root:
  - `chmod -R a+rX <toolDir>` (defensive: root's default `022` umask already yields world-readable
    `0755`; this covers a host with a restrictive umask).
  - `ln -sfn <toolDir>/dbdatasync /usr/local/bin/dbdatasync` — on nearly every interactive `PATH`.
  - Not root → print `sudo <toolDir>/dbdatasync install`, exit 1.
- **`install`, Windows** — needs elevation:
  - Append `<toolDir>` to the **Machine** `Path` via
    `[Environment]::SetEnvironmentVariable('Path', …, 'Machine')` (never `setx` — 1024-char
    truncation), guarded against duplicates; broadcast `WM_SETTINGCHANGE`.
  - `icacls` grant only if a named service account is later found not to be in `Users` — reuse the
    exact shape of `ServiceCommand.GrantDataDirectoryAccess`.
  - Not elevated → print the elevated command, exit 1.
- **`install`, macOS** — needs root: write `/etc/paths.d/dbdatasync` containing `<toolDir>`.
- **`dbdatasync uninstall`** — removes exactly what `install` wrote (the `/usr/local/bin` symlink,
  the Machine `PATH` entry, or the `paths.d` file); **leaves the `--tool-path` payload alone** and
  prints the `dotnet tool uninstall --tool-path <toolDir> DbDataSync` line to finish removal. Same
  elevation rules and same "not elevated → print the command" behavior as `install`. It is step 1
  of a two-line uninstall, the mirror of `install` being step 2 of the install.
- Both idempotent: `install` twice reports "already linked" / "already on PATH"; `uninstall` with
  nothing to remove says so. Neither errors on a no-op.
- `install` reports what it did and what the operator must do next (open a new terminal on Windows;
  `dbdatasync config check` everywhere).

### `CliOptions.DefaultToolDir`

A `static string` beside `DefaultRoot` (phase 112), same `OperatingSystem.Is*` branching. Not read
by `serve`/`service` — those still resolve the executable from `Environment.ProcessPath`. Used by
`ToolCommand` (help text, the not-elevated message), by `ServiceCommand`/`SystemdService` (the
warning below), and mirrored literally in the docs (a test asserts `docs/*` contains its value so
the two can't drift).

### `service install` awareness — `ServiceCommand.cs` / `SystemdService.cs`

- After `executable = Environment.ProcessPath`, test whether it is under the user profile
  (`UserProfile` on Windows; `$HOME` / `/home/` / `/root/` on Linux).
- **Windows, or Linux with an un-hardened unit (`--repo` outside the managed root):** a warning —
  "this tool is in a user profile; a service pointing here breaks if that profile is removed or you
  re-register from another account. Install machine-wide first:" then the two lines from
  Recommendation (3), built from `DefaultToolDir`. Continue.
- **Linux, hardened unit (`--repo` is the managed root → `ProtectHome=yes`):** a hard error — the
  service will not start (systemd hides the home directory from it). Refuse, print the fix, exit
  non-zero.
- `service install`'s output gains a "next: `dbdatasync config check`" line (an item
  `app-and-service-setup.md` §6 already wanted).

### Docs

- **`docs/install.md`** (or a section of `docs/getting-started.md` once that exists) — the
  copy-paste blocks from Recommendation (3), one heading per platform, install and update. A short
  paragraph each on: the SDK requirement (install/update needs it; running does not), the
  `--source <feed>` flag for air-gapped, and which contexts see `dbdatasync` on `PATH` (login
  shells, the service via its absolute `ExecStart`, Windows tasks) vs. which need the absolute path
  (`cron`, minimal-environment scripts).
- **`docs/getting-started.md`'s service sections** (supplied here): install the tool machine-wide →
  `dbdatasync service install` → `dbdatasync config check`.
- Update the stale `src/DbDataSync.Cli/README.md` (still says `%LOCALAPPDATA%/DbDataSync`, pre-phase
  112) in the same pass.

### `tools/install-local-tool` + `tools/install-local-tool.cmd` — pack and (re)install from the working tree

A dev-loop helper that packs the CLI and installs or updates the tool from that local `.nupkg`, so a
developer can exercise the *packaged* command surface — including `dbdatasync install` /
`service install` — against their own build. Same shape as `tools/dev-harness` and
`tools/benchmarks`: an `sh` script plus a `.cmd`, both thin wrappers over `dotnet`.

- **Pack.** `dotnet pack src/DbDataSync.Cli -c Release -o <repo>/bin/local-tool-feed
  -p:Version=0.1.0-local.<timestamp>` into a git-ignored feed directory (`bin/` is already ignored).
  The timestamped version is what makes `dotnet tool update` see each build as newer — without it a
  second run against an unchanged `0.1.0` is a no-op. The pack runs the real SPA build
  (`DbDataSync.Api`'s `BuildSpa` target), so **node is required and the first pack is slow**; a
  `--no-spa` flag passes `-p:SkipWebBuild=true` for a CLI-only change where the console isn't being
  tested.
- **Install / update.** `dotnet tool update --add-source <repo>/bin/local-tool-feed --version
  0.1.0-local.<timestamp> DbDataSync` at one of:
  - **default — `--global`**: on the developer's own `PATH` immediately, no elevation. The common
    case for iterating on CLI behavior.
  - **`--machine-wide`**: `--tool-path <DefaultToolDir>` (elevated / `sudo`), then run
    `<DefaultToolDir>/dbdatasync install` — for testing the machine-wide + `service install` flow
    end to end.
  - **`--tool-path <dir>`**: an explicit directory, passed straight through.
- **Uninstall.** `--uninstall` removes whichever install the same target flags select (`dotnet tool
  uninstall`, plus `dbdatasync uninstall` first for `--machine-wide`).
- Prints the resolved version and where it landed. No staleness check of its own — packs every run,
  the same choice `tools/dev-harness` documents.

---

## What this does not do

- **The container.** It has no tool install and no `PATH` question — `ENTRYPOINT` is an absolute
  `dotnet /app/...` invocation.
- **A native OS installer** (`.msi`, `.deb`, `.rpm`, Homebrew formula). That would remove the
  `dotnet tool` / SDK dependency entirely and is a separate, larger piece of work
  (`phase-051-distribution-and-hosting.md` territory).
- **Package acquisition or self-update.** `dotnet tool install|update --tool-path` does all of it —
  restore, the transitive closure, the apphost shim, version replacement. `dbdatasync install` never
  downloads anything and has no network path. Updating is one `dotnet tool update` line.
- **Changing how `serve` / `service` resolve anything.** The service still gets an absolute
  `ExecStart` from `Environment.ProcessPath`; this only changes *what that path is* (a stable
  `/opt` / `%ProgramFiles%` location) and whether a person can also just type `dbdatasync`.
- **Removing `dotnet tool install -g` from the docs.** It stays as the "evaluate locally" path.
- **A shared multi-tool directory.** `--dir` lets a site point `dbdatasync install` at one, but the
  default and the docs are product-namespaced; DbDataSync only ever manages its own entry.

---

## Decisions

Walked through in review; recorded here so the phase doc can cite them.

1. **Command name: `dbdatasync tool install` / `tool uninstall`** — a nested `tool` group, the shape
   of `service install` / `service uninstall`, not two more top-level verbs (phase 115 deliberately
   trimmed that count).
2. **Linux `PATH`: the `/usr/local/bin/dbdatasync` symlink only** — the tool directory holds one
   executable, so `/etc/profile.d` adds nothing the symlink doesn't already cover, and the symlink
   is one reversible operation.
3. **`SetupCommand` step 6 prints the machine-wide `tool install` block** when it accepts the
   service prompt and `Environment.ProcessPath` is under a user profile — consistent with the step's
   existing "print the command, don't elevate mid-session" behavior. In scope for this phase (phase
   110 shipped `setup`).
4. **`service install`: hard error** in the hardened-Linux + home-directory combination (the service
   will not start), **warning** everywhere else (fragile, not broken).
5. **.NET runtime discoverability: `SystemdService` bakes `Environment=DOTNET_ROOT=` into the unit**
   from the install-time process's own runtime location (scoped, no machine-global side effect),
   **plus a `config check` probe** that warns when no runtime is discoverable machine-wide.
6. **SELinux / AppArmor** — an implementation-time verification item (stock RHEL 9 enforcing,
   Ubuntu 24.04), not a design decision. `restorecon` in `tool install` if `/opt` exec by the
   service user is denied.
7. **Windows `.exe` vs. `.cmd`** — non-issue: `dotnet tool install --tool-path` writes a real
   `dbdatasync.exe` apphost, so the Machine `PATH` entry is enough.
