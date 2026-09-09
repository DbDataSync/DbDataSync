# Phase 111 — systemd service registration on Linux

**Status**: Done. Implemented last among 115/112/113/114/111 despite its number, per explicit
direction — 114 (ACME) was scoped and deliberately deferred instead of built (see its own doc).
**Plan reference**: `architecture/planning/todo/app-and-service-setup.md`. Picks up the deferral
phase 51 named explicitly.

## What this built

`dbdatasync service install|uninstall|status` now works on Linux, alongside the existing Windows
`sc.exe` path — `ServiceCommand.Run` branches on `RuntimeInformation.IsOSPlatform`
(`Windows`/`Linux`/neither), each branch keeping its own subcommand switch so nothing had to change
about how the Windows path already works.

### `src/DbDataSync.Cli/SystemdService.cs` (new)

- `RenderUnit(executable, root, url, user)` — pure, no I/O — builds the unit file text. `Type=notify`
  (§ below); hardening (`NoNewPrivileges`/`ProtectSystem=strict`/`StateDirectory=`) only when `root`
  is exactly the default `/var/lib/dbdatasync` — anywhere else, `ProtectSystem=strict` would make that
  path read-only, so the hardening is skipped rather than silently breaking an operator's own chosen
  `--repo`, with `ReadWritePaths=` still granted either way.
- `Install`/`Uninstall`/`Status` — create the system user if missing (`useradd --system
  --no-create-home --shell /usr/sbin/nologin`), write/remove the unit, `chown` the repo root to that
  user, and run `systemctl daemon-reload`/`enable`/`disable --now`/`status --no-pager`. `install`
  **enables but does not start** the unit — resolved from open question 4's own leaning over the
  narrative text's "enable --now", matching the Windows `sc create ... start= auto` precedent exactly
  (registers, does not start) and printing `systemctl start dbdatasync` as the next step, so the first
  start's own output is visible to the operator rather than scrolling by during `install`.
- Every external effect (user existence/creation, the unit file, `chown`, every `systemctl` call)
  goes through `ISystemdEnvironment`, implemented for real by `RealSystemdEnvironment` and faked by
  `FakeSystemdEnvironment` in tests — the plan doc's own "an injected runner" suggestion.
- A failed write/`useradd` prints the Linux counterpart of the Windows path's `ERROR_ACCESS_DENIED`
  (5) → sentence: *"registering a systemd service needs root — re-run with `sudo dbdatasync service
  install ...`"*.

### Host integration — `DbDataSyncHost.Build`

Added `Microsoft.Extensions.Hosting.Systemd` (version-aligned at `10.0.0` with the existing
`Microsoft.Extensions.Hosting.WindowsServices`). Alongside the existing `WindowsServiceHelpers
.IsWindowsService()` branch: `else if (SystemdHelpers.IsSystemdService()) builder.Host.UseSystemd();`
— applied only when the process really is a systemd service, so `dbdatasync serve` in a terminal is
unaffected, mirroring the Windows branch's own reasoning exactly.

### `CliOptions.DefaultRoot` — already machine-wide, no extra work needed

The plan doc's own §3 ("Repo-root default on Linux") asked for `service install` to default `--repo`
to `/var/lib/dbdatasync` specifically, since at the time it was written `CliOptions.DefaultRoot` was
still per-user. Phase 112 (implemented before this phase, out of number order) already made
`CliOptions.DefaultRoot` machine-wide — `/var/lib/dbdatasync` on Linux — for *every* command, so
`ServiceCommand`'s existing `CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot` line already
resolves correctly with zero changes. §3 is fully superseded, not built separately.

### `setup`'s service step, extended to Linux

Mirrors the existing Windows branch exactly: *"Register dbdatasync as a systemd service?"* → prompts
the service user (default `dbdatasync`) → prints `sudo dbdatasync service install --repo ... --url
... --user ...` to run. Printed, not invoked — the same "don't orchestrate elevation mid-session"
decision the Windows step already made, now for the same underlying reason (root, not an admin
prompt).

### Tests

- **`SystemdServiceTests`** — `RenderUnit`: default root gets `StateDirectory=`/hardening, a
  non-default root doesn't (but still gets `ReadWritePaths=`), executable/repo paths are quoted.
  `Install`/`Uninstall`/`Status` driven through `FakeSystemdEnvironment`: creates the user only when
  missing, writes the unit and chowns the root, `daemon-reload`s and `enable`s but never `start`s, a
  failed user-creation fails loud without writing anything, `uninstall` disables/removes/reloads,
  `status` passes its exit code through.
- **Two tests against the real `RealSystemdEnvironment`**, deliberately read-only and side-effect-free
  (`id -u root` / a nonexistent user; `systemctl --version`) — this sandbox turned out to be a real
  Linux host with working `systemctl`/`sudo`/`useradd`, not a container, so the actual process-shelling
  plumbing could be proven correct directly rather than only through the fake. See Decisions for why
  this stops short of a full real install.
- **`ServiceCommandTests`** (new) — `Run`'s own dispatch: no-args usage, unknown subcommand. Nothing
  that would route to `SystemdService`'s real environment, since `ServiceCommand.Run` has no
  injectable seam of its own at that layer — see Decisions.
- **`SetupCommandTests`** — every full-walk-through script gained one more scripted answer for the
  new Linux service-registration prompt.

## Decisions

- **No real `sudo dbdatasync service install` was run against this sandbox**, even though — unlike
  every other Windows-only piece of this whole five-phase run — this environment genuinely has
  working `systemctl`, `useradd`, and passwordless-capable `sudo`, and could have. This sandbox turned
  out to be a real, apparently-persistent development machine, not a disposable CI container: writing
  a real unit into `/etc/systemd/system`, creating a real system user, and registering a real service
  is a host-state mutation outside this repository, on a machine that isn't mine to permanently alter
  without being asked. The fake-environment tests prove the *logic* completely; the two real-but-
  read-only checks (`id -u`, `systemctl --version`) prove the *plumbing* actually works on this kind
  of host. A full end-to-end install/start/uninstall cycle is the one piece of verification this phase
  still needs, on a host the operator has explicitly offered up for that (a disposable VM/container,
  or this same machine with explicit sign-off).
- **`install` enables but does not start**, resolving the plan doc's own internal inconsistency
  between its narrative text ("`systemctl enable --now dbdatasync`") and open question 4's leaning
  (match Windows: register, don't start). Took the open question's leaning — it's the section that
  exists specifically to settle exactly this, and matching the Windows precedent keeps one mental
  model across both platforms.
- **`geteuid`-via-P/Invoke vs. try-the-write-and-catch** — resolved as leaned: no P/Invoke. `Install`
  attempts the user creation and the unit write directly and catches `UnauthorizedAccessException`/
  `IOException`, which also catches SELinux/AppArmor denials a bare uid check would miss.
- **Named static `User=dbdatasync`, not `DynamicUser=yes`** — resolved as leaned, for the reasons the
  plan doc already gave (a git-tracked repo an operator hand-edits, and a `--repo` outside the default
  location, both work worse with a per-start ephemeral UID).

## What this phase does not build

- **launchd (macOS)** — `service` on macOS keeps bailing, pointing at `dbdatasync serve` / the
  container.
- **A Linux `cert` command** — unchanged; TLS on Linux is `dbdatasync config cert use-pem`/`use-pfx`
  (phase 113) or a reverse proxy.
- **`systemctl --user`** — `install` always writes a system unit.
- **Packaging** (`.deb`/`.rpm`) — a separate decision, as phase 51 already left it.
- **A real, on-host install/start/uninstall verification** — see Decisions.
- **`docs/getting-started.md`'s Linux section** — that file does not exist yet in this repository.

## How it was verified

- `dotnet build DbDataSync.slnx` clean; `Microsoft.Extensions.Hosting.Systemd` is the only new
  reference, version-aligned with `Microsoft.Extensions.Hosting.WindowsServices`.
- Full `dotnet test --filter "Category!=Integration"` green solution-wide (`DbDataSync.Cli.Tests`: 82,
  up from 70 before this phase).
- Full `dotnet test --filter "Category=Integration"` green solution-wide.
- Two tests execute real `id`/`systemctl` invocations on this actual host (read-only, see Decisions).
- Not done: a real `sudo dbdatasync service install` → `systemctl status` shows active → the console
  answers → `uninstall` removes it cycle, on a host where that's an appropriate thing to do to.
