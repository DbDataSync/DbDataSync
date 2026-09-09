# Phase 112 — one machine-wide data directory per platform (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/app-and-service-setup.md`. Related to phase 111,
which it partly supersedes (see below).

## Why

`CliOptions.DefaultRoot` is `Environment.SpecialFolder.LocalApplicationData` + `DbDataSync` —
`%LOCALAPPDATA%\DbDataSync` on Windows, `~/.local/share/DbDataSync` on Linux/macOS. That is a
**per-user** location, and it makes the interactive tool and the service disagree:

- Windows `dbdatasync serve` (interactive) uses `%LOCALAPPDATA%\DbDataSync`; the Windows service
  runs as `LocalSystem` and its `%LOCALAPPDATA%` is `C:\Windows\system32\config\systemprofile\…` —
  so `service install` has to pass an explicit `--repo` and the two modes never share a repo.
- Phase 111 papers over the same split on Linux by hard-coding `/var/lib/dbdatasync` for the systemd
  path only, leaving interactive `serve` on `~/.local/share`.

There is no reason for the default operating mode to differ by platform or by launch method. Give
every major platform **one documented, machine-wide data directory**, so `serve`, `setup`, `doctor`
and the service all resolve the same repo with no `--repo`, and give every *other* platform a single
environment variable to point at one of its own.

## What this builds

### 1. Root resolution — an env var, then a per-platform default

`DbDataSyncRoot.Resolve` becomes: explicit `--repo` → walk up for `dbdatasync.config.yaml` (git-style,
unchanged) → **`$DBDATASYNC_HOME`** → `CliOptions.DefaultRoot`.

`DBDATASYNC_HOME` already exists as a decorative `ENV` in the `Dockerfile` and is read *nowhere*.
Formalise it: it is the escape hatch for any platform whose default this phase does not get right
(and lets the container image drop the explicit `--repo` from its `CMD` — §4).

`CliOptions.DefaultRoot` — platform switch:

```csharp
public static string DefaultRoot =>
    OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(SpecialFolder.CommonApplicationData), "DbDataSync")
    : OperatingSystem.IsMacOS() ? "/Library/Application Support/DbDataSync"
    : OperatingSystem.IsFreeBSD() ? "/var/db/dbdatasync"
    : "/var/lib/dbdatasync";
```

| platform | default | rationale |
| --- | --- | --- |
| **Windows** | `%ProgramData%\DbDataSync` (`SpecialFolder.CommonApplicationData`, not hard-coded, so a redirected `ProgramData` is honoured) | the all-users store, `LocalSystem`'s analog of `/var/lib` |
| **macOS** | `/Library/Application Support/DbDataSync` (hard-coded) | the machine-wide app-support location; `CommonApplicationData` maps to `/usr/share` on macOS, which is wrong |
| **FreeBSD** | `/var/db/dbdatasync` (hard-coded) | FreeBSD `hier(7)` puts server application data under `/var/db/<app>` (`/var/db/mysql`, `/var/db/pkg`); **`/var/lib` does not exist** on a stock FreeBSD |
| **Linux** | `/var/lib/dbdatasync` | FHS `/var/lib/<pkg>`; matches the container image and phase 111's unit |
| **other Unix** (Solaris/illumos, NetBSD, OpenBSD, …) | falls through to `/var/lib/dbdatasync` | `/var/lib` exists on Solaris and is the least-surprising generic default; it is **not** idiomatic everywhere (NetBSD/OpenBSD favour `/var/db`), so the docs tell these operators to set `DBDATASYNC_HOME`. .NET has no `Is*` for these and barely runs on them — a per-OS branch is not worth carrying |

`OperatingSystem.IsFreeBSD()` is a real BCL method (since .NET 5). There is no `IsSolaris` /
`IsNetBSD` / `IsOpenBSD`, which is why those share the final fallthrough.

Only the fallback moves; the `--repo` flag and the walk-up are unchanged.

### 2. First-run directory creation and permissions

The default location is no longer guaranteed writable by the running user:

- **Windows** — `C:\ProgramData`'s default ACL lets `Users` create a subdirectory and makes the
  creator its owner, so an interactive first `serve` still works. The **service** matters: as
  `LocalSystem` it has full control anyway; as a **named account** (`service install --account`),
  `install` must `icacls` the directory to grant that account `Modify` — the same "issuing and
  granting are one operation" principle phase 82 applied to certificate keys. Add this to
  `ServiceCommand.Install`.
- **Linux / FreeBSD** — phase 111's systemd `install` already creates the directory and `chown`s it
  to the service `User`. An interactive `serve` as a non-root user that cannot write `/var/lib`
  (`/var/db` on FreeBSD) fails clearly: *"Cannot create /var/lib/dbdatasync (permission denied). Run
  `dbdatasync setup` (which uses sudo where needed), set `DBDATASYNC_HOME` to a writable path, or
  pass `--repo`."*
- **macOS** — `/Library/Application Support` needs admin to create a subdirectory. `dbdatasync setup`
  runs the `mkdir` + `chown` under `sudo`; a bare `serve` prints the same message. (A per-user Mac
  dev who does not want `sudo` sets `DBDATASYNC_HOME=~/Library/Application Support/DbDataSync`.)

### 3. Migration for existing installs

An upgrade must not silently strand a working `%LOCALAPPDATA%\DbDataSync` / `~/.local/share/DbDataSync`
repo. `serve`, `setup` and `doctor`, when they resolve the *new* default and find **nothing there**,
check the *old* per-user location:

- old location has a real configuration (the `ExistingSetup.DetectedAt` predicate phase 110 added)
  → print, do not act:

  ```
  Found an existing DbDataSync configuration at
      C:\Users\me\AppData\Local\DbDataSync
  The default is now C:\ProgramData\DbDataSync. Either:
    - move that folder there, then re-run; or
    - keep it where it is: dbdatasync serve --repo "C:\Users\me\AppData\Local\DbDataSync"
      (and pass the same --repo to `service install` / set it in dbdatasync.config.yaml)
  ```

- No auto-move: the folder may be large, a service account change is involved, and a half-moved git
  repo is worse than a clear message.
- `setup`'s Step 0 gets the same check and offers "use the existing folder at the old location" as a
  menu choice that just records `--repo` guidance.

### 4. Docs, the container image, and phase 111

- `CONFIG.md`'s "Repo root resolution" section gains the `DBDATASYNC_HOME` step and a per-platform
  default table (Windows / macOS / FreeBSD / Linux / other-Unix), and notes the last row's operators
  should set `DBDATASYNC_HOME`.
- **`Dockerfile`** — with `DBDATASYNC_HOME=/var/lib/dbdatasync` now actually read, the explicit
  `CMD ["--repo", "/var/lib/dbdatasync"]` is redundant and can go (the `ENTRYPOINT` keeps `serve
  --url …`). One less place the path is written.
- Phase 111's carve-out ("`serve` run interactively is unchanged — it still uses … `DefaultRoot`")
  is removed: after this phase `DefaultRoot` *is* the machine-wide path, so interactive and service
  agree with no special case. Phase 111's `install` still creates and `chown`s the directory to the
  service account (the interactive path does not).

## What this phase does not build

- Moving anyone's data. The tooling detects and instructs; the operator moves.
- Changing `ApiOptions`' own raw default (`<cwd>/dbdatasync-repo`) — that is the "ran the API
  project directly" dev path and is not a distribution concern.
- A `dbdatasync migrate` command. If the "move it yourself" message proves to generate support
  load, a helper is a small follow-up.
- Per-OS branches for Solaris/illumos, NetBSD, OpenBSD. `DBDATASYNC_HOME` is their answer; if one of
  them becomes a real target, a one-line `IsX()`-style check (once .NET exposes one) is trivial to
  add.

## How to verify when built

- `dotnet build` clean.
- **`CliOptionsTests`** — `DefaultRoot` returns the expected path on the running OS (Windows / macOS
  / Linux at least; a CI matrix already runs on all three), and it is machine-wide, not under a user
  profile.
- **`DbDataSyncRootTests`** — `$DBDATASYNC_HOME` beats `DefaultRoot` but loses to an explicit
  `--repo` and to a walk-up hit; unset behaves as today.
- **`ServiceCommandTests`** — Windows `install --account CONTOSO\svc` emits an `icacls` grant for
  the data directory; Linux `install --user dbdatasync` creates and `chown`s it (fake runners).
- **`SetupCommandTests` / `DoctorCommandTests`** — with a config only at the old per-user location
  and nothing at the new default, the migration message is printed and names both paths; with a
  config at the new default, no message.
- Manual: fresh Windows install → `serve` with no args → repo at `C:\ProgramData\DbDataSync`;
  `service install` (no `--repo`) → the service uses the same repo. Same on Linux with
  `/var/lib/dbdatasync`.

## Open questions

1. **Windows interactive first-run ownership.** A user creates `C:\ProgramData\DbDataSync` and owns
   it; a later `service install --account svc` grants `svc` Modify but the *owner* is still the
   user. Is that a problem in practice? Leaning: no — Modify is enough; but `install` could also
   take ownership for the service account. Decide when writing the `icacls` call.
2. **macOS creation UX.** `/Library/Application Support/DbDataSync` needs admin to create. Is
   `setup` shelling `sudo mkdir` acceptable, or should the Mac default fall back to per-user when
   the process is not admin and `DBDATASYNC_HOME` is unset? Leaning: machine-wide with the `sudo`
   step (consistent with every other platform), `DBDATASYNC_HOME` for the dev who wants otherwise.
3. **FreeBSD: `/var/db` vs `/usr/local/var`.** Ports-installed daemons sometimes use
   `/usr/local/var/<app>`; `hier(7)` and the datadir precedents (`/var/db/mysql`) point at
   `/var/db`. Leaning: `/var/db/dbdatasync`. Confirm with anyone actually running it there.
4. **The migration check's cost.** It stats one extra directory on every `serve` startup forever.
   Negligible, but it could be gated to "only when the new default is empty" (already the condition)
   and dropped entirely a few releases later.
