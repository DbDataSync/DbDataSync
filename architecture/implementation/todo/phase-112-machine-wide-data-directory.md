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

There is no reason for the default operating mode to differ by platform or by launch method. Make the
default a single **machine-wide** location on every platform, so `serve`, `setup`, `doctor` and the
service all resolve the same repo with no `--repo`.

## What this builds

### 1. `CliOptions.DefaultRoot` — platform switch

```csharp
public static string DefaultRoot => OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(SpecialFolder.CommonApplicationData), "DbDataSync") // C:\ProgramData\DbDataSync
    : OperatingSystem.IsMacOS()
        ? "/Library/Application Support/DbDataSync"
        : "/var/lib/dbdatasync";
```

- **Windows** — `SpecialFolder.CommonApplicationData` is `C:\ProgramData`, the all-users analog of
  `/var/lib`. Not hard-coded, so a redirected `ProgramData` is honoured.
- **Linux** — `/var/lib/dbdatasync`, matching the container image and phase 111's unit.
- **macOS** — `/Library/Application Support/DbDataSync` hard-coded, because .NET maps
  `CommonApplicationData` to `/usr/share` on macOS (and Linux), which is not a writable data
  location.

`DbDataSyncRoot.Resolve`'s walk-up and explicit-`--repo` behaviour is unchanged; only the fallback
moves.

### 2. First-run directory creation and permissions

The default location is no longer guaranteed writable by the running user:

- **Windows** — `C:\ProgramData`'s default ACL lets `Users` create a subdirectory and makes the
  creator its owner, so an interactive first `serve` still works. The **service** matters: as
  `LocalSystem` it has full control anyway; as a **named account** (`service install --account`),
  `install` must `icacls` the directory to grant that account `Modify` — the same "issuing and
  granting are one operation" principle phase 82 applied to certificate keys. Add this to
  `ServiceCommand.Install`.
- **Linux** — phase 111's `install` already creates `/var/lib/dbdatasync` and `chown`s it to the
  service `User`. An interactive `serve` as a non-root user that cannot write `/var/lib` fails
  clearly: *"Cannot create /var/lib/dbdatasync (permission denied). Run `dbdatasync setup` (which
  will use sudo where needed), or pass `--repo <a writable path>`."*
- **macOS** — same message for `/Library/Application Support` written by a non-admin.

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

### 4. Docs and phase 111

- `CONFIG.md`'s "Repo root resolution" table and the per-command defaults update to the new paths.
- Phase 111's carve-out ("`serve` run interactively is unchanged — it still uses … `DefaultRoot`")
  is removed: after this phase `DefaultRoot` *is* `/var/lib/dbdatasync` on Linux, so interactive and
  service agree with no special case. Phase 111's `install` still creates and `chown`s the directory
  (the interactive path does not chown to a service user).

## What this phase does not build

- Moving anyone's data. The tooling detects and instructs; the operator moves.
- Changing `ApiOptions`' own raw default (`<cwd>/dbdatasync-repo`) — that is the "ran the API
  project directly" dev path and is not a distribution concern.
- A `dbdatasync migrate` command. If the "move it yourself" message proves to generate support
  load, a helper is a small follow-up.

## How to verify when built

- `dotnet build` clean.
- **`CliOptionsTests`** — `DefaultRoot` returns the expected path per `OperatingSystem.Is*` (mock or
  run per-OS in CI matrix); it is machine-wide, not under a user profile.
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
2. **macOS default.** Is `/Library/Application Support/DbDataSync` right, or should macOS (a dev
   platform here, not a server) stay per-user at `~/Library/Application Support/DbDataSync`?
   Leaning: per-user on macOS — there is no macOS service story (phase 111 excludes launchd), so
   the machine-wide argument does not apply. Which makes the switch Windows+Linux machine-wide,
   macOS per-user.
3. **The migration check's cost.** It stats one extra directory on every `serve` startup forever.
   Negligible, but it could be gated to "only when the new default is empty" (which is already the
   condition) and dropped entirely a few releases later.
