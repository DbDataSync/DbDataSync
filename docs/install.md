# Installing dbdatasync

[DbDataSync](../README.md) · **Install** · [Getting started](getting-started.md) · [Configuration](../CONFIG.md) · [Building from source](development.md)

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

## Running in a container

Use this for a self-contained deployment with no `PATH` or service to manage.

```sh
docker compose -f docker-compose.app.yml up
```

One volume, at `/var/lib/dbdatasync`, holds both the config repository and the state database.

## Next: Configuration

Once DbDataSync is installed, see [Configuration](../CONFIG.md) for every flag and environment
variable it accepts.
