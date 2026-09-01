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

## Next step

Answer the questions above — they're product and scope decisions, and the honest read of them is that
this is likely several phases (HTTPS/Kestrel wiring as a prerequisite if the answer is "bind it directly,"
then generation/enrollment, then renewal, then the admin UI) rather than one. Worth a scoping
conversation before any of it becomes a phase doc.
