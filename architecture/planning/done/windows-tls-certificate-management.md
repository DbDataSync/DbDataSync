# TLS certificate generation/renewal from the admin screen (Windows)

**Status: proposal, not agreed — and the questions below are the reason.** Raised alongside the admin
config screen (`phase-081-admin-config-screen.md`) but a genuinely separate, much larger problem — kept
apart per this folder's own rule about not holding one thought hostage to another's least-understood
part.

## The ask

On Windows, generate and renew TLS certificates from the admin screen, with support for a configured
Windows CA.

## What's actually there today

Nothing. The app calls `app.UseHttpsRedirection()` and that's the entire TLS-related code in
`DataSyncHost.cs` — no Kestrel certificate binding, no `ListenOptions.UseHttps`, nothing. Today, HTTPS
only works at all if an operator has separately configured Kestrel's own built-in `Kestrel:Certificates
:Default:Path`/`Password` keys (which, notably, would already be reachable through phase 79's config file
or admin screen once those exist, without any code from this item), relies on the ASP.NET Core dev
certificate in Development, or terminates TLS in front of the app entirely (IIS, a reverse proxy). So
this is new, standalone scope — not an extension of an existing certificate story, because there isn't
one.

## Why no implementation phase was written

Because each of these changes what gets built, and the answer isn't inferable from the ask as given:

- **Self-signed, or issued by a real CA?** "Generating" a cert with no CA involved is a small,
  well-understood operation (the equivalent of `New-SelfSignedCertificate`) — but a self-signed cert
  isn't trusted by anything unless it's separately distributed to every client, which is most of the
  reason to want a CA-issued one in the first place. "Support a configured Windows CA" reads as wanting
  the latter, which is a materially bigger integration: build a CSR, submit it, wait for or poll for
  issuance, retrieve and install the result.
- **What does "a configured Windows CA" mean, concretely?** An enterprise Active Directory Certificate
  Services CA reachable via `certreq`/`certutil` (or the CertEnroll COM interfaces) with an admin-chosen
  CA server and certificate template? Something else? This decides the entire implementation approach,
  not a detail within one.
- **Where does the resulting certificate actually apply?** Binding it to Kestrel directly means this
  phase also has to build real HTTPS listener wiring that doesn't exist yet (see above) — a prerequisite
  in its own right. Installing it into the Windows certificate store and leaving IIS/a reverse proxy to
  use it from there is a much smaller feature that never touches this app's own HTTP pipeline.
- **Automatic renewal, or a manual "renew now" button?** Automatic implies tracking expiry and deciding
  when to act — which could reuse the notification core phases 77/80 are already building for "warn
  before X expires" elsewhere (watermark position, pause duration) rather than inventing a second
  mechanism for the same shape of problem.
- **Private key and service account.** The service can run as `LocalSystem` or a domain account
  (`datasync service install --account`); a certificate's private key in the machine store needs to be
  readable by whichever account Kestrel runs as. Does this feature manage that ACL as part of
  "generating," or is that left to whoever already manages the service account?
- **Does applying a new/renewed cert need a service restart**, matching the restart-required pattern
  every other config change in this app has, or is a live Kestrel cert swap (technically possible, not
  trivial) worth building deliberately? Defaulting to "requires restart" without deciding this is
  probably wrong to do silently.

---

# Outcome — resolved 2026-09-01

Scoped in conversation. The work is **two** phases, not the four this doc guessed at, because one of its
premises turned out to be wrong:

- **`implementation/todo/phase-082-windows-certificate-management.md`** — issuance (self-signed and AD CS),
  installation, the private-key ACL, binding, expiry notification, and the `datasync cert` CLI.
- **`implementation/todo/phase-083-admin-certificate-screen.md`** — the Certificates section of the Admin
  area, on top of phase 81.

## The premise that was wrong, and why it matters

This doc treats "bind it to Kestrel" and "install it into the Windows store and let IIS or a proxy use it"
as alternatives, with the former requiring HTTPS listener wiring as a prerequisite phase. **They are not
alternatives.** Kestrel's built-in certificate configuration accepts `Subject` + `Store` + `Location`, not
only `Path` + `Password` — so a certificate installed into `LocalMachine\My` is consumed by Kestrel with
**no listener code at all**, and simultaneously serves IIS or a reverse proxy in front of it. The
prerequisite disappears; all three topologies are covered by one install.

It also removes a secret. A `Path` + `Password` binding puts a PFX password into configuration or the
secret store; store-by-subject means the private key never leaves the Windows store and **no certificate
password is ever persisted anywhere by DataSync**. That is a better security posture arrived at by
accident, and it is worth stating as a reason not to revisit the decision casually.

## The decisions

1. **Install to `LocalMachine\My`; Kestrel reads it by subject.** No listener wiring. The three
   `Kestrel:Certificates:Default:*` keys are written into `datasync.config.yaml` through phase 79's
   writer, like any other setting.
2. **Both issuance paths.** Self-signed via `CertificateRequest.CreateSelfSigned` (in-box .NET — no
   `New-SelfSignedCertificate` shelling) for standalone and lab installs; AD CS enrollment for domain
   deployments. They share CSR construction, installation, the ACL grant and binding — the two paths
   differ only in what happens between "build the request" and "have a certificate".
3. **Notify, admin renews.** Expiry is tracked and raises a `CertificateExpiring` notification through
   the phase 77/80 core; renewal is a deliberate action. `NotificationStore` already keys events as
   string constants (`RunFailed`, `ReplicationPaused`, `PositionExpired`), so this is a new constant and
   a producer, not a new mechanism.
4. **Applying requires a restart**, matching every other config change and phase 81's restart-required
   banner. Live rotation via `ServerCertificateSelector` is real and was considered; it was rejected
   because it is the only thing that would have forced us to own the listener wiring, and buying that
   complexity to avoid an occasional scheduled restart is a bad trade at this stage. Recorded here so
   the option is not re-derived from scratch: it stays available and costs one `UseHttps` call plus a
   volatile field, if interrupted runs during rotation ever become a real complaint.

## The failure this feature exists to prevent, which the ask did not mention

A certificate enrolled into `LocalMachine\My` has a private key ACL granting SYSTEM and Administrators.
A service running as a domain account — which `datasync service install --account` explicitly supports,
and which phase 51 already flags as load-bearing — **cannot read it**, and the symptom is an opaque TLS
handshake failure at startup rather than anything that says "permission". Phase 82 therefore owns the
ACL grant rather than leaving it to whoever manages the service account, and phase 83 surfaces
"can the service account actually read this key" as a state on the screen.

## One thing settled that the doc did not ask

**The CLI comes first, and must be able to do the whole job alone.** The admin screen is served over the
very connection the certificate secures, so requiring the SPA to configure TLS for the first time is
circular. That is the reason for the 82/83 split, rather than it being merely a core/UI convenience.
