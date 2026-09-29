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

`service install` also sets up versioned slots in that directory (see [Updating](#updating)), and says so. To
update, from an elevated prompt:

```powershell
dbdatasync update --to <version> --apply
```

Once the slots exist, don't run `dotnet tool update --tool-path` on this directory.

To remove:

```powershell
dbdatasync tool uninstall
Remove-Item -Recurse "$env:ProgramFiles\DbDataSync"
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

`service install` also sets up versioned slots in that directory (see [Updating](#updating)), and says so. To
update:

```sh
sudo dbdatasync update --to <version> --apply
```

Once the slots exist, don't run `dotnet tool update --tool-path` on this directory.

To remove:

```sh
sudo dbdatasync tool uninstall
sudo rm -r /opt/dbdatasync
```

## Updating

`dbdatasync update` lists what can be installed and lets you choose. With `--apply` it installs the version you
choose and switches to it ([below](#applying-it)). Without `--apply` it only prints what to do:

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

Without `--apply` it changes nothing. Use `dbdatasync update --list` to only list, `--channel stable|beta|snapshot`
to narrow it, `--json` for scripts, and `--to <version>` to skip the prompt.

| channel | what it is | where it comes from |
| --- | --- | --- |
| `stable` | a release | nuget.org |
| `beta` | a prerelease (`-beta`) | nuget.org — a plain `dotnet tool install` never picks these; only an exact version does |
| `snapshot` | the newest build that has passed CI, before any release is cut | a GitHub prerelease for every commit promoted to the `test` branch; the newest 20 are kept |

A **snapshot** is downloaded for you into a per-user staging folder (`~/.local/share/DbDataSync/updates`,
`%LOCALAPPDATA%\DbDataSync\updates`; `--stage-dir` to change it), checked against the SHA-512 published
beside it, and installed from that folder — no token or private feed is involved.
That checksum catches a corrupted or truncated download, **not** a tampered release; a snapshot is a
development build, and its trust rests on TLS to `github.com` and who can publish to the repository.
Stable and beta go through `dotnet tool` from nuget.org as usual.

If this copy was not installed as a dotnet tool (a development build) or is a container image, there is nothing to
update in place and it says so. For a container, pull a newer image tag.

The release list is read from public APIs, anonymously; GitHub limits that to 60 requests an hour per
address. Set `GITHUB_TOKEN` (or `GH_TOKEN`) to raise it — it is never required.

### Applying it

```sh
sudo dbdatasync update --to 2026.9.18.1918 --apply
```

On Windows, run the same command without `sudo` from an elevated prompt.

A machine-wide install keeps **two versions side by side**, in two slots. The service and `PATH` run a small
launcher, which runs whichever slot `current.txt` names. In `/opt/dbdatasync/` (Windows:
`C:\Program Files\DbDataSync\`):

- the launcher — the file named `dbdatasync` (`dbdatasync.exe`), which the service and `PATH` run
- `current.txt` — `a` or `b`
- `versions/a/` and `versions/b/` — one dotnet tool install each

`--apply` does this:

1. Installs the version into the slot that is **not** running. Nothing in use is touched, which is what makes this
   work on Windows.
2. Stops the service.
3. Switches `current.txt`.
4. Starts the service, and checks it answers.

If the service does not answer, `--apply` switches back and starts the previous version again. There is nothing to
reinstall. The previous version stays in the other slot, and this command switches back to it later:

```sh
sudo dbdatasync update --rollback
```

It asks first (`--yes` skips the prompt, and is required when there is no terminal). `--url` says where to check the
service answers (default: the configured `DbDataSync:App:Url`). `--health-timeout` says how many seconds to wait
(default 90). `dbdatasync update --status` shows both slots, what the last update did, and the history. Each run's
commands and output are logged to `<data directory>/updates/update.log`.

**An install from before versioned slots** converts itself the first time you run `--apply` or `service install`, and
says so. The running version becomes slot `a`, and the launcher replaces the tool's own `dbdatasync` at the same path,
so the service and `PATH` need no change. The old `.store` is removed on a later run. After that, update with
`dbdatasync update`, not `dotnet tool update --tool-path`, which would write over the launcher.

A **global tool** (`dotnet tool install -g`) cannot keep two versions side by side. There, `update` prints the
`dotnet tool` commands to run instead.

The launcher itself does not change when you update. `dbdatasync launcher repair` replaces it with the running
version's copy and re-points a registered service at it; that is rarely needed.

### From the web console

Admin → **Updates** shows the running version and what each slot holds. It gives the commands above for this server,
with its data directory filled in, each with a copy button. Each listed release has a **Commands…** button with that
version's exact `--apply` command. The console does not apply updates itself: run the command on the server.

While an update runs from a shell, the page shows its progress. The service is unreachable for a few seconds while it
switches; the page keeps asking rather than reporting an error, and you don't have to sign in again (sessions live in
the state database).

Listing releases on the page calls nuget.org and GitHub, so it is **off by default**. Set `DbDataSync:Updates:Mode` to
`manual` (Admin → Configuration, or `dbdatasync.config.yaml`) to turn it on. The commands are shown either way. Only
`stable` is listed unless you allow more with `DbDataSync:Updates:Channels` (`stable`, `beta`, `snapshot`,
comma-separated). A snapshot is a development build: its download is checked only against a checksum published beside
it, which catches corruption, not tampering.

## Running in a container

Use this for a self-contained deployment with no `PATH` or service to manage.

```sh
docker run -p 8080:8080 -v dbdatasync-data:/var/lib/dbdatasync ghcr.io/dbdatasync/dbdatasync:latest
```

One volume, at `/var/lib/dbdatasync`, holds both the config repository and the state database. Images are published for
**linux/amd64 and linux/arm64** (an Arm server, a Raspberry Pi 4 or 5, AWS Graviton); Docker picks the one that matches the
machine.

| tag | what it is |
| --- | --- |
| `latest`, `2026.9.20.2152` | the default image. It includes the .NET SDK, which the console needs to install a library that is not in the bundled catalog |
| `runtime`, `2026.9.20.2152-runtime` | the smaller image, with no SDK: catalog drivers only, and a library outside the catalog is written down and left "pending restore" until `config library sync` runs somewhere that has an SDK |

`latest` and `runtime` follow the newest **stable** release. A beta gets only its exact version tags (`…-beta`,
`…-beta-runtime`), so pulling `latest` never gives you a prerelease. To stay put on a version, use its tag.

There is nothing to update in place: `dbdatasync update` inside a container says so. Pull a newer tag and start the container
again — the volume keeps your data. The image reports the version it was built for: `docker run --rm --entrypoint dotnet <image> /app/DbDataSync.Cli.dll version`.

To build it yourself from a checkout instead, `docker compose -f docker-compose.app.yml up` builds the same Dockerfile.

## Next: Configuration

Once DbDataSync is installed, see [Configuration](configuration.md) for every flag and environment
variable it accepts.
