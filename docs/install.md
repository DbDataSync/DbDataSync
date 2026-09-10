# Installing `dbdatasync` machine-wide

`dbdatasync` ships as a .NET global tool. `dotnet tool install --global` puts it in the *installing
user's own profile* (`~/.dotnet/tools`, `%USERPROFILE%\.dotnet\tools`) — fine for trying it out, but the
wrong place for a service: `dbdatasync service install` bakes the executable's path into the service
registration, so a service pointing into someone's profile breaks the moment that profile is cleaned up,
or the tool is re-registered from a different account. It's also invisible to everyone else on the
machine.

`dotnet tool install --tool-path <DIR>` installs to a directory of your choosing, but does nothing
about `PATH` or permissions — that's what `dbdatasync tool install` does. The two steps together are a
proper machine-wide install: the binary equivalent of the machine-wide *data* directory
(`%ProgramData%\DbDataSync` / `/var/lib/dbdatasync`) `dbdatasync serve` already defaults to.

You need the **.NET SDK** to install or update `dbdatasync` (`dotnet tool install`/`update` restore and
publish it); only the **.NET runtime** is needed to actually run it afterwards.

## Linux

```sh
# Install
sudo dotnet tool install --tool-path /opt/dbdatasync DbDataSync
sudo /opt/dbdatasync/dbdatasync tool install

# Update
sudo dotnet tool update --tool-path /opt/dbdatasync DbDataSync
```

`tool install` links `/opt/dbdatasync/dbdatasync` from `/usr/local/bin/dbdatasync`, world-readable and
-executable, so anyone on the machine can run `dbdatasync` directly. `tool update` **does not** need
`tool install` run again — the symlink already points at the directory, not at a specific file inside
it, so the regenerated shim underneath it is picked up automatically.

To use a different feed (an air-gapped network, an internal mirror), add `--source <feed>` to the
`dotnet tool install`/`update` command; `dbdatasync tool install` itself never touches the network.

## Windows (elevated PowerShell)

```powershell
# Install
dotnet tool install --tool-path "$env:ProgramFiles\DbDataSync" DbDataSync
& "$env:ProgramFiles\DbDataSync\dbdatasync.exe" tool install

# Update
dotnet tool update --tool-path "$env:ProgramFiles\DbDataSync" DbDataSync
```

`tool install` appends the directory to the **Machine** `Path` (not `setx`, which silently truncates
past 1024 characters) and broadcasts the change so already-open windows notice — open a **new**
terminal regardless, since nothing forces every running process to re-read its environment. As on
Linux, `tool update` never needs `tool install` run again: the `PATH` entry names the directory, and
the shim inside it is what changes.

## Removing it

```sh
sudo dbdatasync tool uninstall
sudo dotnet tool uninstall --tool-path /opt/dbdatasync DbDataSync
```

`tool uninstall` removes exactly what `tool install` added (the symlink, the `/etc/paths.d` entry, or
the `Path` segment) and prints the `dotnet tool uninstall` command that removes the underlying install
— it never removes that on its own, the same way `tool install` never restores a package on its own.

## Registering the service

Once installed machine-wide:

```sh
sudo dbdatasync service install     # Linux (systemd) — see `dbdatasync service --help`
dbdatasync config check
```

```powershell
dbdatasync service install          # Windows (elevated) — registers as a Windows service
dbdatasync config check
```

`service install` warns (Linux, a non-default `--repo`) or refuses outright (Linux, the default
machine-wide `--repo` — the unit hardens with `ProtectHome=yes`, which would hide a user-profile
executable from the service entirely, installing cleanly and then failing to start) if it notices its
own executable is still sitting in a user profile. Installing machine-wide first, as above, is what
that warning/refusal is pointing you at.

## What sees `dbdatasync` on `PATH`, and what doesn't

- **A login shell** (an interactive terminal, a new one especially) — yes, once `tool install` has run
  and (Windows) you've opened a new one.
- **The registered service** — irrelevant either way: `service install` resolves and bakes in the
  *absolute* path to the executable, not a bare `dbdatasync` that depends on `PATH` at start time.
- **A `cron` job, or anything else that doesn't source a login shell's profile** — no. Use the absolute
  path (`/opt/dbdatasync/dbdatasync`, or wherever `--dir` pointed) rather than assuming `PATH` reaches
  there.

## Notes

- `dbdatasync tool install`/`uninstall` accepts `--dir <path>` to override the directory it infers from
  its own running location — mostly useful for testing, or an install that used a nonstandard
  `--tool-path` to begin with.
- Running `tool install`/`uninstall` from a per-user install (`~/.dotnet/tools`) works, but prints a
  note pointing at the machine-wide sequence above instead — it isn't refused, since a single-user,
  single-account deployment is a real (if smaller) use case too.
