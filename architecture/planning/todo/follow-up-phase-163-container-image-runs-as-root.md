# The published container image runs as root

**Found** 2026-09-21, while verifying phase 163 (`architecture/implementation/todo/phase-163K-container-image-on-ghcr.md`). It was true before
the image was published; publishing it makes it something other people run.

## What is true

- The Dockerfile has no `USER`. The image I built reports `uid=0(root)`, `HOME=/root`, for both the default and the `runtime` variant.
- So `dbdatasync serve` runs as root inside the container. The container has no capabilities beyond Docker's defaults, but a
  vulnerability in the web console, a driver library or a query someone installs from the console would start as root, and the data
  volume is written as root.
- The startup log carries a related warning on every start: `Storing keys in a directory '/root/.aspnet/DataProtection-Keys' that may
  not be persisted outside of the container.` No code under `src/` calls DataProtection directly (checked with a search for
  `DataProtection`/`IDataProtector`/antiforgery), so what the framework encrypts with those keys, if anything the app relies on, is
  **not established** — sessions live in the state database, not in a protected cookie payload.

## Why it is not a one-line fix

- **Existing volumes are root-owned.** Anyone already running the image has `/var/lib/dbdatasync` owned by root. Switching to a
  non-root user without a step that fixes ownership would make their next start fail on the first write.
- **The default (SDK) variant writes in more places than the data root**: `LibraryInstaller` shells out to `dotnet publish`, which needs
  a writable `HOME`/`DOTNET_CLI_HOME` and NuGet cache. A non-root user needs those pointed somewhere it can write.
- **Ports.** 8080 needs no privilege, so that is not an obstacle.

## Options

- **A.** Add a `dbdatasync` user (fixed uid, e.g. 10001), `USER` it, set `HOME`/`DOTNET_CLI_HOME`/`NUGET_PACKAGES` to paths under the
  volume, and have an entrypoint step (`chown -R` when started as root, then drop privileges) so existing root-owned volumes keep
  working. Document the uid for people using bind mounts.
- **B.** As A, but no ownership step: a breaking change, called out in release notes, with a one-line `docker run --user 0` escape hatch.
- **C.** Leave it, say so in `docs/install.md`, and recommend `--user`/`--read-only`/`--cap-drop=ALL` in the run example.

## Open questions

- Does anything the app depends on read the DataProtection key ring (cookie authentication, passkey ceremonies, antiforgery)? If so,
  persisting it under the volume is a correctness fix, not just a tidy-up. If not, the warning can be silenced by configuring the key
  path explicitly.
- Does `DbDataSync.TaskRunner` (the spawned worker) or the DuckDB install at `serve` start assume it can write outside the data root?
