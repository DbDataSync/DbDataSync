# Phase 114 — automatic TLS certificates (ACME) (planned)

**Status**: Planned, not started. **Starts with a spike** (Open questions 1).
**Plan reference**: `architecture/planning/todo/linux-tls-without-a-reverse-proxy.md`, tier 3.
Builds on phase 113's cert-swap seam; independent of it otherwise. Cross-refs phase 111 (systemd
capabilities) and phase 115 (`config check`, née phase 110's `doctor`).

## Why

Phase 113 lets a deployment serve TLS from a file someone else keeps current. ACME removes the
"someone else": the process obtains and renews its own certificate from Let's Encrypt or an internal
ACME CA (`step-ca`, AD CS ACME), with no reverse proxy and no cron. For a small internal
deployment, "run it, reach it over HTTPS at its hostname, forever" should be one config block.

ACME is OS-neutral, so this is not a Linux feature — it is the certificate path that works the same
on Windows, Linux and (a Mac dev's) macOS, alongside the Windows cert-store path (phases 82–83) which
stays for AD-CS shops.

## What this builds

### 1. Config — `src/DbDataSync.Core/Config/` + `src/DbDataSync.Api/Configuration/`

```yaml
DbDataSync:
  Tls:
    Acme:
      Enabled: true
      Domains: [dbdatasync.corp.example]        # SANs on the issued cert; first is the CN
      ContactEmail: ops@corp.example
      DirectoryUrl: https://acme-v02.api.letsencrypt.org/directory   # or staging, step-ca, ADCS ACME
      Challenge: http-01                         # http-01 | tls-alpn-01
      AcceptTermsOfService: true                 # required true for a public CA
```

An `AcmeOptions.FromConfiguration` beside the existing option records; validated at startup
(`Domains` non-empty, `AcceptTermsOfService` for a public directory, `Challenge` known) with the same
"say what is wrong now, not at first use" contract `PasskeyOptions.Problem()` follows.

### 2. Issuance and renewal — `src/DbDataSync.Api/Services/AcmeCertificateService.cs`

An `IHostedService` (started before Kestrel needs a cert, or providing the cert lazily via the
selector — see Open questions 4):

- On start, load a persisted ACME **account** (or register one, storing the account key) and the
  last issued **certificate**, both from `<repo>/tls/` (§3).
- If no valid cert for `Domains`, or it renews within the threshold (⅓ of lifetime remaining, so ~30
  days for Let's Encrypt's 90), run an order: new order → answer the challenge → finalise → download
  → persist → hand to the cert selector.
- A renewal timer (daily check) does the same. A failed renewal logs, raises a notification
  (`NotificationStore`, like `CertificateExpiryService` does for a Windows cert nearing expiry), and
  retries with backoff — it does not crash the process; the current cert is still valid for weeks.

**Challenge listeners:**

- **http-01** — a `.well-known/acme-challenge/{token}` endpoint served on **port 80**. Mapped
  unconditionally when `Challenge == http-01` (it 404s when no order is in flight). Needs port 80
  bound — on systemd that is `AmbientCapabilities=CAP_NET_BIND_SERVICE` (§5). Also serve a plain
  308 → the HTTPS URL on port 80 for everything else, which subsumes `UseHttpsRedirection`.
- **tls-alpn-01** — no port 80; the challenge is answered inside the TLS handshake on 443 via a
  special ALPN protocol and a throwaway cert. The ACME library must support hooking Kestrel's
  handshake for this; if the chosen library does not, tls-alpn-01 is deferred and http-01 is the
  only mode.

### 3. Storage — `<repo>/tls/`

- `<repo>/tls/account.key`, `<repo>/tls/<primary-domain>.pfx` (cert + key), a small `acme.json`
  with order state. `0600`, owned by the service account.
- **Git-ignored** — `ServeCommand.Prepare` / `setup` writes a `.gitignore` with `tls/`, `state.db*`,
  `FIRST-RUN.txt`. A private key must never enter the git-tracked config.
- Not the state DB: a `tls/` backup and a `state.db` backup should be separable, and the cert should
  be a plain file an operator can inspect with `openssl`.

### 4. Wiring the cert into Kestrel

`DbDataSyncHost` sets `ServerCertificateSelector` (the seam phase 113 builds for file-cert reload) to
return `AcmeCertificateService`'s current cert. When ACME is enabled it is authoritative; when
disabled the selector falls back to `Kestrel:Certificates:Default:*` (phase 113 / the Windows store).

### 5. systemd (phase 111)

`service install` adds `AmbientCapabilities=CAP_NET_BIND_SERVICE` to the unit when the resolved
config has `Tls:Acme:Challenge: http-01` (or `tls-alpn-01` binding 443 as non-root). It is a narrow
capability; installing it always would also be defensible. Add `ExecReload=/bin/kill -HUP $MAINPID`
or a reload endpoint hook so a manual cert refresh is possible.

### 6. `config check` and `setup`

- `config check` — an ACME check: `Enabled` and options valid; account key present; a current cert for
  every `Domain`; `NotAfter` beyond the renewal threshold; the last renewal attempt's outcome and
  time. On `http-01`, a note if port 80 does not look bindable.
- `setup` step 7 — "Automatic (Let's Encrypt / ACME)" becomes a certificate option: prompt the
  domain(s), contact email, directory URL (default Let's Encrypt, offer staging and "internal CA
  URL"), ToS acceptance. Writes the block; notes the first issuance happens on next start and takes
  up to a minute.

### 7. Cross-platform

`DbDataSync:Tls:Acme` is valid on Windows. The doc and `setup` present three certificate paths there
— Windows store (`config cert bind`/`enroll`), bring-your-own file (phase 113), ACME — and one or two on
Linux/macOS (file, ACME). This is the convergence phase 112's "one operating mode per platform"
points at.

## What this phase does not build

- **dns-01** and DNS-provider plugins — the fallback for a host not reachable on 80/443 from the CA.
  A later addition; until then such a deployment points `DirectoryUrl` at an internal ACME CA that
  can validate over the internal network, or uses phase 113.
- **Multi-certificate / SNI** for more than one hostname's cert — `Domains` is one cert's SAN list.
- **A Linux cert admin screen** — `AdminCertificatePage` stays Windows-store-shaped; `config check`
  covers the operational need, a small "ACME status" panel is a later UI task.
- **Removing `UseHttpsRedirection`** except where the http-01 listener's 308 already covers it.

## How to verify when built

- The **spike** (before committing to the design): `AddLettuceEncrypt()` (or the Certes glue)
  issues a real cert from **Let's Encrypt staging** for a test domain on the current runtime, and
  its cert store can be pointed at `<repo>/tls/`. Records which library the phase uses.
- **`AcmeOptionsTests`** — validation: empty `Domains`, missing ToS against a public directory,
  unknown `Challenge`.
- **`AcmeCertificateServiceTests`** — against **Pebble** (the ACME test server) in a container
  (add to `docker-compose.yml` / CI services): first issuance persists an account + cert to a temp
  `tls/`; a cert near expiry triggers renewal; a failed challenge logs + notifies + does not throw;
  http-01 endpoint returns the key authorisation for an in-flight order and 404 otherwise.
- **Integration** — host with ACME enabled against Pebble, http-01: `GET https://<domain>/api/health`
  succeeds after issuance; the served cert's issuer is Pebble; forcing a renewal swaps the cert
  without dropping connections.
- **`ReadinessChecksTests`** — ACME enabled but no cert yet → `Warn` ("provisioning"); cert present
  and fresh → `Ok`; last renewal failed → `Fail` with the reason.
- Manual: a real internet-facing Linux host, Let's Encrypt production, http-01 — issues, serves,
  and renews (fast-forward the clock or wait).

## Open questions

1. **LettuceEncrypt vs. Certes vs. minimal.** Decided by the spike. LettuceEncrypt
   (`natemcmaster/LettuceEncrypt`) is Kestrel-native (`AddLettuceEncrypt()`, http-01 + tls-alpn-01 +
   renewal + a pluggable store) but its release cadence has slowed — confirm it builds and issues
   clean on .NET 10 and that its store abstraction accepts `<repo>/tls/`. Certes (`fszlin/certes`)
   is a well-maintained lower-level ACME v2 client with no Kestrel integration — a few hundred lines
   of our own listener + selector + renewal loop. Minimal in-house only if both disappoint.
2. **Internal ACME CA trust.** The box must trust the internal CA to load and validate its own
   issued cert. Does the phase add the CA root to the OS trust store, set `AllowInvalid`, or leave
   it to the operator? Leaning: document that the CA root must be OS-trusted (which it usually
   already is on a domain-joined box), and `config check` reports a chain-build failure clearly.
3. **Where the ACME account is scoped** — one account per deployment (per `<repo>/tls/`) is simplest
   and what most tools do. Confirm no reason to share across deployments.
4. **First-issuance startup** — the process cannot serve HTTPS until issuance completes (seconds to
   ~a minute). Options: (a) block startup until the first cert; (b) serve the http-01 listener +
   a "TLS is being provisioned, retry shortly" 503 on 443 with a throwaway cert, swap when ready.
   Leaning: (b) — a service manager restarting a process that is "slow to start" because it is
   talking to a CA is worse than a brief 503.
5. **`reloadOnChange` / selector reliability** — shared with phase 113 Open question 1; the cert
   selector this phase needs is the same mechanism.
6. **Rate limits** — Let's Encrypt production caps issuance per domain per week. A crash-loop that
   re-issues on every start would exhaust it. The persisted cert + "only renew within threshold"
   logic must be robust to a process that restarts frequently; the spike should exercise a restart.
