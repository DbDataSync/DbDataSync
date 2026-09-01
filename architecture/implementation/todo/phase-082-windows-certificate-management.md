# Phase 82 — Windows certificate management: issuance, installation, binding, expiry

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/windows-tls-certificate-management.md`, resolved
2026-09-01. Depends on phase 79 (`datasync.config.yaml` and its writer) for binding, and on phases 77/80
(the notification core) for expiry. Phase 83 puts a screen on top of this; **this phase is complete and
usable without it**, deliberately.

## What this phase will build

Today there is no TLS story at all. `DataSyncHost.cs:204-208` is the whole of it — a guarded
`UseHttpsRedirection()`, no `UseHttps`, no certificate binding. HTTPS works only if an operator
separately configured Kestrel's own keys, used the ASP.NET Core dev certificate, or terminated TLS in
front of the app.

This phase makes a Windows deployment able to obtain, install, bind and keep track of a server
certificate, entirely from the CLI.

### Why the CLI, and why it must be self-sufficient

**The admin screen is served over the connection the certificate secures.** Requiring the SPA to
configure TLS for the first time is circular — the operator would need HTTPS working in order to set up
HTTPS. So every capability here is reachable from `datasync cert …` with no browser involved, and phase
83's screen is a second door onto the same operations rather than the only one.

This is the same reasoning phase 51 applied to `datasync service install`: the bootstrap path cannot
depend on the thing being bootstrapped.

### No listener wiring — the prerequisite that turned out not to exist

Kestrel's certificate configuration accepts a **store lookup**, not only a file path:

```yaml
Kestrel:
  Certificates:
    Default:
      Subject: datasync.corp.example.com
      Store: My
      Location: LocalMachine
      AllowInvalid: false          # true is required for a self-signed certificate
```

So installing to `LocalMachine\My` is enough for Kestrel to serve it, **and** for IIS or a reverse proxy
in front of the app to use the same certificate. No `ListenOptions.UseHttps`, no changes to
`DataSyncHost`'s pipeline beyond what already exists.

`AllowInvalid: true` is genuinely required for a self-signed certificate — Kestrel validates the chain on
load and refuses one it cannot build. Writing it silently would be wrong; the CLI reports that it set it
and why, because an operator who later installs a CA-issued certificate should know to turn it back off.

### `src/DataSync.Certificates` — a new project

Small, and separate because it is the only Windows-only code in the solution outside
`ServiceCommand`. Everything public is marked `[SupportedOSPlatform("windows")]`, and callers gate on
`OperatingSystem.IsWindows()`.

**The Linux container image from phase 51 must still build and run.** That is a hard constraint, not a
nicety: the feature reports itself unavailable on a non-Windows host rather than throwing, and no code
path in `DataSyncHost` becomes conditional on it.

```csharp
public sealed record CertificateSpec(
    string SubjectCommonName,
    IReadOnlyList<string> DnsNames,          // never empty — see below
    int ValidityDays,
    string? FriendlyName);
```

**`DnsNames` empty is rejected at construction.** A certificate with only a CN and no Subject Alternative
Name is rejected by every current browser — Chrome removed CN fallback years ago — so a self-signed
certificate generated without SANs would install cleanly, bind cleanly, and fail in the browser with an
error naming none of that. The validation exists so the failure happens at the point someone can fix it.

### Issuance — two paths, one shared spine

Both build the same request and end in the same install-and-bind. Only the middle differs.

**Self-signed** — `CertificateRequest.CreateSelfSigned`, in-box .NET, no PowerShell shelling and no
`New-SelfSignedCertificate`:

- `SubjectAlternativeNameBuilder` for every DNS name (and the machine's own FQDN and hostname by default)
- `X509KeyUsageFlags.DigitalSignature | KeyEncipherment`
- an EKU extension carrying `1.3.6.1.5.5.7.3.1` (server authentication) — without it Windows will not
  offer the certificate for TLS server use
- RSA 2048 by default

**AD CS enrollment** — `CertificateRequest.CreateSigningRequest()` for the CSR, submitted to the
configured enterprise CA. Configuration is two settings, both plain text and both fine in a git-tracked
`datasync.config.yaml`:

- `DataSync:Certificates:CaConfig` — the CA configuration string, `CASERVER\CA Name`
- `DataSync:Certificates:Template` — the certificate template name

Submission goes through the `CertificateAuthority.Request` COM interface (`ICertRequest`), resolved by
ProgID and called dynamically, rather than shelling `certreq.exe`. The reason is error handling:
`certreq` reports failure by printing to stdout and requires temp files for the request and the response,
while the COM call returns a disposition code that distinguishes the cases that matter.

**Pending issuance is a real state and is handled, not treated as failure.** A template requiring
certificate-manager approval returns `CR_DISP_UNDER_SUBMISSION` with a request id. The CLI records that
id and exits saying enrollment is pending; `datasync cert retrieve` collects it later. Treating pending
as an error would make this feature unusable in exactly the environments that most want a CA — the ones
strict enough to require approval.

**Templates are listed on a best-effort basis.** `datasync cert templates` queries the AD configuration
partition for the templates *published on the configured CA* — the `certificateTemplates` attribute of
`CN=<CA>,CN=Enrollment Services,CN=Public Key Services,CN=Services,CN=Configuration,…` — rather than
every template in the forest, since a template the CA does not offer cannot be enrolled against and
listing it would only produce a confusing failure later.

The word doing the work is **attempt**. This query fails in ordinary, non-exceptional ways: the host is
not domain-joined, LDAP is unreachable, the caller lacks read rights on the configuration partition, or
`CaConfig` is not set yet. Every one of those returns an **empty list and a stated reason**, never an
exception and never a blocked command — `--template` remains free text, and enrollment works identically
whether the listing succeeded or not. A discovery convenience that can take the feature down with it
would be worse than no discovery.

Adds `System.DirectoryServices` (Windows-only) as a dependency of `DataSync.Certificates`, which is
already a Windows-only project, so it widens nothing.

### Installation, and the ACL that is the whole point

Install into `LocalMachine\My`, private key **non-exportable**.

Then grant read on the private key to the service account. This is the part that the original ask did not
mention and that will otherwise generate a support ticket per domain install:

> A certificate in `LocalMachine\My` has a private key ACL granting SYSTEM and Administrators. A service
> running as a domain account — which `datasync service install --account` supports and phase 51 flags as
> load-bearing — cannot read it, and the symptom is a TLS handshake failure at startup that says nothing
> about permissions.

`PrivateKeyAccess.Grant(certificate, account)` reaches the CNG key via `GetRSAPrivateKey()` → `RSACng`
→ `CngKey`, and applies a read ACE to the key file's DACL.

**Which account** is resolved in this order: an explicit `--account`, else the account of the installed
DataSync service read back from `sc.exe qc` (the same tool `datasync service status` already parses),
else `LocalSystem`, which needs no grant. Reading it back from the installed service is what stops the
two commands from disagreeing about who the app runs as.

### Binding

Writes the four `Kestrel:Certificates:Default:*` keys into `datasync.config.yaml` via
`DataSyncConfigFile.SetValue`, then commits through `new GitCommitService(root).CommitChanges(...)` —
the same two-step `ServeCommand` already uses when it writes the starter file, so the change is
git-tracked and attributed like every other config write rather than appearing as an untracked local
edit.

**Nothing takes effect until the service restarts** — `ApiOptions` and Kestrel's certificate are both
resolved once at startup. The CLI says so on completion, in the same terms phase 81's banner uses. It
does not offer to restart the service itself: a restart interrupts in-flight replication runs, and
choosing when to take that is the operator's call.

### Renewal is a re-enroll

`datasync cert renew` builds a **new** request carrying the same subject and SANs as the bound
certificate and submits it as an ordinary enrollment — it does not construct an AD CS renewal request
signed by the expiring certificate.

Re-enroll always works, on every template, and needs no special handling when the existing certificate
has already expired (a renewal signed by a dead certificate is the case that fails when it is needed
most). A signed renewal's advantage is that some templates let it skip re-approval; that is real, and it
is precisely the property a strict environment cares about — so if a deployment turns out to need it,
it is an additive second code path behind the same command, not a redesign. Re-enroll is the default
and, for now, the only behaviour.

Renewal that lands in `CR_DISP_UNDER_SUBMISSION` behaves exactly like a first enrollment: the request id
is recorded and `datasync cert retrieve` collects it. The old certificate stays bound and serving until
the new one is retrieved and bound, which is the correct ordering — nothing is unbound in the hope that
a replacement arrives.

### Expiry

A hosted service in the API, on the same pattern as `SchedulerService`, checks the bound certificate once
a day and raises through the existing notification core:

- `CertificateExpiring` — a new constant beside `RunFailed`, `ReplicationPaused` and `PositionExpired` in
  `NotificationStore`, raised at `DataSync:Certificates:ExpiryWarningDays` (default 30) and not repeated
  more than once a day
- `CertificateExpired` — once past `NotAfter`

**A new constant and a producer, not a new mechanism.** "Warn before X expires" is the shape phases 77
and 80 already built for watermark position and pause duration; inventing a second path for the same
shape would be the mistake.

It also checks, on the same pass, that the service account can still read the private key — an ACL can be
removed by a certificate re-issue or a group policy, and that failure is otherwise invisible until the
next restart.

### CLI

```
datasync cert status                 # what is bound, thumbprint, SANs, NotAfter, days remaining,
                                     #   whether the service account can read the key
datasync cert list                   # server-auth certificates in LocalMachine\My
datasync cert new-self-signed --dns … [--days] [--account]
datasync cert enroll --dns … [--ca] [--template] [--account]
datasync cert renew                  # re-enroll with the bound certificate's subject and SANs
datasync cert retrieve --request-id  # collect a pending AD CS issuance
datasync cert templates              # best-effort: templates published on the configured CA
datasync cert bind --thumbprint      # bind one already in the store
```

`--account` on the issuing commands, because issuing and granting are one operation from the operator's
point of view and splitting them is how the grant gets forgotten.

## How it will be verified

**Unit** (`tests/DataSync.Certificates.Tests`, runnable anywhere for the parts that are pure)
- a self-signed certificate carries every requested DNS name as a SAN, the server-auth EKU, and the
  requested validity
- a `CertificateSpec` with no DNS names is rejected, naming the browser behaviour as the reason
- the CSR round-trips subject and SANs
- expiry evaluation: a certificate 31 days out raises nothing, 29 days raises `CertificateExpiring` once,
  a second check the same day raises nothing further, past `NotAfter` raises `CertificateExpired`
- `renew` builds a request carrying the bound certificate's subject and every one of its SANs, including
  when that certificate is already past `NotAfter` — the case a signed renewal could not handle
- the bound certificate stays bound through a renewal that comes back pending; nothing is unbound in
  anticipation of a replacement
- template listing returns empty **and a reason** for each failure path — not domain-joined, LDAP
  unreachable, access denied, `CaConfig` unset — and never throws; enrollment with an explicit
  `--template` succeeds against an empty listing
- on a non-Windows host every entry point reports unavailable rather than throwing

**Windows-only** (`Category=Windows`)
- install into `CurrentUser\My` (so the test needs no elevation), then assert the private key DACL
  contains a read ACE for a named account after `PrivateKeyAccess.Grant`
- `bind` writes the four Kestrel keys into a temp `datasync.config.yaml` and reports restart-required

**Honest about what is not tested.** AD CS enrollment has no test — it needs an enterprise CA, and the
repo's CI has no Windows runner today. Phase 51 set the precedent by stating plainly that *"Windows
service registration is untested"* rather than writing a test asserting that `sc.exe` would have been
called, and the same applies here: the enrollment path is exercised manually against a real CA, and the
phase doc records that rather than implying coverage that does not exist.

## Decisions taken before implementation

- **Store lookup, not a PFX path.** Beyond removing the listener-wiring prerequisite, it means **no
  certificate password is ever persisted by DataSync** — the private key never leaves the Windows store.
  A `Path` + `Password` binding would have put a PFX password into config or the secret store.
- **Restart required, not live rotation.** `ServerCertificateSelector` would give a live swap for one
  `UseHttps` call and a volatile field, and was rejected only because it is the one thing that would
  force us to own the listener wiring. Recorded in the planning doc so it is not re-derived; it stays
  cheap to add if interrupted runs during rotation become a real complaint.
- **Pending enrollment is a first-class state**, not an error.
- **Renewal is a re-enroll**, not a signed AD CS renewal — it always works, including from an already
  expired certificate, and a signed renewal stays additive behind the same command if a template ever
  needs it.
- **Template listing is best-effort and never blocking.** Empty plus a reason on every failure path;
  `--template` stays free text regardless.
- **Non-exportable private keys.** Nothing in this feature exports one, and nothing should.

## Out of scope

- **Live certificate rotation without a restart.** Above.
- **ACME / Let's Encrypt**, and any public-CA integration. The ask is a Windows enterprise CA.
- **Client certificate authentication.** Phases 52/53 own authentication; this is server TLS only.
- **TLS for the source and target *database* connections.** A different concern with a different trust
  story (`Encrypt=true`, server certificate validation), belonging to `ConnectionConfig`, not here.
- **Certificate management on Linux or macOS.** The feature reports unavailable; the container image is
  expected to terminate TLS in front of itself, which is the normal container topology anyway.
- **Configuring the IIS binding.** Installing to the store is what IIS needs from us; pointing an IIS
  site at it is IIS administration.

## Open questions to resolve during implementation

- **`AllowInvalid` when a self-signed certificate is later replaced by a CA-issued one.** The bind
  command knows which kind it just installed and could clear the flag. Doing it silently changes a
  security-relevant setting the operator did not ask about in that moment; reporting it and leaving it is
  the conservative option and probably right.
