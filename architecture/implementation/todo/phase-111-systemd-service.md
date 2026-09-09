# Phase 111 — systemd service registration on Linux (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/app-and-service-setup.md`. Picks up the deferral
phase 51 named explicitly: *"Linux service registration (systemd unit). Worth having, not asked for,
and the container covers the same ground on Linux hosts."* The container still covers it; this is
for the operator who wants DbDataSync on a Linux host without Docker, the same way the Windows
service serves the Windows operator.

## What this builds

`dbdatasync service install | uninstall | status` starts working on Linux, mirroring the Windows
`sc.exe` path — it writes a systemd unit, enables and starts it, and says what it did and what it
could not. Plus the host-side integration that makes the process behave as a service, and the
`setup` step that offers it.

### 1. Host integration — `src/DbDataSync.Api/`

- `DbDataSync.Api.csproj` — add `Microsoft.Extensions.Hosting.Systemd` (version-aligned with the
  existing `Microsoft.Extensions.Hosting.WindowsServices` at `10.0.0`).
- `DbDataSyncHost.Build` — alongside the `WindowsServiceHelpers.IsWindowsService()` branch:

  ```csharp
  else if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
      builder.Host.UseSystemd();
  ```

  Same reasoning as the Windows branch: applied **only** when the process really is a systemd
  service, so `dbdatasync serve` in a terminal is unaffected. `UseSystemd()` switches the lifetime
  to one that sends `sd_notify(READY=1)` when the host has started and `RELOADING`/`STOPPING` as it
  shuts down, and routes logging to the journal format — which is what lets the unit be
  `Type=notify` (below).

### 2. `dbdatasync service` — the Linux branch — `src/DbDataSync.Cli/ServiceCommand.cs`

Today `Run` bails on non-Windows with a message pointing at systemd-by-hand. Replace that with a
platform split: `RuntimeInformation.IsOSPlatform(OSPlatform.Linux)` → a `SystemdService` collaborator
(new file `src/DbDataSync.Cli/SystemdService.cs`), keeping the `sc.exe` code as the Windows path.
macOS still bails (see *does not build*).

- **`install`** —
  - resolves the apphost path (`Environment.ProcessPath`), the repo root, the URL, and the service
    `User` (below), exactly as the Windows path resolves its `binPath` inputs;
  - writes `/etc/systemd/system/dbdatasync.service` (the unit, §*The unit file*);
  - `systemctl daemon-reload`;
  - `systemctl enable --now dbdatasync`;
  - prints the resolved values — repo, console URL, unit path, `User` — the same "find out now, not
    at the first failed start" print the Windows path does, and the integrated-auth note adapted for
    Linux (a connection using `IntegratedAuth` authenticates via the service account's Kerberos
    credentials / keytab, if any).
- **`uninstall`** — `systemctl disable --now dbdatasync`; remove the unit; `daemon-reload`. Leaves
  the repo and state on disk, like `sc delete` does.
- **`status`** — `systemctl status dbdatasync --no-pager` passthrough (exit code forwarded).
- **Elevation** — writing under `/etc/systemd/system` and `daemon-reload` need root. Check
  `geteuid()` (via `Mono.Posix`-free `[DllImport("libc")] static extern uint geteuid()`, or simpler:
  attempt the write and catch `UnauthorizedAccessException`), and on failure print *"Registering a
  systemd service needs root — re-run with `sudo dbdatasync service install …`"* — the counterpart of
  the Windows `ERROR_ACCESS_DENIED` (5) → sentence.

### 3. Repo-root default on Linux

Windows `service install` defaults `--repo` to `CliOptions.DefaultRoot`
(`%LOCALAPPDATA%\DbDataSync`). A Linux **system** service running as a dedicated account has no
business under a per-user data dir. Default `--repo` to **`/var/lib/dbdatasync`** on Linux (matching
the container image's volume), and have `install` create it and `chown` it to the service `User`.
`serve` run interactively is unchanged — it still uses `DbDataSyncRoot.Resolve` / `DefaultRoot`.

### 4. `setup` step 6 — `src/DbDataSync.Cli/SetupCommand.cs`

The `OperatingSystem.IsWindows()` block that prints the `dbdatasync service install` command gains an
`else if (OperatingSystem.IsLinux())` twin:

```
Register dbdatasync as a systemd service? [y/N]
Service account [dbdatasync]:
Run this as root to finish:
    sudo dbdatasync service install --repo "/var/lib/dbdatasync" --url http://localhost:5080 --user dbdatasync
```

Same "print the command, do not orchestrate elevation mid-flow" decision setup already made for
Windows. The certificate step stays Windows-only; on Linux setup points at the reverse-proxy section
of the getting-started doc (TLS on Linux is a reverse proxy, or `Kestrel:Certificates:Default` in
config — neither is a `cert` subcommand).

### 5. Documentation — `docs/getting-started.md`

The Linux section becomes two paths: **behind a reverse proxy** (already planned) and **as a systemd
service** — `dbdatasync service install` (or `dbdatasync setup`), then the reverse proxy in front for
TLS. One `nginx`/`Caddy` snippet, and the `DbDataSync:Url` / `Passkeys:RelyingPartyId` / `Origins`
values that must match the public hostname.

## The unit file

Written by `SystemdService`, values resolved at install time:

```ini
[Unit]
Description=DbDataSync — cross-database replication
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=/usr/…/dbdatasync serve --repo /var/lib/dbdatasync --url http://localhost:5080
User=dbdatasync
Group=dbdatasync
WorkingDirectory=/var/lib/dbdatasync
StateDirectory=dbdatasync
Restart=on-failure
RestartSec=5
# Modest hardening — safe for the default repo root; skipped/relaxed if --repo is elsewhere.
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
ReadWritePaths=/var/lib/dbdatasync

[Install]
WantedBy=multi-user.target
```

- **`Type=notify`** because §1's `UseSystemd()` sends `READY=1` — so `systemctl start` blocks until
  the host is actually serving, and a failed start is a failed `systemctl` command rather than a
  process that exits 30 seconds later.
- **`ReadWritePaths`** is the repo root. When `--repo` points somewhere `ProtectSystem=strict` would
  make read-only, `install` widens `ReadWritePaths` to include it, or drops the hardening block with
  a printed note — the operator's path choice wins over the default hardening.
- `ExecStart` carries the resolved apphost + args, same as the Windows `binPath`.

## Service account

Windows defaults to `LocalSystem`. The Linux analog is a **dedicated system user**:

- `--user <name>` (default `dbdatasync`). `install` runs `useradd --system --no-create-home
  --shell /usr/sbin/nologin <name>` if the user does not exist (and says it did), then `chown`s the
  repo root.
- `DynamicUser=yes` (systemd creates an ephemeral UID per start, with `StateDirectory` following it)
  is the modern best practice and considered — but it interacts badly with a git-tracked repo whose
  files an operator also edits, and with a `--repo` outside `/var/lib`. A named static user is more
  predictable and is what a Windows operator's mental model maps onto. Left as an open question.

## What this phase does not build

- **launchd** (macOS). macOS is a dev platform here, not a server target; `service` on macOS keeps
  bailing with a pointer to `dbdatasync serve` / the container.
- **A Linux `cert` command.** TLS on Linux is a reverse proxy or `Kestrel:Certificates:Default` in
  config. `dbdatasync cert` stays Windows-only (it is the Windows cert store + `netsh http`).
- **`systemctl --user`** as the default. A one-line note in the docs that it is possible (needs
  `loginctl enable-linger`), but `install` writes a system unit.
- **Packaging** — no `.deb`/`.rpm` that drops the unit; the tool writes it. A distro package is a
  separate decision, exactly as phase 51 left the feed decision separate.

## How to verify when built

- `dotnet build` clean; `Microsoft.Extensions.Hosting.Systemd` is the only new reference.
- **`SystemdServiceTests`** (unit, no root) — the rendered unit file for given inputs: `ExecStart`
  quoting, `User`/`Group`, `ReadWritePaths` = the repo root, the hardening-relaxed variant when
  `--repo` is outside `/var/lib`.
- **`ServiceCommandTests`** — on a non-Windows test host, `service install` with a fake "systemctl"
  (a `PATH`-shimmed script, or an injected runner) writes the unit to a temp path and calls
  `daemon-reload` + `enable --now`; a write-permission failure prints the `sudo` sentence.
- **`SetupCommandTests`** — the Linux branch of step 6 prints the `service install --user` command
  with the resolved root and URL.
- **Manual, on a real Linux host** (no CI Linux-with-systemd runner — say so, as phase 51 did for
  the Windows service): `sudo dbdatasync service install` → `systemctl status` shows active
  (running), the console answers, `Type=notify` made `start` block correctly; a reboot brings it
  back; `uninstall` removes it and leaves `/var/lib/dbdatasync`. `journalctl -u dbdatasync` shows
  structured logs (the `UseSystemd()` journal formatter).
- **Host integration** — `SystemdHelpers.IsSystemdService()` is false under `dbdatasync serve` in a
  terminal (so Ctrl+C and console output are unchanged) and true under the unit.

## Open questions

1. **Named static `User=` vs. `DynamicUser=yes`.** Leaning: named static (`dbdatasync`), created by
   `install` — predictable, maps onto the Windows model, works with a hand-edited git repo. Revisit
   if the hardening story pushes toward `DynamicUser`.
2. **How much hardening in the default unit.** `ProtectSystem=strict` + `ReadWritePaths` is safe for
   the default root; every added directive is one more thing that breaks a non-default `--repo`.
   Leaning: the block above, with `install` relaxing it (and saying so) when the root is unusual.
3. **`geteuid` via P/Invoke vs. try-and-catch the write.** Leaning: try the write and catch — no
   P/Invoke, and it also catches SELinux/AppArmor denials a uid check would miss.
4. **Does `install` start the service (`--now`) or just enable it?** Windows `sc create … start=
   auto` registers but does not start. Leaning: match Windows — `enable` without `--now`, and print
   `systemctl start dbdatasync` as the next step, so the operator sees the first start's output.
5. **Unit name collision / multiple instances** — one host, two DbDataSync repos. Windows has the
   same single-`ServiceName` limit. Out of scope, but the unit name could take a suffix from `--name`
   later.
