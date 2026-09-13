# Phase 130 — Managed self-signed TLS (tier 2)

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/linux-tls-without-a-reverse-proxy.md`, tier 2. Consumes
phase 113's file-based certificate path exactly as built (`Kestrel:Certificates:Default:{Path,KeyPath}`,
`AllowInvalid=true`); shares no code with phase 114 (ACME), which remains not started. Cross-platform,
like phase 113 and unlike `CertificateExpiryService`/`new-self-signed`, both Windows-only today.

## Why

Today the only cross-platform way to serve HTTPS is phase 113: point DbDataSync at a certificate file
someone else keeps current. That's real, but it means a certbot/internal-PKI dependency even for a
small internal deployment with no reachable CA at all. Tier 2 is the answer for that narrower case:
DbDataSync generates its own certificate, serves it, and warns before it expires — no CA, no external
process, at the cost of every client needing to trust the certificate once.

## Two real gaps in the planning doc's own sketch — found before writing anything, corrected here

The planning doc's tier 2 sketch assumes two mechanisms that don't exist. Both are worth stating
plainly rather than discovering mid-implementation:

1. **"Swaps it in through the same cert selector tiers 1 and 3 use. No restart."** No such selector
   exists. Phase 113 investigated this directly and declined to build it: *"`dbdatasync.config.yaml`
   isn't hot-reloaded by anything in this codebase today... Every existing
   `Kestrel:Certificates:Default:*` change... already requires a restart... left for whenever phase
   114's ACME work needs the same seam for real, at which point building it once for both is worth
   it — building it now, unverified, for this phase alone was not."* Phase 114 hasn't been built
   either. Building the seam now, for tier 2 alone, repeats exactly the risk phase 113 declined.
   **Decision: tier 2 requires a restart to pick up a renewed certificate**, consistent with every
   other certificate path in this codebase today. Not a regression — see "What this builds," #2.
2. **"A background service — an extension of `CertificateExpiryService`."** `CertificateExpiryService`
   is registered only when `OperatingSystem.IsWindows()` (`DbDataSyncHost.Build`) and reads from the
   Windows certificate store — it cannot be generalized in place without breaking that scoping, and
   tier 2's whole point is Linux. **Decision: a new, cross-platform hosted service**, same shape,
   different producer — see "What this builds," #3.

## What this builds

### 1. Cross-platform self-signed generation

`dbdatasync config cert new-self-signed` (`CertCommand.NewSelfSigned`, Windows-only today — it builds a
keypair with `CertificateRequest`/`X509Certificate2` and installs it into the Windows store) gains a
non-Windows path that builds the identical keypair but writes it to `<repo>/tls/dbdatasync.pfx`
(unencrypted, matching phase 113's own supported case) instead of a store, then writes
`Kestrel:Certificates:Default:{Path,KeyPath}` + `AllowInvalid=true` into `dbdatasync.config.yaml` — the
exact keys phase 113's `use-pfx` already writes and Kestrel already binds. No new Kestrel wiring: this
phase is entirely about *producing* a file phase 113 already knows how to *consume*.

### 2. Renewal requires a restart, with a warning first

A regenerated certificate takes effect on the next process start, the same as `cert bind`, `use-pem`,
and `use-pfx` today (phase 113's own "Decisions": *"stated explicitly in `CertificateBinding.Bind`'s
own doc comment"*). No live swap. The cross-platform service (#3) regenerates the file once it's
within the expiry threshold and raises a notification saying a restart is needed — an operator running
this without a service manager set to auto-restart on failure would otherwise have a certificate that
silently went stale.

### 3. `SelfSignedCertificateService` — a new hosted service, not an extension

`src/DbDataSync.Api/Services/SelfSignedCertificateService.cs`, registered when tier 2 is configured
(not gated by OS — the point is that it isn't Windows-only). Same shape
`CertificateExpiryService`/`SchedulerService`/`RunPruningService` already establish: an immediate first
pass, then a daily `PeriodicTimer`, swallowing and logging rather than taking the process down.

Unlike `CertificateExpiryService` (which only *warns* — a Windows-store cert is renewed by hand via
`cert renew`), this service *acts*: within the renewal threshold (⅓ of lifetime remaining, matching
tier 3's own planned threshold for consistency), it regenerates `<repo>/tls/dbdatasync.pfx` in place
using the same generation path as #1, then raises `NotificationKinds.CertificateExpiring` (existing
constant, new producer) noting the restart requirement. Never touches a cert that isn't the one this
phase generated — a file phase 113's `use-pem`/`use-pfx` pointed at is left alone regardless of its own
`NotAfter`; that path is `CertificateExpiryService`'s Windows-only territory or nothing at all on
Linux, unchanged by this phase.

### 4. Storage

`<repo>/tls/dbdatasync.pfx`, `0600`, service-account-owned — the same directory phase 114 (ACME) would
use for its own account key and cert, should it ever be built; no conflict, since only one of tier 2 or
tier 3 is configured at a time. Git-ignored via the existing `.gitignore` write in
`ServeCommand.Prepare`/`setup`.

### 5. SANs

The console URL's host, plus `localhost` — so the same certificate answers both a hostname request and
a loopback health check.

### 6. `setup`

The certificate step (already extended to non-Windows by phase 113) gains a third option: "Generate a
self-signed certificate now." **On request, not automatic** — resolves the planning doc's open question
3 as leaned: automatic HTTPS every browser flags on first contact is its own kind of bad first
impression. Prints the trust-distribution caveat plainly (every client reaching the console needs to
trust this certificate once) rather than only in docs.

### 7. `config check`

Phase 113's `CertificateCheck` currently has no way to distinguish "this is a self-signed cert we
generated on purpose and it's current" from "someone bound a self-signed cert by accident." Resolves
the planning doc's open question 4: the check gains a marker — the simplest is checking whether the
bound cert's file path matches this phase's own well-known `<repo>/tls/dbdatasync.pfx` path — and
reports `Ok` with "self-signed (managed)" rather than a generic "certificate is self-signed" caution
when it does. A cert at any other path that happens to be self-signed keeps today's behavior
unchanged.

## What this phase does not build

- **A local CA (root + leaf).** Resolves the planning doc's open question 1 against its own stated
  leaning: a local CA is "most of an internal-PKI feature," a genuinely separate and bigger decision
  than a self-signed leaf. This phase ships **a leaf only** — one certificate per deployment,
  re-trusted by every client on each renewal. If the re-trust-on-renewal friction turns out to matter
  in practice, a local CA is a real follow-on, not a gap in this phase.
- **Live certificate hot-swap.** See "Two real gaps," above.
- **ACME (phase 114)** or anything that touches its scope — this phase and phase 114 share the `tls/`
  directory convention and nothing else.
- **A Linux/cross-platform certificate admin UI screen.** `config check` (#7) covers the operational
  need; `AdminCertificatePage` stays Windows-store-shaped.

## How to verify when built

- **`SelfSignedGenerationTests`** — the non-Windows keypair/PFX-write path: SANs include the
  configured host and `localhost`; `NotAfter` matches `--days`; the written PFX is loadable by a plain
  `X509Certificate2` constructor with no password.
- **`SelfSignedCertificateServiceTests`** (fake clock, in-memory notification store, real temp
  `tls/`, the pattern `SchedulerServiceReconcileTests` already established): a cert outside the renewal
  threshold does nothing; one inside it regenerates the file and raises exactly one
  `CertificateExpiring` notification; a regeneration failure logs and does not crash the service; the
  service never touches a certificate at a path other than its own.
- **`CertificateCheckTests`** — extended with the three states: self-signed-and-managed → `Ok` with the
  distinguishing message; self-signed-and-not-at-the-managed-path → today's existing caution,
  unchanged; managed-but-past-`NotAfter` → `Fail`.
- **Integration** (`Category=Integration`, real Kestrel): `config cert new-self-signed` on Linux
  produces a file phase 113's own HTTPS-listener test already proves Kestrel can bind — reuse that
  proof rather than re-deriving it; a real HTTPS request against the running host succeeds with the
  expected SANs (via `HttpClientHandler.ServerCertificateCustomValidationCallback` inspecting the
  presented cert, not disabling validation).
- Manual: `setup`'s new option end to end on a fresh Linux checkout; restart after a forced
  regeneration actually serves the new cert.

## References

- `architecture/planning/done/linux-tls-without-a-reverse-proxy.md` — tier 2's original sketch and open
  questions, resolved above.
- `architecture/implementation/done/phase-113-tls-bring-your-own-certificate.md` — the file-consumption
  path this phase produces input for, and the "no live reload" precedent this phase follows rather
  than re-litigates.
- `src/DbDataSync.Cli/CertCommand.cs` (`NewSelfSigned`) — the Windows generation logic this phase gives
  a second, file-writing branch.
- `src/DbDataSync.Api/Services/CertificateExpiryService.cs` — the shape this phase's new service
  mirrors; explicitly not the class it extends.
