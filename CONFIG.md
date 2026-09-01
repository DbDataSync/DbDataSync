# Configuration

Every CLI flag and environment variable that configures a running DataSync, across every way it can
be started. There are two separate entry points into the same host, with different defaults — that
distinction matters more than any individual setting below, so it's worth reading first.

## Two entry points, one host

- **`dotnet run --project src/DataSync.Api`** — the raw ASP.NET Core project. This is the dev-loop
  path in the main [README](README.md), and the only one it currently documents.
- **`datasync`** — a dotnet global tool (`src/DataSync.Cli`, `PackAsTool=true`,
  `ToolCommandName=datasync`). This is the actual product distribution: `datasync serve`,
  `datasync service install|uninstall|status`, `datasync cert ...`, `datasync invite`,
  `datasync health`, `datasync version`. It's also what the Docker image runs.

Both ultimately call the same `DataSyncHost.Build(args)` composition root
(`src/DataSync.Api/DataSyncHost.cs`), so a setting documented under "DataSync.Api process config"
below applies no matter which entry point launched it — the CLI just supplies its own flags and
defaults, then forwards them as the same `DataSync:*` configuration keys.

**The three entry points default to three different ports** — the most common source of "why can't
the SPA reach the API" confusion:

| how it's started | default port |
| --- | --- |
| `dotnet run --project src/DataSync.Api` (dev loop) | `5183` (`http`), plus `7186` (`https`) |
| `datasync serve` | `5080` |
| the Docker image | `8080` |

The SPA's dev-server proxy (`DATASYNC_API_URL`, below) defaults to `5183` — i.e. it assumes the raw
`dotnet run` dev loop, not a CLI-launched backend. Point it at `5080` if you're running against
`datasync serve` instead.

## `datasync` CLI (`src/DataSync.Cli`)

Parsing is a simple `--flag value` scan (`CliOptions.Read`/`Has`) — no positional magic, no `=`
syntax. Run `datasync --help` or any subcommand's `--help` for the same text.

### Repo root resolution (`serve`, `invite`, `health`)

These three commands resolve their repo root the same way, via a shared resolver
(`DataSyncRoot.Resolve`, phase 79):

1. An explicit `--repo <path>` wins outright.
2. Otherwise, walk upward from the current directory — the same shape `git` itself uses to find
   `.git` — looking for `datasync.config.yaml` at each parent in turn.
3. Otherwise, fall back to `%LOCALAPPDATA%\DataSync` (Windows) / the OS-equivalent local-app-data
   dir (`CliOptions.DefaultRoot`), unchanged from before this walk-up existed.

`datasync service install` does **not** use this resolver — its `--repo` (and `--url`) keep working
exactly as before, baked into the registered service's `binPath` at install time; a service that
should read from a `datasync.config.yaml` gets there because `serve` itself resolves one at each
start, not because `install` searched for one.

### `datasync.config.yaml`

A git-tracked file at `<RepoRoot>/datasync.config.yaml` (phase 79) — the same `DataSync:*` settings
this document describes, in the format ASP.NET Core's own `appsettings.json` uses conceptually but
written as YAML:

```yaml
DataSync:
  Url: http://localhost:5080
  StateEngine: MsSql
  StateConnectionString: "Server=sql01;Database=DataSyncState;"
```

It sits in the *same* precedence slot `appsettings.json` occupies — behind environment variables and
`--DataSync:Key`/CLI arguments, so either still overrides the file for a one-off run, and ahead of
nothing but the defaults baked into code. `serve --repo <fresh-root>` writes a starter copy the first
time it `git init`s a repo root (never touching one that already exists) with every key present but
commented out, as a reference rather than a default silently taking effect.

**No credential is ever written into this file.** `StateConnectionString` is validated the same way
`ConnectionConfig.ConnectionString` already is (`ConfigValidation.RejectEmbeddedCredential`) — a
`password`/`pwd`/`passwd`/`secret`/`accountkey`/`apikey` key in the string is rejected outright. The
password for a non-SQLite state store goes through the secret store instead, under the one fixed ref
`datasync:config:stateConnectionString` (see "Secrets," below) — the starter file's own comment names
this, and it and the connection string are spliced together at connect time.

### `datasync serve`

Starts the API, the scheduler and the web console in one process.

| flag | default |
| --- | --- |
| `--repo <path>` | see "Repo root resolution," above |
| `--state-db <path>` | `<repo>/state.db` |
| `--url <url>` | `--url` flag, else `DataSync__Url` env var, else the resolved config's `DataSync:Url`, else `http://localhost:5080` |

`--repo` git-inits the config repository if it isn't one already. Internally the resolved values
become `--DataSync:RepoRoot`, `--DataSync:StateDbPath` and `--urls`, so it's the exact same
configuration surface as running the API directly (see below) — just with CLI-flavored names and
defaults.

### `datasync service install|uninstall|status`

Windows-only; `install` needs an elevated prompt.

| flag | applies to | default |
| --- | --- | --- |
| `--repo <path>` | `install` | `%LOCALAPPDATA%\DataSync` — **not** the walk-up resolver; see above |
| `--url <url>` | `install` | `http://localhost:5080` |
| `--account <account>` | `install` | `LocalSystem` |

Registers the tool's own installed executable via `sc.exe create`, with `serve --repo ... --url ...`
baked into `binPath`. **A connection using integrated authentication connects as this service
account** — worth deciding `--account` deliberately rather than accepting `LocalSystem`.

### `datasync cert status|list|new-self-signed|enroll|renew|retrieve|templates|bind`

Windows-only (phase 82) — issues, installs, binds, renews and reports on the certificate Kestrel serves
TLS with, entirely from the CLI so setting up HTTPS never depends on HTTPS already working. Run
`datasync cert` with no subcommand for the full flag list.

| command | does |
| --- | --- |
| `status` | what is bound (thumbprint, SANs, `NotAfter`, days remaining), and whether the resolved service account can read its private key |
| `list [--location LocalMachine\|CurrentUser]` | server-authentication certificates in `<location>\My` |
| `new-self-signed --dns <names> [--days] [--account]` | issues and installs a self-signed certificate, in-box .NET (`CertificateRequest.CreateSelfSigned`), no PowerShell |
| `enroll --dns <names> [--ca] [--template] [--account]` | submits a CSR to an enterprise CA over COM (`ICertRequest`); a template requiring approval reports a request id and exits — not an error |
| `renew [--ca] [--template] [--account] [--days]` | re-enrolls with the bound certificate's subject and SANs (a fresh request, not a signed AD CS renewal) — works even if the bound certificate has already expired |
| `retrieve --request-id <id> [--account]` | collects a pending enrollment from `enroll`/`renew` |
| `templates [--ca]` | best-effort: templates published on the configured CA; empty-plus-a-reason on any failure (not domain-joined, LDAP unreachable, access denied, `CaConfig` unset), never blocking — `--template` stays free text regardless |
| `bind --thumbprint <thumbprint> [--location] [--allow-invalid\|--no-allow-invalid]` | binds an already-installed certificate by writing the four `Kestrel:Certificates:Default:*` keys (below) into `datasync.config.yaml`, committed the same way every other config write is |

`--account` on the issuing commands, because issuing and granting private-key read access are one
operation from the operator's point of view — resolved as an explicit `--account`, else the account the
installed `DataSync` service runs as (read back via `sc.exe qc`), else `LocalSystem` (which needs no
grant, since `LocalMachine\My`'s default ACL already covers it).

**Nothing takes effect until the process restarts** — `bind` only writes config; Kestrel's certificate
is resolved once at startup, the same as every other `DataSync:*`/`Kestrel:*` setting.

### `datasync invite`

Prints a fresh single-use invitation URL — for when the first-run one has scrolled off-screen, or the
process is a service with no console to print to in the first place.

| flag | default |
| --- | --- |
| `--repo <path>` | see "Repo root resolution," above |
| `--state-db <path>` | same as `serve` |
| `--url <url>` | same as `serve` |
| `--role Admin\|Viewer` | `Admin` |

Opens the state database directly rather than calling a running API — the situation it exists for is
"nobody can sign in," and an endpoint that needs a session is no help there. Resolves
`DataSync:StateEngine`/`DataSync:StateConnectionString` the same way `serve` does (the resolved
config file, then `DataSync__*` environment variables), so it works against a `MsSql`- or
`Postgres`-backed deployment, not only the SQLite default — before phase 79 it opened SQLite
unconditionally and had no way to reach the other two. For SQLite, requires the state database file
to already exist (i.e., DataSync has been started at least once); for the other two engines, it
connects to whatever `StateEngine`/`StateConnectionString` (plus the secret store, if needed) name.

### `datasync health`

| flag | default |
| --- | --- |
| `--repo <path>` | see "Repo root resolution," above — used only to find a config file's `DataSync:Url`, nothing else |
| `--url <url>` | `--url` flag, else the resolved config's `DataSync:Url`, else `http://127.0.0.1:8080` (the *container's* port, not `serve`'s 5080) |

Hits `{url}/api/health`; exits `0` on success, `1` otherwise. This is what the container's
`HEALTHCHECK` runs — inside the image there's no config file to find, so it always falls through to
the hardcoded default unless `--url` is passed (which the image's own `HEALTHCHECK` does).

### `datasync secret set|list|remove`

Thin wrappers over the same secret store connections use (see "Secrets," below) — reachable without
going through a connection's own save flow. Never prints a stored value back.

| command | does |
| --- | --- |
| `datasync secret set <ref> <value>` | stores `value` under `ref` |
| `datasync secret list [<ref> ...]` | reports whether each given ref is currently set (given none, checks the one fixed ref this build defines: `datasync:config:stateConnectionString`) |
| `datasync secret remove <ref>` | deletes a stored value |

Example — setting the state store's password for a `datasync.config.yaml`-configured deployment:

```
datasync secret set datasync:config:stateConnectionString "Password=..."
```

### `datasync version`

No flags.

## DataSync.Api process configuration

Read through ASP.NET Core's standard configuration chain — so every key below is settable
interchangeably as a `--DataSync:Key value` CLI argument, a `DataSync__Key` environment variable
(double underscore), under `"DataSync": { "Key": ... }` in `appsettings.json` /
`appsettings.Development.json`, or (phase 79) under `DataSync:` in `datasync.config.yaml` at the repo
root. Neither shipped `appsettings*.json` sets any `DataSync:*` key today — every default below lives
in code. `datasync.config.yaml` sits in the same precedence slot `appsettings.json` occupies — behind
environment variables and the command line — so a CLI flag or env var still overrides a value the file
sets; see "`datasync.config.yaml`," above, for the file itself.

### `DataSync:*`

| key | env var | default | notes |
| --- | --- | --- | --- |
| `RepoRoot` | `DataSync__RepoRoot` | `<cwd>/datasync-repo` under raw `dotnet run`; the CLI's `--repo` default under `datasync serve` | git-tracked config store root |
| `Url` | `DataSync__Url` | none | **CLI-consumed, not an `ApiOptions` field** — `datasync serve`/`datasync health` read this themselves (see above) and translate it to `--urls`/Kestrel's bind address; running the raw API project doesn't read it at all (use `ASPNETCORE_URLS`/`--urls` directly there instead) |
| `StateDbPath` | `DataSync__StateDbPath` | `<RepoRoot>/state.db` | the SQLite file; ignored when `StateEngine` is `MsSql` or `Postgres` |
| `StateEngine` | `DataSync__StateEngine` | `Sqlite` | which database backs the state store — `Sqlite`, `MsSql` or `Postgres`; see below |
| `StateConnectionString` | `DataSync__StateConnectionString` | none | how to reach that engine; required unless `StateEngine` is `Sqlite`; never put a password in it — see "Secrets," below |
| `TaskRunnerDllPath` | `DataSync__TaskRunnerDllPath` | resolved automatically | see below |
| `StatePort` | `DataSync__StatePort` | `0` (ephemeral) | loopback-only runner-state listener; set to a fixed port if you'd rather firewall a known one than trust the loopback binding |
| `RunRetentionDays` | `DataSync__RunRetentionDays` | `90` | finished runs older than this are pruned hourly; `0` = keep forever |
| `RunRetentionMaxPerMapping` | `DataSync__RunRetentionMaxPerMapping` | `1000` | most recent N finished runs kept, *per table mapping* (not global); `0` = no cap |
| `RunPruningIntervalMinutes` | `DataSync__RunPruningIntervalMinutes` | `60` | how often the retention sweep runs |
| `ChangeCheckRetentionDays` | `DataSync__ChangeCheckRetentionDays` | `7` | how long the scheduler's change-check history (phase 75) is kept; `0` = keep forever |

`TaskRunnerDllPath` resolves in this order: (1) beside the running assembly — true for the tool, the
container, and any plain `dotnet publish`; (2) a dev-repo-layout guess (swaps `DataSync.Api/bin` for
`DataSync.TaskRunner/bin`) — true only for this repository's own working tree.

### State store engine (`StateEngine`/`StateConnectionString`)

SQLite by default — a file at `StateDbPath`, nothing to configure. `StateEngine` can instead be set to
`MsSql` or `Postgres` to run the state store (run history, the work queue, watermarks, users and
sessions) on infrastructure a deployment already operates and backs up, with `StateConnectionString`
saying how to reach it. Two separate keys rather than one connection string carrying a provider hint,
so a typo in the engine name fails as "you named an engine that doesn't exist" rather than as a
driver-level parse error.

The schema is created on first open regardless of engine, and all three are meant to behave
identically — this is API-process-only config; `DataSync.TaskRunner` never reads it, since a runner
always reaches state over the phase 39 loopback endpoint rather than opening the store directly.

Two things worth being explicit about:
- **No cross-engine migration.** Pointing an existing deployment at a different `StateEngine` starts an
  empty store — it does not move anything. Choose once, at stand-up.
- **An unrecognized `StateEngine` value falls back to `Sqlite`** rather than refusing to start — a typo
  shouldn't take down an API that has a perfectly good store already.

A run failing *either* retention cap is pruned along with its log lines; a run that hasn't finished is
never pruned regardless of age. Verification results are **not** covered by this pruning — a
verification run's `TaskRuns` row is pruned, but its result file and index entry are not (a known gap).

### `DataSync:Auth:*`

| key | env var | default | meaning |
| --- | --- | --- | --- |
| `DataSync:Auth:Disabled` | `DataSync__Auth__Disabled` | `false` | runs with **no authentication at all** — an explicit opt-in for a trusted-network deployment, never the silent default |
| `DataSync:Auth:AdminGroup` | `DataSync__Auth__AdminGroup` | none | Windows group whose members are admins |
| `DataSync:Auth:ViewerGroup` | `DataSync__Auth__ViewerGroup` | none | Windows group whose members are viewers |

Windows authentication (`Negotiate`) is registered automatically when the process is running on
Windows — it's additive to session-cookie auth, not a mode switch, and works independently of whether
either group is configured. If neither `Disabled` nor a group is set, the only way in on a fresh
install is the bootstrap invite: printed as a startup warning whenever no users exist yet, and always
reprintable with `datasync invite`.

### `DataSync:Auth:Passkeys:*`

| key | env var | default |
| --- | --- | --- |
| `DataSync:Auth:Passkeys:RelyingPartyId` | `DataSync__Auth__Passkeys__RelyingPartyId` | `localhost` |
| `DataSync:Auth:Passkeys:RelyingPartyName` | `DataSync__Auth__Passkeys__RelyingPartyName` | `DataSync` |
| `DataSync:Auth:Passkeys:Origins` (array) | `DataSync__Auth__Passkeys__Origins__0`, `__1`, ... | `http://localhost:5080`, `https://localhost:5080`, `http://localhost:5173` |

`RelyingPartyId` is a **bare domain** — no scheme, no port, and it cannot be an IP address (WebAuthn
requires a real domain name). `Origins` are full URLs, and each one's host must match
`RelyingPartyId` or a subdomain of it. Checked at startup — a misconfiguration logs a warning naming
exactly what's wrong, rather than failing silently inside a browser API the first time someone tries
to enroll a key.

### `DataSync:Certificates:*`

| key | env var | default | meaning |
| --- | --- | --- | --- |
| `DataSync:Certificates:ExpiryWarningDays` | `DataSync__Certificates__ExpiryWarningDays` | `30` | how many days before the bound certificate's `NotAfter` the daily expiry check (phase 82) starts raising a `CertificateExpiring` notification; raised at most once a day, and `CertificateExpired` once past `NotAfter` |
| `DataSync:Certificates:CaConfig` | `DataSync__Certificates__CaConfig` | none | the enterprise CA's `CASERVER\CA Name` string — read by `datasync cert enroll`/`renew`/`templates` (a CLI-process concern; the running API never needs to know which CA a certificate came from, only which one is bound) |
| `DataSync:Certificates:Template` | `DataSync__Certificates__Template` | none | the certificate template name for `datasync cert enroll`/`renew` |

Windows-only end to end (phase 82) — see `datasync cert`, above, for issuance/installation/binding.
The daily expiry check is a hosted service in the API process, registered only when
`OperatingSystem.IsWindows()`, on the same pattern `SchedulerService`/`RunPruningService` already use.

### `Kestrel:Certificates:Default:*`

Not a `DataSync:*` key — ASP.NET Core's own Kestrel configuration, read the same way (config file,
environment variable, CLI flag), and what `datasync cert bind` writes into `datasync.config.yaml`:

| key | meaning |
| --- | --- |
| `Kestrel:Certificates:Default:Subject` | the certificate's simple subject name — a store *lookup*, not a file path, so no certificate password is ever persisted by DataSync and the private key never leaves the Windows store |
| `Kestrel:Certificates:Default:Store` | always `My`, written by `bind` |
| `Kestrel:Certificates:Default:Location` | always `LocalMachine`, written by `bind` |
| `Kestrel:Certificates:Default:AllowInvalid` | `true` for a self-signed certificate (Kestrel validates the chain on load and refuses one it cannot build otherwise); `bind` reports this and leaves it as configured on every subsequent bind rather than silently clearing it when a CA-issued certificate replaces a self-signed one — use `--no-allow-invalid` to turn it off explicitly |

**Nothing takes effect until the process restarts** — resolved once at startup, same as every other
`DataSync:*` setting.

### Standard ASP.NET Core variables

Not DataSync-specific, but relevant: `ASPNETCORE_URLS` (checked directly at startup to decide whether
HTTPS redirection should be enabled — it isn't, unless an `https://` URL is actually bound) and
`ASPNETCORE_ENVIRONMENT` (set to `Development` in `Properties/launchSettings.json` for the dev-loop
`http`/`https` profiles).

## DataSync.TaskRunner process (CLI only)

Spawned per run — by `ProcessSupervisor` inside the API, or directly by `datasync`. Hand-rolled
argument parser; an unrecognized flag is a hard error, not a silent ignore.

| flag | required | default |
| --- | --- | --- |
| `--repo-root <path>` | yes | — |
| `--state-db <path>` | yes | — |
| `--replication <name>` | yes | — |
| `--degree-of-parallelism <n>` | no | `4` |
| `--state-endpoint <url>` | no | falls back to the `DATASYNC_STATE_ENDPOINT` environment variable |
| `--state-grace-seconds <n>` | no | `60` |

Two more environment variables exist here, but they're **internal, process-to-process only** — set by
the parent API when it spawns a runner, never something an operator sets by hand:

- `DATASYNC_STATE_ENDPOINT` — the loopback address of the process that owns the state store.
- `DATASYNC_RUNNER_TOKEN` — the runner's auth token for that loopback connection.

## Secrets

Connection credentials go through `ClrKernel.Core.Secrets.SecretStore` (an external package). The
secret ref for a connection named `<name>` is `datasync:connection:<name>`. When no OS keyring is
available (a sandboxed or CI environment, most commonly), `SecretStore` falls back to an environment
variable: the ref uppercased, with every non-alphanumeric character replaced by `_`, prefixed
`CLRKERNEL_SECRET_`.

Example: a connection named `orders-db` resolves to `CLRKERNEL_SECRET_DATASYNC_CONNECTION_ORDERS_DB`.

## SPA dev server (`src/DataSync.Web`)

| env var | default | meaning |
| --- | --- | --- |
| `DATASYNC_API_URL` | `http://localhost:5183` | target for Vite's `/api` and `/hubs` dev-server proxy |

No `.env` files ship with the project — this is the only variable Vite itself reads.

## Container image

```sh
docker run -p 8080:8080 -v datasync-data:/var/lib/datasync <image>
```

- `EXPOSE 8080`; entrypoint is `datasync serve --url http://0.0.0.0:8080`, with `CMD ["--repo", "/var/lib/datasync"]` as the default trailing arguments — override at `docker run` time to pass different `datasync serve` flags.
- `HEALTHCHECK` runs `datasync health --url http://127.0.0.1:8080`.
- `VOLUME ["/var/lib/datasync"]` — one mount is a complete deployment: the config repository and the state database live together, so a backup of this directory is a backup of everything that isn't the image itself.
- **`ENV DATASYNC_HOME=/var/lib/datasync` is set in the image but not currently read anywhere in the application.** The actual root comes from the `--repo` argument in `CMD`, not this variable — treat `DATASYNC_HOME` as vestigial today, not as a supported override.

## `tools/dev-harness` — development and testing only

Not application configuration — a separate tool for standing up local SQL Server/Postgres containers,
seeding data, generating live traffic, and verifying/corrupting a running replication for testing.
Run `tools/dev-harness help` for the full, current, self-documented list of verbs and per-verb flags
(`--tables`, `--rows`, `--rate`, `--duration`, `--parallelism`, `--target-engine`, and more).

Its environment variables:

| env var | default | meaning |
| --- | --- | --- |
| `DATASYNC_MSSQL_SA_PASSWORD` | `DataSync_Test_Pw1` | SA password for both SQL Server containers |
| `DATASYNC_POSTGRES_PASSWORD` | `DataSync_Test_Pw1` | PostgreSQL container password |
| `DATASYNC_HARNESS_TARGET_ENGINE` | `mssql` | or `postgres` — must agree across `up`/`verify`/`drift` |
| `DATASYNC_HARNESS_TABLES` | `1` | how many generated tables — same rule |
