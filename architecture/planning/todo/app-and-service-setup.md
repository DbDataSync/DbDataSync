# App and service setup, config, and readiness

**Status: proposal, not agreed.** Draft for review.

Scope: everything between "someone wants to run DbDataSync" and "a correctly-configured instance is
serving a console an operator can sign into." The person doing this is a deployer or platform
admin — not the operator who later builds replications (that is `operator-setup-and-config.md`).

This is the higher-priority half. Phase 109 (`nuget-loaded-drivers.md`) makes it more so: once
providers and drivers are NuGet packages installed into the deployment, "set up the app" grows a
step that does not exist today, and it should be designed rather than accreted.

---

## Recommendation up front

Four pieces, roughly in value order:

1. **`dbdatasync doctor`** — one command that inspects a deployment and says either "ready" or a
   numbered list of what is not, each with the exact fix. This is the single biggest lever: today
   every misconfiguration is discovered by hitting its failure.
2. **First-run output that a human actually sees.** The bootstrap admin invite is currently a
   `LogWarning` interleaved with Kestrel startup lines. Make `serve`'s first run print a framed
   "next steps" block, and write the invite to `<repo>/FIRST-RUN.txt` (removed when redeemed) so a
   service deployment has somewhere to look.
3. **`docs/getting-started.md`** — the happy path, once, per deployment shape (local, container,
   Windows service, Linux behind a proxy). Today it is scattered across the README (dev harness),
   the CLI README (two lines), and `CONFIG.md` (a reference, not a walkthrough).
4. **Provider/driver setup as a first-class part of `serve` and `doctor`** — `driver sync` on
   deploy, a clear failure when a configured engine's provider is missing, and the offline story.
   Design this alongside phase 109, not after.

---

## Current state

What a deployer works with today:

- **Acquisition** — `dotnet tool install -g DbDataSync` (a global tool, `src/DbDataSync.Cli`,
  `PackAsTool`), or the container image (`docker-compose.app.yml`, one volume at
  `/var/lib/dbdatasync`, port 8080), or `dbdatasync service install` on Windows.
- **`dbdatasync serve`** (`ServeCommand.cs`) — resolves the repo root (`DbDataSyncRoot.Resolve`:
  `--repo`, else walk up for `dbdatasync.config.yaml`, else `%LOCALAPPDATA%\DbDataSync`), `git init`s
  it on first run, commits a fully-commented starter `dbdatasync.config.yaml`, prints three lines
  (repo, state db, console URL), and runs. There is no `init` command — `serve` is the bootstrap.
- **Config** (`CONFIG.md`) — `dbdatasync.config.yaml` in the repo root, in the `appsettings.json`
  precedence slot; env vars and `--DbDataSync:Key` override it. Three entry points, **three default
  ports** (5183 / 5080 / 8080), which `CONFIG.md` itself calls the most common source of confusion.
- **State store** — SQLite by default (a file beside the repo, zero config). `StateEngine: MsSql` or
  `Postgres` needs `StateConnectionString` (credential-free) in the config file plus
  `dbdatasync secret set dbdatasync:config:stateConnectionString "Password=…"` — a verbatim magic
  ref string.
- **Auth** (`AuthOptions`, `PasskeyOptions`, `BootstrapInvite`) — closed by default. First `serve`
  with zero users mints an Admin bootstrap invite and `LogWarning`s `/invite#<code>`; it is revoked
  the moment a user exists and re-minted every restart until then. `dbdatasync invite` reprints one.
  Passkey relying-party defaults to `localhost`; a real hostname needs
  `DbDataSync:Auth:Passkeys:RelyingPartyId` + `Origins`, over HTTPS. `PasskeyOptions.Problem()` runs
  at startup and `LogWarning`s a mismatch. Windows: `AdminGroup` / `ViewerGroup`. Escape hatch:
  `DbDataSync:Auth:Disabled=true`.
- **TLS** — `dbdatasync cert …` issues/binds a certificate **on Windows only**. Linux has no
  in-product story and nothing documents the reverse-proxy alternative — which matters because
  passkeys need HTTPS anywhere but `localhost`.
- **Health** — `dbdatasync health` hits `GET /api/health` (no auth), exits 0/1; the container's
  `HEALTHCHECK` runs it.
- **Upgrades** — `dotnet tool update` / a new image. Schema migrations run on every startup
  (`StateDatabase.EnsureSchema`), so a state DB is brought current automatically; there is no
  pre-flight or rollback story written down.

### The gap phase 109 opens

After phase 109, a deployment has a `<repo>/providers/` and `<repo>/drivers/` tree of manifests, and
the DLLs are restored (not committed — Q2) by `dbdatasync provider|driver sync`. That means:

- A **fresh deployment pointed at an existing config repo** must `sync` before `serve`, or `serve`
  must do it. Undefined today.
- After phase 109g, a **SQL Server or Postgres state backend needs its provider installed** — a
  behaviour change for existing deployments, and exactly the kind of thing `doctor` should catch and
  `serve` should refuse clearly rather than fail with "assembly not found."
- **Air-gapped / offline** deployments need `--source <internal-feed>` and a documented flow.

---

## What to build

### 1. `dbdatasync doctor`

A read-only inspection, exit 0 if ready, 1 with a numbered report otherwise. Checks, each producing
either a green line or a problem with its fix:

- **Repo** — resolvable, a valid git repo, `dbdatasync.config.yaml` parses.
- **State store** — for SQLite, the file's directory is writable; for MsSql/Postgres, the provider
  is installed (post-109) and a connection opens with the spliced credential; the schema version is
  current or migratable.
- **Providers / drivers** (post-109) — every manifest's package closure is present (`sync` needed
  or not); every `driverType` referenced by a `connection.yaml` resolves to a registered driver.
- **Auth** — a method is configured (passkeys, Windows, or `Disabled` stated); `PasskeyOptions.Problem()`
  is null; the bound URL's host matches `RelyingPartyId`; if any URL is HTTPS the certificate is
  valid and not near expiry (reuse `CertificateOptions` / the `cert status` logic).
- **Binding** — the console URL is reachable from this host; a warning if it is HTTP and not
  loopback.
- **First admin** — whether a user exists; if not, print the current bootstrap invite here too.

`doctor` is also what the getting-started doc ends every section with ("run `dbdatasync doctor` — it
should say Ready"), and what a `service install` prints a reminder to run.

### 2. First-run experience

- `ServeCommand` detects the fresh-repo case it already has (`isFreshRepo`) and, instead of three
  bare lines, prints a framed block: the console URL, the state store in use, and — when there are
  no users — the invite URL and its expiry, verbatim, not via the logger.
- `BootstrapInvite` (or a small collaborator) writes the same invite to `<repo>/FIRST-RUN.txt` with
  a one-paragraph explanation, and deletes it in the same place it currently calls
  `DeleteBootstrapInvites()` (the moment a user exists). A service with no console then has a file.
- The invite log line moves from `LogWarning` to `LogInformation` with a box, or stays a warning but
  the file + console block make it non-load-bearing.

### 3. `docs/getting-started.md`

One page, four short sections — **evaluate locally**, **run in a container**, **Windows service**,
**Linux behind a reverse proxy** — each a linear list ending in `dbdatasync doctor` → sign in. It
links to `CONFIG.md` for the full surface and does not duplicate it. The reverse-proxy section is
new material: an nginx/Caddy snippet terminating TLS, forwarding `/`, `/api`, `/hubs` (SignalR needs
the upgrade headers), and the `Passkeys:RelyingPartyId` / `Origins` / `DbDataSync:Url` values that
must then match the public hostname.

### 4. Provider/driver setup, designed with phase 109

- `serve` runs `provider|driver sync` (or refuses with the exact command) when a manifest's closure
  is absent — decided in phase 109c, but the *experience* is this doc's: it should be one message,
  not a stack trace.
- The default config-repo template (whatever seeds a fresh `serve`) includes `provider.json`
  manifests for `Microsoft.Data.SqlClient` and `Npgsql` pinned to tested versions, so the built-in
  state backends keep working with zero manual steps after 109g.
- A `--source` flag on `sync` and a short "air-gapped install" section in getting-started: restore
  once against an internal feed or a folder of `.nupkg`s, commit the closure (the Q2 opt-in), deploy.
- `doctor` covers the "provider missing / out of sync" cases named above.

### 5. Windows service and upgrades — smaller items

- `service install`'s output already prints paths; add "run `dbdatasync doctor` next" and, if TLS
  is wanted, the `cert` steps.
- Document the upgrade flow: stop the service / container, `dotnet tool update` or repull, start —
  migrations self-apply; keep a state-DB backup first. A `--dry-run` on migration (log what would
  run, apply nothing) is worth considering for MsSql/Postgres state backends.
- **Backups**: say plainly what to back up — the config repo (git, so also pushable to a remote) and
  the state DB (file, or the server database). For the container it is one volume.

---

## What this does not do

- The operator's journey — connections, replications, the first table mapping, in-app hints. That is
  `operator-setup-and-config.md`.
- Re-specifying the phase 109 `provider` / `driver` CLI — that is phases 109c–109e. This doc only
  owns the setup *experience* around them.
- High availability, multi-instance, or externalising the config repo to a remote as the primary —
  separate questions.
- A GUI installer.

---

## Open questions

1. **Is `doctor` a new command or a mode of `serve`** (`serve --check`)? Leaning: its own command,
   so a service wrapper and a CI smoke test can call it without starting Kestrel.
2. **Should `serve` block on `doctor` failing**, or start degraded and surface problems in the admin
   UI? Leaning: block only on the things that make it genuinely non-functional (no auth method and
   not `Disabled`; state store unreachable); warn-and-continue on the rest.
3. **Does the first-run invite file belong in the git-tracked repo root** (it would be committed) or
   a sibling non-repo path? Leaning: repo root but `.gitignore`d by the starter `.gitignore` the
   bootstrap writes.
4. **Container first-run** has no interactive console at all — is `FIRST-RUN.txt` in the volume
   enough, or does the image need a documented `docker logs | grep invite` / `docker exec …
   dbdatasync invite`? Both, probably; getting-started should show the `docker exec` form.
5. How much of `doctor` can reuse existing code (`PasskeyOptions.Problem`, `cert status`,
   `StateDatabase` version read, the connection factory) vs. needs new probes.
