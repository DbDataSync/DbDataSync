# DbDataSync CLI

```
dotnet tool install -g DbDataSync
dbdatasync serve
```

Starts the API, the scheduler and the web console in one process, creating a git-backed config
repository under `%ProgramData%\DbDataSync` (Windows) or `/var/lib/dbdatasync` (Linux; `/Library/
Application Support/DbDataSync` on macOS) on first run. `--repo`, `--state-db` and `--url` override the
defaults.

`-g`/`--global` above installs into *your own* profile — fine for trying `dbdatasync` out, but not for
a service (see below) or a deployment more than one person uses. See **`docs/install.md`** for a real
machine-wide install (`dotnet tool install --tool-path` plus `dbdatasync tool install`, which puts
`dbdatasync` on the machine's `PATH` for everyone).

## As a Windows service

```
dbdatasync service install    # from an elevated prompt
dbdatasync service status
dbdatasync service uninstall
```

`install` resolves the paths and prints them, because a service has no console to say "I could not
find my config repository" on. `--account` sets the service account, which matters: a connection
using integrated authentication connects **as that account**. `install` also warns if this tool is
still installed in a user profile — see `docs/install.md` for why a service should point at a
machine-wide install instead.

## As a Linux systemd service

```
sudo dbdatasync service install    # needs root
dbdatasync service status
sudo dbdatasync service uninstall
```

Runs as a dedicated system user (`dbdatasync` by default; `--user` overrides), enabled but not started
— `sudo systemctl start dbdatasync` next. Refuses outright (not just a warning) if this tool is still
installed in a user profile and `--repo` is the default machine-wide root: the unit's own hardening
(`ProtectHome=yes`) would hide a user-profile executable from the service entirely, so it would install
cleanly and then fail to start. `docs/install.md` has the machine-wide install sequence this points you
at.

## Machine-wide install (`dbdatasync tool install`/`uninstall`)

```
dotnet tool install --tool-path /opt/dbdatasync DbDataSync   # or --tool-path "$env:ProgramFiles\DbDataSync" on Windows
sudo /opt/dbdatasync/dbdatasync tool install
```

Puts a `--tool-path` install on the machine's `PATH` — a `/usr/local/bin` symlink on Linux, an
`/etc/paths.d` entry on macOS, the Machine `PATH` on Windows. Needs root/an elevated prompt; see
**`docs/install.md`** for the full sequence, both platforms, and what to run to update or remove it.

## In a container

```
docker compose -f docker-compose.app.yml up
```

One volume at `/var/lib/dbdatasync` holds both the config repository and the state database, so a
backup of that directory is a backup of everything that is not the image. There is no `PATH` question
in a container — it runs an absolute `dotnet /app/...`, so `tool install` has nothing to do here.

The default image ships the .NET SDK so `POST /api/libraries`/`config library install` can restore any
library on the fly. `docker build --target runtime -t dbdatasync:<v>-runtime .` builds a smaller,
SDK-less alternative for a deployment that only ever installs the seven bundled `KnownLibraries`
catalog entries — those still install with no SDK and no network, copied from a cache baked into the
image at build time; anything else is written and left "pending restore" (see the Libraries admin
screen) until `config library sync` runs somewhere with the SDK.
