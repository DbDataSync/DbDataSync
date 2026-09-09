# Phase 113 — TLS on any platform with a certificate you supply (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/linux-tls-without-a-reverse-proxy.md`, tier 1.
Independent of phase 114 (ACME); a deployment picks one or neither.

## Why

Kestrel can terminate TLS from a PEM or PFX file on every platform — `Kestrel:Certificates:Default:*`
is standard ASP.NET Core config, resolved once at startup, and `DbDataSyncHost` adds nothing to it.
On Windows `dbdatasync config cert bind` writes those keys for a store-installed certificate; on Linux and
macOS there is no equivalent and no documentation, so the only HTTPS answer today is a reverse proxy.

This phase gives every platform a one-command way to point DbDataSync at a certificate file the
operator already has — from certbot, an internal PKI, or a platform team — with the credential
handled the same way every other secret is.

## What this builds

### 1. `dbdatasync config cert use-pem` — cross-platform — `src/DbDataSync.Cli/CertCommand.cs`

```
dbdatasync config cert use-pem --cert <path> --key <path> [--password-ref <secretRef>] [--repo <path>]
dbdatasync config cert use-pfx --pfx <path>  [--password-ref <secretRef>] [--repo <path>]
```

- Writes into `dbdatasync.config.yaml`, committed by `GitCommitService` like every other config
  write:
  - PEM: `Kestrel:Certificates:Default:Path` = the cert path, `:KeyPath` = the key path.
  - PFX: `Kestrel:Certificates:Default:Path` = the `.pfx` path.
  - encrypted key / PFX password: `Kestrel:Certificates:Default:Password` is **not** written —
    instead a secret ref (default `dbdatasync:config:kestrelCertificatePassword`, a new
    `SecretRefs.ForAppSetting` constant) is stored via `SecretStore`, and `DbDataSyncHost` splices
    the resolved value into the Kestrel config at startup the same way `StateDatabase.Factory`
    splices the state-DB password. No certificate password ever lands in the git-tracked file — the
    rule `ConfigValidation.RejectEmbeddedCredential` already enforces for connection strings.
- Validates the files load (`X509Certificate2.CreateFromPemFile` / `new X509Certificate2(pfx, pw)`)
  and prints the subject, SANs and `NotAfter` — the "find out now" print `config cert bind` already does.
- `--reload` (default on): also sets `reloadOnChange: true` on the Kestrel cert config (see §3).

### 2. The `CertCommand` platform gate — `src/DbDataSync.Cli/CertCommand.cs`

Today `Run` hard-bails off Windows before dispatching. Change it to: off Windows, allow only
`use-pem` / `use-pfx` / `status`; every other subcommand keeps its `[SupportedOSPlatform("windows")]`
and prints the existing "certificate management is Windows-only — or use a reverse proxy / `use-pem`"
message. `config cert status` becomes cross-platform: on non-Windows it reports the configured file cert
(path, subject, `NotAfter`, days remaining) rather than reading the Windows store.

### 3. Reload-on-renewal

certbot and an internal PKI renew the file in place; the running process should pick it up without a
restart. **This needs verifying, not assuming.** Kestrel's file-cert config supports
`reloadOnChange`, but historically the file watcher has been unreliable across editors/atomic
renames.

- If `reloadOnChange` works: `use-pem` sets it and the docs say "certbot's `deploy-hook` can just
  `systemctl reload` nothing — the swap is automatic."
- If it does not: `DbDataSyncHost` adds a small `IOptionsMonitor<KestrelServerOptions>` +
  `ConfigureHttpsDefaults(o => o.ServerCertificateSelector = …)` that re-reads the file when its
  timestamp changes, checked on a timer or a `FileSystemWatcher`. This is also the seam phase 114
  needs for ACME cert swap, so building it here is not wasted.
- Fallback either way: `dbdatasync config cert reload` — hits a loopback admin endpoint that re-reads the
  configured cert. A `systemctl reload dbdatasync` deploy-hook (needs `ExecReload=` in phase 111's
  unit) is the last resort.

### 4. `config check` and docs

- `config check`'s existing "auth / TLS" check (phase 110, as `doctor`; folded into `config check` by
  phase 115) already validates a bound HTTPS cert on Windows. Extend it: on any platform, if
  `Kestrel:Certificates:Default:Path` is set, the file exists, loads, its SANs cover the console
  URL's host, and `NotAfter` is not near.
- `docs/getting-started.md` Linux section: `dbdatasync config cert use-pem --cert /etc/letsencrypt/live/…/fullchain.pem
  --key …/privkey.pem`, and a certbot `--deploy-hook` example.
- `setup` step 7 (currently Windows-only cert): on Linux, offer `use-pem` — prompt for the two
  paths — alongside the "reverse proxy" and (phase 114) "ACME" options.

## What this phase does not build

- Issuing or renewing anything — this phase only consumes a file someone else manages. Automatic
  issuance is phase 114; a self-signed cert DbDataSync generates and rotates is the tier-2 planning
  doc.
- A Linux equivalent of `config cert enroll` / AD CS — that stays Windows-only.
- The `AdminCertificatePage` SPA screen gaining a Linux view — `config check` and the CLI cover the need.

## How to verify when built

- `dotnet build` clean.
- **`CertUsePemTests`** (cross-platform) — `use-pem` with a generated test PEM pair writes the two
  Kestrel keys and commits; with an encrypted key + `--password-ref`, the password goes to the fake
  `SecretStore` and not the file; a bad path / unloadable cert fails with a clear message and writes
  nothing.
- **`CertCommandTests`** — off Windows (the CI Linux runner), `config cert enroll` still prints the
  Windows-only message; `config cert use-pem` and `config cert status` work.
- **`ReadinessChecksTests`** — a config pointing at a missing / expired / wrong-SAN file cert produces
  the matching `Fail`/`Warn`.
- **Integration** — start the host with `Kestrel:Certificates:Default:{Path,KeyPath}` set to a
  self-signed test pair, `GET https://localhost:<port>/api/health` with cert validation disabled
  returns 200. Then rewrite the files with a fresh pair and confirm the swap (or, if reload is
  unavailable, that `config cert reload` / a restart picks it up) — this is the test that settles §3.
- Manual on Linux: certbot-issued cert via `config cert use-pem`, browser trusts it, passkey enrolment works
  against the real hostname.

## Open questions

1. **Does Kestrel's `reloadOnChange` reliably pick up an atomically-renamed cert file** on the
   current runtime? Spike it; the answer decides whether §3 is one config line or a small selector.
   (Shared with phase 114.)
2. **PFX as well as PEM, or PEM only?** Leaning: both — `use-pfx` is trivial once the password-ref
   plumbing exists, and a Windows operator exporting from the store or a Java shop will have PFX.
3. **Secret ref name** — `dbdatasync:config:kestrelCertificatePassword` vs. something shorter.
   Match whatever convention `dbdatasync:config:stateConnectionString` set.
