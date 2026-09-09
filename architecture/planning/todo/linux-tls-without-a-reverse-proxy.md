# Linux TLS without a reverse proxy — including automatic (ACME) certificates

**Status: proposal / investigation, not agreed.** Draft for review.

Today the only documented way to serve DbDataSync over HTTPS on Linux is to put nginx/Caddy in
front. That is a reasonable production topology, but it is a second thing to install, configure and
keep patched for what is meant to be a single self-contained tool — and it is a hard stop for a
small internal deployment where "just run it and reach it over HTTPS" should be enough. Passkeys
need HTTPS anywhere but `localhost`, so this is on the critical path for a non-trivial Linux install.

Scope: Kestrel terminating TLS itself on Linux, and — the part worth investigating — issuing and
renewing the certificate automatically.

---

## Recommendation up front

Three tiers, ship 1 and 3:

1. **Bring-your-own PEM** — `dbdatasync cert` gains a cross-platform `use-pem --cert <path> --key
   <path>` that writes the `Kestrel:Certificates:Default:{Path,KeyPath}` keys (Kestrel reads PEM
   directly, and `reloadOnChange` picks up a renewed file). Near-zero code; it is the baseline that
   works with certbot-on-a-cron, an internal PKI, or a cert handed over by a platform team.
2. **Managed self-signed** — `dbdatasync cert new-self-signed` works on Linux (it is Windows-only
   today), writing a PEM pair into `<repo>/tls/` and the Kestrel keys, with a background service
   regenerating before expiry. For a trusted network where clients trust the cert by hand.
3. **ACME** — an opt-in config block (`DbDataSync:Tls:Acme`) that makes the process obtain and renew
   a certificate from Let's Encrypt or an internal ACME CA, with no external tooling. This is the
   "just works" path and the main thing to get right.

ACME is **platform-neutral** — it should become an option on Windows too (alongside the existing AD
CS / cert-store path), which starts to close the "the two platforms operate differently" gap the
data-directory phase (112) also addresses.

---

## What exists today

- **Kestrel HTTPS is entirely config-driven** — `Kestrel:Certificates:Default:*`, resolved once at
  startup. `DbDataSyncHost` adds only `UseHttpsRedirection` when an HTTPS URL is bound. No cert code
  in the host.
- **The Windows cert story** (phases 82–83) is store-based: `Store=My`, `Location=LocalMachine`,
  issued by `dbdatasync cert` from an AD CS template or self-signed, private key never leaving the
  Windows store. `CertificateExpiryService` warns on approaching expiry; `AdminCertificateService`
  is the admin-screen backend. Both already `OperatingSystem.IsWindows()`-guard themselves and are
  inert on Linux.
- **`Kestrel:Certificates:Default:Path` + `KeyPath`** already work cross-platform (ASP.NET Core
  reads PEM). Undocumented and unwired, but tier 1 is mostly "expose and document this."

---

## The tiers in detail

### Tier 1 — bring-your-own PEM

- `dbdatasync cert use-pem --cert /etc/ssl/dbdatasync.crt --key /etc/ssl/dbdatasync.key
  [--password-ref <secretRef>]` — writes `Kestrel:Certificates:Default:Path` / `KeyPath` (and, for
  an encrypted key, a `Password` sourced from the secret store, never the file) into
  `dbdatasync.config.yaml`, committed like every other config write. Mirrors `cert bind` on Windows.
- Set `reloadOnChange: true` on the Kestrel certificate config so a renewed file is picked up
  without a restart — verify Kestrel honours this for file certs (it does for appsettings; confirm
  for the cert path specifically, it may need `ConfigureHttpsDefaults` with a file watcher).
- Doc: a certbot `--deploy-hook` that copies the renewed cert into place, or just points the config
  at certbot's `live/` path directly.

### Tier 2 — managed self-signed

- Generalise `dbdatasync cert new-self-signed` (Windows-only today) to write a PEM keypair on Linux
  via `X509Certificate2` / `CertificateRequest`, into `<repo>/tls/` (git-ignored), and set the
  Kestrel keys with `AllowInvalid=true`.
- A background service (or extend `CertificateExpiryService`) regenerates when < N days remain.
- Fine for `localhost`-plus-a-hostname internal use; the passkey RP id is the hostname and passkeys
  work against a self-signed cert the browser has been told to trust.

### Tier 3 — ACME

The design work. An opt-in block:

```yaml
DbDataSync:
  Tls:
    Acme:
      Enabled: true
      Domains: [dbdatasync.corp.example]
      ContactEmail: ops@corp.example
      DirectoryUrl: https://acme-v02.api.letsencrypt.org/directory   # or an internal step-ca / ADCS ACME
      Challenge: http-01   # http-01 | tls-alpn-01
      AcceptTermsOfService: true
```

- On startup, if `Enabled`, the process registers/loads an ACME account, requests a cert for
  `Domains`, answers the challenge on its own listener, installs the cert into Kestrel's cert
  selector, and schedules renewal (well before the 90-day Let's Encrypt lifetime).
- **Challenge types:**
  - **http-01** — a `.well-known/acme-challenge/*` route on port 80. Simplest; needs the domain to
    resolve to this host and port 80 reachable *from the CA*. On systemd, the unit needs
    `AmbientCapabilities=CAP_NET_BIND_SERVICE` to bind 80 as a non-root user (cross-ref phase 111).
  - **tls-alpn-01** — port 443 only, no port 80, via a special TLS handshake on the main listener.
    Better when only 443 is open; the ACME library has to hook the TLS handshake.
  - **dns-01** — the only option when the host is not reachable from the public internet at all, but
    needs a DNS-provider plugin (Cloudflare/Route53/RFC2136/…). **Out of tier-3 v1** — an internal
    deployment that cannot do http-01 should point `DirectoryUrl` at an **internal ACME CA**
    (smallstep `step-ca`, or an AD CS ACME endpoint) which can do http-01 on the internal network.
- **Certificate + account key storage** — `<repo>/tls/` (git-ignored, beside `state.db` and
  `FIRST-RUN.txt`), or the state DB. A private key in the git-tracked config is not acceptable.
  Leaning: `<repo>/tls/` with a written `.gitignore` entry.
- **`doctor`** gains an ACME check: account resolves, cert present and not near expiry, the last
  renewal attempt's outcome.

#### The ACME client — the thing to actually decide

| option | notes |
| --- | --- |
| **LettuceEncrypt** (`natemcmaster/LettuceEncrypt`) | Kestrel-native, `AddLettuceEncrypt()`, does http-01 + tls-alpn-01 + renewal + a pluggable cert store. The obvious fit. Concern: maintenance cadence has slowed; last releases are infrequent. Evaluate whether it still tracks current ASP.NET Core cleanly on .NET 10. |
| **Certes** (`fszlin/certes`) | Lower-level, well-maintained ACME v2 client. No Kestrel integration — we write the challenge listener, the cert selector, and the renewal loop (~a few hundred lines). More control, more to own. |
| **Minimal in-house** | Only if both above disappoint. ACME v2 is not enormous, but JWS, nonce handling and the challenge dance are exactly the kind of thing to get subtly wrong. |

Leaning: **prototype with LettuceEncrypt first**; fall back to **Certes + our own glue** if
LettuceEncrypt does not build clean against the current stack or its cert-store abstraction fights
the `<repo>/tls/` model.

---

## Cross-platform consequence

ACME does not care about the OS. Once tier 3 exists, `DbDataSync:Tls:Acme` is a valid path on
**Windows** too — an operator without AD CS, or who just wants Let's Encrypt, gets the same block.
The Windows cert-store path (phases 82–83) stays for AD-CS shops. This is the direction phase 112's
"one operating mode per platform" points: the certificate story converges on ACME, with the
store-based path as the Windows-specific alternative rather than the only option.

---

## systemd interaction (phase 111)

- **http-01 needs port 80.** If ACME with http-01 is configured, phase 111's unit needs
  `AmbientCapabilities=CAP_NET_BIND_SERVICE` (and `AmbientCapabilities` implies the service can bind
  low ports without root). `service install` should add it when it can tell ACME/http-01 is in play,
  or always (it is a narrow capability).
- **tls-alpn-01 needs port 443**, same capability.
- The renewal is in-process (a background service), so nothing extra in the unit for that.

---

## What this does not do

- **dns-01 and DNS-provider plugins** — a later addition if internal deployments that cannot do
  http-01 even against an internal CA turn out to be common.
- **A cert-management admin screen for Linux** — phase 83's screen is Windows-store-shaped;
  surfacing ACME state is a smaller, later UI task (`doctor` covers the operational need first).
- **Replacing the Windows cert-store path** — it stays; ACME is added alongside.
- **HTTP→HTTPS on port 80 as a permanent redirect listener** — out of scope beyond what http-01
  needs transiently; `UseHttpsRedirection` already handles the redirect once 443 is bound.

---

## Open questions

1. **LettuceEncrypt vs. Certes** — resolved by a spike: does `AddLettuceEncrypt()` build and issue
   against Let's Encrypt staging on the current stack, and can its cert store be pointed at
   `<repo>/tls/`? If yes, use it. If it needs forking, Certes.
2. **Where the issued cert + ACME account key live** — `<repo>/tls/` (git-ignored) vs. the state DB.
   Leaning: `<repo>/tls/`, so a `state.db` backup and a `tls/` backup are separable and the cert is
   a plain file an operator can inspect.
3. **Internal ACME CA support** — is pointing `DirectoryUrl` at `step-ca` / an ADCS ACME endpoint
   enough, or do internal CAs need trust-root handling (the box has to trust the internal CA to
   validate its own issued cert on load — `AllowInvalid` or adding the root to the OS trust store)?
4. **Does tier 1's `reloadOnChange` actually work for a file cert in Kestrel**, or does it need an
   explicit `IOptionsMonitor` + `ConfigureHttpsDefaults` reload? Verify before promising renewal
   without a restart.
5. **First-issuance startup behaviour** — the process cannot serve HTTPS until the first cert is
   issued (seconds to a minute for http-01). Serve HTTP-only until then and swap in the cert, or
   block startup? Leaning: serve http-01's listener + a "TLS is being provisioned" HTTP response,
   swap when ready.
6. **macOS** — no server story (phase 111 excludes launchd), so ACME on macOS is not a goal; tier 1
   (BYO PEM) is enough for a Mac dev who wants HTTPS.
