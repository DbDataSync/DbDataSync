# Linux TLS without a reverse proxy

**Resolved 2026-09-13 — all three tiers are now implementation phases.** Tier 1 is done
(`architecture/implementation/done/phase-113-tls-bring-your-own-certificate.md`); tier 3 is scoped and
deliberately deferred (`architecture/implementation/todo/phase-114-tls-acme.md`); tier 2, the one this
document still had open, is now `architecture/implementation/todo/phase-130-tls-managed-self-signed.md`,
which resolves all four of tier 2's open questions below (leaf-only, not a local CA; the key lives at
`<repo>/tls/`; `setup` offers it on request, not automatically; `config check` distinguishes a
self-signed cert this mechanism manages from one bound by other means) — and corrects two real gaps in
this document's own tier-2 sketch: the "no restart" cert-swap it assumed doesn't exist anywhere in this
codebase (phase 113 built and consumed the file-based path but explicitly declined to build a live
reload seam), and "an extension of `CertificateExpiryService`" isn't possible since that service is
Windows-only end to end. See phase 130 for the corrected design.

This document is kept below as the original proposal record.

Today the only documented way to serve DbDataSync over HTTPS on Linux is nginx/Caddy in front. That
is a fine production topology, but it is a second thing to install, configure and patch for a tool
meant to be self-contained, and it is a hard stop for a small internal deployment. Passkeys need
HTTPS anywhere but `localhost`, so this is on the critical path for any non-trivial Linux install.

## The three tiers

1. **Bring your own certificate** — point DbDataSync at a PEM/PFX file the operator already keeps
   current (certbot, an internal PKI, a platform team). → **phase 113**
   (`phase-113-tls-bring-your-own-certificate.md`). A cross-platform `dbdatasync config cert use-pem`
   writing the standard `Kestrel:Certificates:Default:*` keys, with the credential in the secret
   store; the cert-reload seam it builds is shared with tier 3.
2. **Managed self-signed** — DbDataSync generates its own certificate, serves it, and regenerates it
   before expiry. No CA, no files to manage; the operator distributes trust for the cert once. →
   **this doc**, below.
3. **ACME** — the process obtains and renews a real certificate from Let's Encrypt or an internal
   ACME CA, with nothing external. → **phase 114** (`phase-114-tls-acme.md`). An opt-in
   `DbDataSync:Tls:Acme` block; http-01 / tls-alpn-01; cert in `<repo>/tls/`. OS-neutral, so it
   becomes a certificate option on Windows too, alongside the store-based path.

Ship 1 and 3. Tier 2 sits between them in value: cheaper to run than ACME (no CA reachability, no
rate limits) but every client has to be told to trust the cert, which ACME and tier 1 avoid.

---

## Tier 2 — managed self-signed (future consideration)

### What it would be

- Generalise `dbdatasync config cert new-self-signed` — Windows-only today — to a cross-platform command
  that builds a keypair with `CertificateRequest` / `X509Certificate2`, writes it to `<repo>/tls/`
  (git-ignored, the same directory tier 3 defines), and sets
  `Kestrel:Certificates:Default:{Path,KeyPath}` + `AllowInvalid=true`.
- A background service — an extension of `CertificateExpiryService`, or a sibling — regenerates the
  certificate when it is within N days of `NotAfter`, and swaps it in through the same cert selector
  tiers 1 and 3 use. No restart.
- SANs from the console URL's host plus `localhost`, so the same cert works for a hostname and a
  loopback check.

### When it is the right answer

- A **trusted internal network** where the operators control the clients: they import the cert (or
  its issuing key as a mini-CA root) into the handful of machines that reach the console, once, and
  never think about certificates again.
- A deployment that **cannot reach any CA** — no internet for Let's Encrypt, no internal ACME
  server, no existing PKI to issue from — where tier 1 has nothing to point at and tier 3 has no
  directory URL.
- **First-boot / evaluation**, as a better default than plain HTTP: `setup` could offer "generate a
  self-signed certificate now" so a fresh install is HTTPS-at-a-hostname immediately, with a clear
  note that browsers will warn until the cert is trusted.

### Why it is not a phase yet

- Tiers 1 and 3 cover the deployments that matter most: an operator with a PKI (1) and an operator
  who wants it to just work against a real CA (3). Tier 2's niche — no CA reachable *and* clients
  the operator can configure — is real but smaller.
- The "distribute trust" step is manual and per-client, which is exactly the friction ACME exists to
  remove. Building tier 2 well means also thinking about generating a **local CA** (root + leaf) so
  the operator imports one root and can re-issue leaves silently — which is most of an internal-PKI
  feature, and a bigger decision than "self-signed leaf."
- It shares all its plumbing with tiers 1 and 3 (the `<repo>/tls/` directory, the cert selector, the
  `CertificateExpiryService` extension), so building it later costs little that this phasing wastes.

### Open questions for when it is picked up

1. **Self-signed leaf, or a generated local CA (root + leaf)?** A leaf is simpler; a local CA lets
   the operator trust one root and never touch it again as leaves rotate. Leaning: local CA — the
   rotation story is the whole point, and a rotating self-signed leaf means re-trusting on every
   renewal.
2. **Where the (CA) private key lives** — `<repo>/tls/`, `0600`, service-account-owned, git-ignored;
   same as tier 3's account key. A generated CA root key is more sensitive than an ACME account key,
   though — worth a louder note in the docs.
3. **`setup` default** — should a fresh non-localhost install get a self-signed cert automatically,
   or only on request? Leaning: on request, with the browser-warning caveat stated; automatic HTTPS
   that every browser flags is its own kind of bad first impression.
4. **Interaction with `config check`** — a self-signed cert is `AllowInvalid`, so `config check`'s
   cert check has to distinguish "deliberately self-signed and current" from "misconfigured."
