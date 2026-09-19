# Installing dbdatasync

[DbDataSync](../README.md) · **Install** · [Configuration](configuration.md) · [Getting started](getting-started.md) · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · [State database](state-database.md) · [Building from source](development.md)

`dbdatasync` ships as a .NET global tool. Pick the install below that matches how you plan to run it.

## Prerequisites

- **.NET SDK** — needed to install or update `dbdatasync`
- **.NET runtime** — enough to run `dbdatasync` afterward

## User-local install

Use this to try DbDataSync out, or for a single-user setup.

```sh
dotnet tool install -g DbDataSync
dbdatasync setup
```

This installs into your own profile (`~/.dotnet/tools`, `%USERPROFILE%\.dotnet\tools`). It's not
suitable for a Windows or systemd service, or for a machine shared by more than one person — see the
sections below for those.

## Windows machine-wide install

Use this to register DbDataSync as a Windows service, or to make `dbdatasync` available to every user
on a shared machine.

```powershell
dotnet tool install --tool-path "$env:ProgramFiles\DbDataSync" DbDataSync
& "$env:ProgramFiles\DbDataSync\dbdatasync.exe" tool install
```

`tool install` adds the directory to the Machine `Path`. Open a new terminal for it to take effect.

To register the service:

```powershell
dbdatasync service install
dbdatasync config check
```

`--account` sets the service account. A connection using integrated authentication connects as that
account.

To update:

```powershell
dotnet tool update --tool-path "$env:ProgramFiles\DbDataSync" DbDataSync
```

To remove:

```powershell
dbdatasync tool uninstall
dotnet tool uninstall --tool-path "$env:ProgramFiles\DbDataSync" DbDataSync
```

## Linux machine-wide install

Use this to register DbDataSync as a systemd service, or to make `dbdatasync` available to every user
on a shared machine. The same steps work on macOS; there, `tool install` adds a `/etc/paths.d` entry
instead of a symlink.

```sh
sudo dotnet tool install --tool-path /opt/dbdatasync DbDataSync
sudo /opt/dbdatasync/dbdatasync tool install
```

`tool install` links `/opt/dbdatasync/dbdatasync` from `/usr/local/bin/dbdatasync`.

To register the service:

```sh
sudo dbdatasync service install
dbdatasync config check
```

This runs as a dedicated `dbdatasync` system user by default (`--user` to override). It's enabled but
not started — start it with `sudo systemctl start dbdatasync`.

To update:

```sh
sudo dotnet tool update --tool-path /opt/dbdatasync DbDataSync
```

To remove:

```sh
sudo dbdatasync tool uninstall
sudo dotnet tool uninstall --tool-path /opt/dbdatasync DbDataSync
```

## Updating

`dbdatasync update` lists what can be installed, lets you choose, and prints the commands to install it:

```sh
dbdatasync update
```

```
Installed: 2026.9.16.1005

stable
  1) 2026.9.18.1918      built 2026-09-18 19:18 UTC  newer
  2) 2026.9.16.1005      built 2026-09-16 10:05 UTC  installed

beta
  3) 2026.9.12.721-beta  built 2026-09-12 07:21 UTC

snapshot
  4) 2026.9.19.1432-snapshot.g65615e7  built 2026-09-19 14:32 UTC  newer

Install which one? Type a number or a version (empty to cancel):
```

It **never changes your installation** — it prints the commands (stop the service, `dotnet tool
update`, start the service, check the version) and you run them. Use `dbdatasync update --list` to only
list, `--channel stable|beta|snapshot` to narrow it, `--json` for scripts, and `--to <version>` to skip
the prompt.

| channel | what it is | where it comes from |
| --- | --- | --- |
| `stable` | a release | nuget.org |
| `beta` | a prerelease (`-beta`) | nuget.org — a plain `dotnet tool install` never picks these; only an exact version does |
| `snapshot` | the newest build that has passed CI, before any release is cut | a GitHub prerelease for every commit promoted to the `test` branch; the newest 20 are kept |

A **snapshot** is downloaded for you into a per-user staging folder (`~/.local/share/DbDataSync/updates`,
`%LOCALAPPDATA%\DbDataSync\updates`; `--stage-dir` to change it), checked against the SHA-512 published
beside it, and the printed command installs from that folder — no token or private feed is involved.
That checksum catches a corrupted or truncated download, **not** a tampered release; a snapshot is a
development build, and its trust rests on TLS to `github.com` and who can publish to the repository.
Stable and beta go through `dotnet tool` from nuget.org as usual.

`dotnet tool update` will not go *down* to an older version, so choosing one prints an `uninstall`
followed by an `install` instead. If this copy was not installed as a dotnet tool (a development build)
or is a container image, there is nothing to update in place and it says so — for a container, pull a
newer image tag.

The release list is read from public APIs, anonymously; GitHub limits that to 60 requests an hour per
address. Set `GITHUB_TOKEN` (or `GH_TOKEN`) to raise it — it is never required.

## Running in a container

Use this for a self-contained deployment with no `PATH` or service to manage.

```sh
docker compose -f docker-compose.app.yml up
```

One volume, at `/var/lib/dbdatasync`, holds both the config repository and the state database.

## Next: Configuration

Once DbDataSync is installed, see [Configuration](configuration.md) for every flag and environment
variable it accepts.
