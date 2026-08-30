# DataSync CLI

```
dotnet tool install -g DataSync
datasync serve
```

Starts the API, the scheduler and the web console in one process, creating a git-backed config
repository under `%LOCALAPPDATA%/DataSync` (Windows) or `~/.local/share/DataSync` (Linux, macOS) on
first run. `--repo`, `--state-db` and `--url` override the defaults.

## As a Windows service

```
datasync service install    # from an elevated prompt
datasync service status
datasync service uninstall
```

`install` resolves the paths and prints them, because a service has no console to say "I could not
find my config repository" on. `--account` sets the service account, which matters: a connection
using integrated authentication connects **as that account**.

## In a container

```
docker compose -f docker-compose.app.yml up
```

One volume at `/var/lib/datasync` holds both the config repository and the state database, so a
backup of that directory is a backup of everything that is not the image.
