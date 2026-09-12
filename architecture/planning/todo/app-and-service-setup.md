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

1. **`dbdatasync setup`** — an interactive command that is the recommended first thing after install.
   On an existing deployment it reviews the configuration and lets you change any part of it; on a
   fresh one it walks the config settings for your platform — root folder, console URL, state
   database, common drivers, authentication, and on Windows the service registration and TLS
   certificate. It writes nothing you cannot also write by hand; it is a guided front over the
   config file and the existing subcommands.
2. **`dbdatasync config check`** (implemented as `doctor` in phase 110; folded under `config` by
   phase 115) — the non-interactive core of `setup`'s review: the same checks, one numbered "ready /
   here is what is wrong" report, exit 0/1, for a CI smoke test or a service wrapper. `setup`'s
   review screen is `config check` rendered for a human with "fix this now" actions attached.
3. **First-run output that a human actually sees.** The bootstrap admin invite is currently a
   `LogWarning` interleaved with Kestrel startup lines. Make `serve`'s first run print a framed
   "next steps" block, and write the invite to `<repo>/FIRST-RUN.txt` (removed when redeemed) so a
   service or container deployment has somewhere to look.
4. **`docs/getting-started.md`** — the happy path, once, per deployment shape (local, container,
   Windows service, Linux behind a proxy). Points at `dbdatasync setup` for the interactive path and
   at `docs/configuration.md` for the full surface; does not duplicate either.
5. **Provider/driver setup wired into `setup`, `serve` and `config check`** — `config driver sync` on deploy, a
   clear failure when a configured engine's provider is missing, and the offline story. Design this
   alongside phase 109, not after.

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
- **Config** (`docs/configuration.md`) — `dbdatasync.config.yaml` in the repo root, in the `appsettings.json`
  precedence slot; env vars and `--DbDataSync:Key` override it. Three entry points, **three default
  ports** (5183 / 5080 / 8080), which `docs/configuration.md` itself calls the most common source of confusion.
- **State store** — SQLite by default (a file beside the repo, zero config). `StateEngine: MsSql` or
  `Postgres` needs `StateConnectionString` (credential-free) in the config file plus
  `dbdatasync config secret set dbdatasync:config:stateConnectionString "Password=…"` — a verbatim
  magic ref string.
- **Auth** (`AuthOptions`, `PasskeyOptions`, `BootstrapInvite`) — closed by default. First `serve`
  with zero users mints an Admin bootstrap invite and `LogWarning`s `/invite#<code>`; it is revoked
  the moment a user exists and re-minted every restart until then. `dbdatasync invite` reprints one.
  Passkey relying-party defaults to `localhost`; a real hostname needs
  `DbDataSync:Auth:Passkeys:RelyingPartyId` + `Origins`, over HTTPS. `PasskeyOptions.Problem()` runs
  at startup and `LogWarning`s a mismatch. Windows: `AdminGroup` / `ViewerGroup`. Escape hatch:
  `DbDataSync:Auth:Disabled=true`.
- **TLS** — `dbdatasync config cert …` issues/binds a certificate **on Windows only**. Linux has no
  in-product story and nothing documents the reverse-proxy alternative — which matters because
  passkeys need HTTPS anywhere but `localhost`.
- **Health** — `dbdatasync health` hits `GET /api/health` (no auth), exits 0/1; the container's
  `HEALTHCHECK` runs it.
- **Upgrades** — `dotnet tool update` / a new image. Schema migrations run on every startup
  (`StateDatabase.EnsureSchema`), so a state DB is brought current automatically; there is no
  pre-flight or rollback story written down.

### The gap phase 109 opens

After phase 109, a deployment has a `<repo>/providers/` and `<repo>/drivers/` tree of manifests, and
the DLLs are restored (not committed — Q2) by `dbdatasync config provider|driver sync`. That means:

- A **fresh deployment pointed at an existing config repo** must `sync` before `serve`, or `serve`
  must do it. Undefined today.
- After phase 109g, a **SQL Server or Postgres state backend needs its provider installed** — a
  behaviour change for existing deployments, and exactly the kind of thing `config check` should
  catch and `serve` should refuse clearly rather than fail with "assembly not found."
- **Air-gapped / offline** deployments need `--source <internal-feed>` and a documented flow.

---

## What to build

### 1. `dbdatasync setup`

An interactive command, dispatched from `Program.cs` alongside `serve` / `service` / `config`. It is
what `getting-started.md` tells a first-time deployer to run, and what an existing deployment runs to
check or change its configuration. Everything it does is also doable by editing
`dbdatasync.config.yaml` and running the individual subcommands — `setup` is a guided front over
them, never a second source of truth.

**Requires a TTY.** When input is redirected or there is no interactive console
(`Console.IsInputRedirected`), it prints *"setup is interactive — run `dbdatasync config check` to
check a configuration, or edit `dbdatasync.config.yaml` (see docs/configuration.md)"* and exits non-zero. There
is no `--non-interactive` mode; that role belongs to the config file.

**Superseded by phase 128.** This originally specified a small internal `Prompt` helper over
`Console.ReadLine`/`ReadKey`, keeping the CLI project's package-reference count at zero. Phase 128
deliberately reversed that: `setup` is now a Terminal.Gui TUI (`Terminal.Gui` 2.5.0, the CLI project's
first real package dependency), a tabbed form with a live readiness-checks sidebar rather than a
sequential prompt flow — see the phase doc once written.

#### Step 0 — find or choose the root

- Resolve a candidate the way every command does (`DbDataSyncRoot.Resolve`: `--repo`, else walk up
  for `dbdatasync.config.yaml`, else the per-user default).
- **An existing setup is at the candidate** — a `dbdatasync.config.yaml` with uncommented keys, a
  git history beyond the starter commit, or a state DB present → go straight to the **review
  screen**.
- **Nothing at the candidate** → ask which folder to use, offering the platform default as the
  pre-filled answer (`%LOCALAPPDATA%\DbDataSync` / `~/.local/share/DbDataSync` /
  `/var/lib/dbdatasync`). Then:
  - **the chosen folder is already configured** → say so plainly (*"That folder already has a
    DbDataSync configuration."*) and go to the **review screen** for it;
  - **the chosen folder is fresh** → run the **walk-through**.

#### The review screen (existing setup)

The human form of `config check` (§2) — the same checks, each line ✓ or ✗-with-fix: resolved root ·
console URL · state engine, reachability, schema version · auth method + passkey RP status ·
certificate status (Windows) · installed providers/drivers, and whether a `sync` is pending ·
whether a first admin exists.

Then a menu:

- **Reconfigure a section** — state database · authentication · drivers · (Windows) service ·
  (Windows) certificate. Each re-runs that part of the walk-through against the current values.
- **Print the effective configuration** — the merged view (file + env + defaults), secrets masked.
- **Reissue the first-run invite** — offered only while no admin exists.
- **Start DbDataSync** — `serve` in the foreground, or start the registered service.
- **Exit.**

A section the user does not open is not rewritten — the same rule `ServeCommand.Prepare` follows for
a repo root that already existed.

#### The walk-through (fresh folder)

Ordered, every step defaulted so `Enter` accepts:

1. **Repo root** — confirm the folder; `git init` and write the starter config by calling
   `ServeCommand.Prepare` (share the code).
2. **Console URL** — bind address and port, default `http://localhost:5080`. An option "reachable at
   a hostname" prompts the hostname, switches the scheme to `https`, and carries it into steps 5 and 7.
3. **State database** —
   - **SQLite** (default) — a path beside the repo; nothing else.
   - **SQL Server / PostgreSQL** — prompt the credential-free connection string, then the password
     (masked) → store it via the `config secret set dbdatasync:config:stateConnectionString` path.
     Offer to `config provider install` the matching ADO.NET provider (required after phase 109g;
     skipped while it is still a hard reference). Open a test connection before continuing.
4. **Common drivers** — *"Which database engines will you replicate?"* Multi-choice: SQL Server,
   PostgreSQL, DuckDB (built-in — no action), MySQL / MariaDB, Oracle, "something else". For each
   non-built-in choice, run `config provider install` for the known package and drop a `driver.yaml`
   from a template (`config driver install --from <engine>`, once phase 109d lands). Until then this
   step is informational.
5. **Authentication** —
   - **Passkeys** (default) — confirm the relying-party id: `localhost` for a local install, else
     the step-2 hostname. Set `RelyingPartyId` / `Origins`, require the console URL to be HTTPS, run
     `PasskeyOptions.Problem()` and refuse to finish on a mismatch.
   - **Windows groups** — prompt the admin and viewer group names.
   - **No authentication** — a hard confirm (*"DbDataSync will accept every request. Type ALLOW to
     continue."*) before setting `Disabled=true`.
6. **Windows only — service** — *"Register DbDataSync as a Windows service so it starts with the
   machine?"* → the `service install` path. Prompt the service account (LocalSystem, or a domain
   account — note that an integrated-auth DB connection connects **as that account**). If `setup` is
   not elevated, finish everything else and print the single `service install` command to run from an
   elevated prompt rather than relaunching through UAC mid-session.
7. **Windows only — certificate** — *"Set up the TLS certificate Kestrel serves?"* → the
   `config cert` path: a self-signed certificate for a quick start, or enroll from a template / bind
   an existing thumbprint. Bind it to the console URL's port.
8. **Write and finish** — commit `dbdatasync.config.yaml`; confirm the stored secret, the installed
   providers, the service, the certificate. Print the first-run invite URL, noting it is also in
   `<repo>/FIRST-RUN.txt`. Offer to **start DbDataSync now**.

#### Platform awareness

The **root default** and steps **6–7** are the platform branches. On non-Windows, 6–7 are replaced
by a one-paragraph pointer to the reverse-proxy section of `getting-started.md`. Inside the container
image `setup` is not the path at all — the image is configured by environment and the mounted
volume, and `getting-started.md`'s container section says so.

### 2. `dbdatasync config check` — the checks `setup` and CI share

A read-only inspection, exit 0 if ready, 1 with a numbered report otherwise. Each check is a green
line or a problem with its exact fix:

- **Repo** — resolvable, a valid git repo, `dbdatasync.config.yaml` parses.
- **State store** — for SQLite, the file's directory is writable; for MsSql/Postgres, the provider
  is installed (post-109) and a connection opens with the spliced credential; the schema version is
  current or migratable.
- **Providers / drivers** (post-109) — every manifest's package closure is present (`sync` needed
  or not); every `driverType` referenced by a `connection.yaml` resolves to a registered driver.
- **Auth** — a method is configured (passkeys, Windows, or `Disabled` stated); `PasskeyOptions.Problem()`
  is null; the bound URL's host matches `RelyingPartyId`; if any URL is HTTPS the certificate is
  valid and not near expiry (reuse `CertificateOptions` / the `config cert status` logic).
- **Binding** — the console URL is reachable from this host; a warning if it is HTTP and not
  loopback.
- **First admin** — whether a user exists; if not, print the current bootstrap invite.

`config check` is what `getting-started.md` ends every section with, what `service install` prints a
reminder to run, and the check engine `setup`'s review screen renders interactively.

### 3. First-run experience

- `ServeCommand` detects the fresh-repo case it already has (`isFreshRepo`) and, instead of three
  bare lines, prints a framed block: the console URL, the state store in use, and — when there are
  no users — the invite URL and its expiry, verbatim, not via the logger. (A deployer who ran
  `dbdatasync setup` has already seen this once; `serve` reprinting it is for the direct path.)
- `BootstrapInvite` (or a small collaborator) writes the same invite to `<repo>/FIRST-RUN.txt` with
  a one-paragraph explanation, and deletes it in the same place it currently calls
  `DeleteBootstrapInvites()` (the moment a user exists). A service or container with no console then
  has a file.
- The invite log line moves from `LogWarning` to `LogInformation` with a box, or stays a warning but
  the file + console block make it non-load-bearing.

### 4. `docs/getting-started.md`

One page, four short sections — **evaluate locally**, **run in a container**, **Windows service**,
**Linux behind a reverse proxy**. The local and Windows-service sections are two lines each: install,
then `dbdatasync setup`. The container section is env + volume (setup does not apply). The
reverse-proxy section is new material `setup` points at: an nginx/Caddy snippet terminating TLS,
forwarding `/`, `/api`, `/hubs` (SignalR needs the upgrade headers), and the
`Passkeys:RelyingPartyId` / `Origins` / `DbDataSync:Url` values that must match the public hostname.
Every section ends in `dbdatasync config check` → sign in. Links to `docs/configuration.md` for the full surface; does
not duplicate it.

### 5. Provider/driver setup, designed with phase 109

- `serve` runs `provider|driver sync` (or refuses with the exact command) when a manifest's closure
  is absent — decided in phase 109c, but the *experience* is this doc's: it should be one message,
  not a stack trace.
- The default config-repo template (whatever seeds a fresh `serve`) includes `provider.json`
  manifests for `Microsoft.Data.SqlClient` and `Npgsql` pinned to tested versions, so the built-in
  state backends keep working with zero manual steps after 109g.
- A `--source` flag on `sync` and a short "air-gapped install" section in getting-started: restore
  once against an internal feed or a folder of `.nupkg`s, commit the closure (the Q2 opt-in), deploy.
- `config check` covers the "provider missing / out of sync" cases named above; `setup`'s drivers step
  installs them in the first place.

### 6. Windows service and upgrades — smaller items

- `setup` offers the service registration and cert; `service install` run directly still prints its
  paths, and gains a "run `dbdatasync config check` next" line.
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
- A GUI installer. `setup` is a terminal *TUI* since phase 128 (Terminal.Gui) — a real tabbed form and
  widgets, not a bare prompt flow any more, but still not a windowed/graphical installer.
- `setup` does not run the server — it configures, then offers to hand off to `serve` or the service.

---

## Open questions

1. **`setup` vs. the existing bootstrap.** `serve` already does `Prepare` (git init + starter
   config) and `service install` already prompts for nothing. Does `setup` supersede the
   `serve`-first-run path entirely, or coexist? Leaning: coexist — `serve` stays the "I know what I'm
   doing / I'm scripted" path, `setup` is what getting-started recommends and what the review screen
   needs to exist for.
2. **"Already configured" detection.** A fresh `serve` writes a fully-commented
   `dbdatasync.config.yaml` and a starter commit, so file-exists and git-history are both true on a
   never-really-used root. The signal has to be *uncommented keys*, or a state DB, or > 1 commit.
   Pick one and make it a single testable predicate.
3. **How far does `setup`'s "reconfigure a section" go?** Changing the passkey RP id or the state
   engine on a live deployment has consequences (existing passkeys stop working; state does not
   migrate between engines). Leaning: `setup` makes the config change and prints the consequence
   loudly ("existing passkeys will not work against the new hostname — re-enrol"), rather than
   trying to migrate anything.
4. **Elevation on Windows.** Detect non-elevated up front and defer steps 6–7 with printed
   commands, or offer to relaunch elevated? Leaning: defer and print — a UAC relaunch of an
   interactive session mid-flow loses the console state.
5. **Is the check engine a separate command or `serve --check`?** Resolved (phase 110, revised by
   phase 115): separate — `dbdatasync config check` — so CI and a service wrapper call it without
   starting Kestrel. `setup`'s review screen calls the same check code in-process. (Originally built
   as its own top-level `doctor` command in phase 110; phase 115 nested it under `config` alongside
   `cert`/`secret`/`provider`/`driver` to trim the top-level command count.)
6. **Should `serve` block on `config check` failing**, or start degraded and surface problems in the admin
   UI? Leaning: block only on genuinely non-functional states (no auth method and not `Disabled`;
   state store unreachable); warn-and-continue on the rest.
7. **First-run invite file** — git-tracked repo root (committed) or a sibling non-repo path? Leaning:
   repo root, `.gitignore`d by a starter `.gitignore` the bootstrap writes.
8. **Container first-run** has no interactive console — is `FIRST-RUN.txt` in the volume enough, or
   does getting-started also show `docker exec … dbdatasync invite`? Both, probably.
9. How much of the check engine reuses existing code (`PasskeyOptions.Problem`, `config cert status`,
   `StateDatabase` version read, the connection factory) vs. needs new probes.
