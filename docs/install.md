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

### Applying it for you

Add `--apply` and `dbdatasync update` carries the plan out instead of printing it — stop the service, install,
start it, and check it answers, putting the previous version back if it does not:

```sh
sudo dbdatasync update --to 2026.9.18.1918 --apply
```

It asks first (`--yes` to skip the prompt, which is required when there is no terminal). It needs the same rights
the printed commands would, which is why the example uses `sudo`. `--url` says where to check the service answers
(default: the configured `DbDataSync:Url`), and `--health-timeout` how many seconds to wait for it (default 90).
`dbdatasync update --status` shows what the last update did, and whether one is waiting or on trial. If an apply fails, its log is kept in a temporary directory whose path is printed.

**Windows** does not have `--apply` yet: a running `dbdatasync.exe` and its service hold their own files open, so it
needs a helper that outlives them, and that has not been verified on a real host. There, `update` prints the
commands and you run them.

### From the web console

Admin → **Updates** lists the releases and has an **Update** button on each. It is **off by default**, and it needs
three things — the first is a decision only root can make:

1. **A systemd unit that applies updates.** Linux, installed as a dotnet tool, running as a systemd service that was
   registered with `--self-update`:

   ```sh
   sudo dbdatasync service install --self-update
   sudo systemctl restart dbdatasync
   ```

   That adds a step which runs **as root, outside the service's own sandbox**, before every start, to apply an update
   the service asked for. It is opt-in, and is only ever added by running that command as root: the service runs with
   fewer rights on purpose, and a setting the service itself could write must not be what switches a
   root-privileged step on. Without it, an ordinary unit is unchanged and the console will say why it cannot update.
2. `DbDataSync:SelfUpdateEnabled` set to `true` (Admin → Configuration, or `dbdatasync.config.yaml`).
   `dbdatasync config check` warns when this is on and the unit was not registered with `--self-update`.
3. Only `stable` is offered unless you allow more: `DbDataSync:SelfUpdateChannels` (`stable`, `beta`, `snapshot`,
   comma-separated). A snapshot is a development build — its download is checked only against a checksum published
   beside it, which catches corruption, not tampering.

What pressing it does: the service stops starting new work (the scheduler pauses and changes over the API answer
`409`), waits for running work to finish — up to `SelfUpdateDrainTimeoutSeconds`, default 120; anything left is picked
up again after the restart — then exits with code 75, which its unit treats as a clean restart. Before the service
starts again, systemd runs `dbdatasync internal apply-update` to install the new version. The new version counts as
having worked once it has been serving for `SelfUpdateConfirmAfterSeconds` (default 60). **If it crashes or hangs
before then, the next start puts the previous version back** — from a copy of its package kept aside for the purpose —
with nothing watching it but the restart systemd does anyway.

**What the service can and cannot ask for.** The request the service leaves behind is a version and who asked, nothing
more. The privileged step looks that version up in the pinned release sources itself, works out which installation it
is from its own location, and downloads any package itself — so a compromised service cannot point an update at other
code, only ask for a genuine release. Its own records (what is on trial, the spare package, the log) are in
`/var/lib/dbdatasync-update/`, which the service cannot write; what the service writes, and the console shows, is under
`<data directory>/updates/`. A failed or rolled-back update says so on the page and names `update.log` in the root-only
directory.

The service is unreachable for a few seconds while it restarts, and the page keeps asking rather than reporting an
error. Signing in again is not needed: sessions live in the state database.

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
