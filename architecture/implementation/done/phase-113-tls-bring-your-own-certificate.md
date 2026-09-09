# Phase 113 — TLS on any platform with a certificate you supply

**Status**: Done.
**Plan reference**: `architecture/planning/todo/linux-tls-without-a-reverse-proxy.md`, tier 1.

## What this built

`dbdatasync config cert use-pem`/`use-pfx` — cross-platform, unlike every other `cert` subcommand:

```
dbdatasync config cert use-pem --cert <path> --key <path> [--repo <path>]
dbdatasync config cert use-pfx --pfx <path> [--repo <path>]
```

Both validate the file(s) load (`X509Certificate2.CreateFromPemFile` / `X509CertificateLoader
.LoadPkcs12FromFile`), print the subject/SANs/`NotAfter`, write `Kestrel:Certificates:Default:Path`
(+`:KeyPath` for PEM) via the existing `DbDataSyncConfigFile.SetValue`, and commit — the same shape
`cert bind` already uses for its own four keys, just a different pair/quad of them. Kestrel's own
configuration binder reads these back automatically; no code anywhere had to be added to make that
happen; that's the entire reason this phase's file-based path is as small as it is.

### `CertCommand`'s platform gate

`Run` used to bail off Windows before dispatching at all. Now: `use-pem`/`use-pfx`/`status` run on
any platform; everything else keeps its own `[SupportedOSPlatform("windows")]` and a clear
Windows-only refusal naming the three exceptions. The gate is a plain `if (OperatingSystem
.IsWindows())` wrapping one switch, with a second switch for the non-Windows path — not a combined
condition — because the CA1416 platform-compatibility analyzer only recognises the former as making
the Windows-only calls inside it safe; the more natural-looking `!IsWindows() && sub is not (...)`
produced six warnings for calls the analyzer couldn't prove were unreachable off Windows.

`config cert status` is cross-platform too: it reports the file-based certificate (path, subject,
SANs, `NotAfter`, days remaining) when `Kestrel:Certificates:Default:Path` is set, falls back to the
existing Windows store-based report when running on Windows with nothing file-based configured, and
otherwise says nothing is bound and points at `use-pem`/`use-pfx`.

### Keeping the two binding shapes from coexisting

`Kestrel:Certificates:Default` now has two shapes that can live in the same section: the existing
store-based one (`Subject`/`Store`/`Location`/`AllowInvalid`) and the new file-based one
(`Path`/`KeyPath`). Switching between them (or from PEM to PFX, which drops `KeyPath`) left stale
keys from the old shape behind, which Kestrel's own certificate loader would then try to reconcile —
undocumented, and not a state this codebase should ever deliberately produce. Fixed two ways:

- `DbDataSyncConfigFile.RemoveValue(repoRoot, section, key)` (new) — `SetValue`'s counterpart, needed
  because nothing before this phase ever had to *remove* a key rather than set a new value over it.
- `CertificateBinding.Bind` (store-based) now clears `Path`/`KeyPath` first; `use-pem`/`use-pfx`
  (file-based) clear `Subject`/`Store`/`Location`/`AllowInvalid` first, and `use-pfx` additionally
  clears a stale `KeyPath` a prior `use-pem` may have left.

### A `Certificate` check in `dbdatasync config check`

The plan doc assumed `doctor`'s "auth/TLS" check already validated a bound HTTPS certificate — it
didn't; no such check existed in `ReadinessChecks.cs` before this phase (see Decisions). Added
`CertificateCheck`: `Ok` when no file-based certificate is configured; otherwise the file exists,
loads, its SANs cover the console URL's host, and it isn't within `CertificateOptions
.ExpiryWarningDays` of `NotAfter` (`Warn`) or already past it (`Fail`). A Windows store-based binding
isn't covered — `CertificateExpiryService` already watches that path independently, and duplicating
it here would risk two different answers about the same certificate.

### `setup`'s certificate step, extended to non-Windows

The walk-through's TLS step was Windows-only before this phase. Added a parallel non-Windows branch:
*"Point Kestrel at a certificate file (PEM), e.g. from certbot?"* → prompts the two paths → prints the
matching `dbdatasync config cert use-pem ...` command to run — printed, not invoked, matching every
other certificate step in this same walk-through (including the pre-existing Windows ones), so the
step stays consistent rather than this one path alone driving `CertCommand.UsePem`'s logic directly.

### Tests

- **`CertUsePemTests`** (new, cross-platform) — valid PEM pair writes both keys and commits; a missing
  file fails and writes nothing; an encrypted private key is refused with a clear message and writes
  nothing; a valid unprotected PFX writes `Path` only; a password-protected PFX is refused the same
  way; switching PEM → PFX removes the stale `KeyPath`; `status` reports the file certificate, and
  reports "nothing bound, try use-pem/use-pfx" when nothing is configured.
- **`CertificateBindingTests`** — one new test: binding a store-based certificate after a prior
  file-based configuration removes `Path`/`KeyPath`.
- **`DbDataSyncConfigFileTests`** — four new tests for `RemoveValue`: no file, key absent, removes only
  the named key, and preserves comments/other sections around it.
- **`ReadinessChecksTests`** — four new tests for `CertificateCheck`: no certificate configured (`Ok`),
  configured file missing (`Fail`), SAN covers the console URL host (`Ok`), SAN does not (`Fail`).
  Written to a 90-day-validity test certificate specifically so the "days remaining" branch can never
  accidentally land in `Warn` territory (the default expiry-warning window is 30 days) and make the
  `Ok` assertions flaky.
- **`ConfigCommandTests`** — the existing `Cert_ForwardsToCertCommand` routing test switched from
  `cert status` (no longer Windows-only, so it stopped proving anything about the gate) to `cert list`
  (still Windows-only).
- **`SetupCommandTests`** — every full-walk-through script gained one more scripted answer for the new
  non-Windows TLS-file prompt.
- Manual/integration: full solution `Category=Integration` run green (see How it was verified).

## Decisions

- **No encrypted-key or password-protected-PFX support.** The plan doc's own §1 open question
  ("does Kestrel's `reloadOnChange` reliably pick up an atomically-renamed cert file… this needs
  verifying, not assuming") signals real uncertainty about how Kestrel's *automatic*
  `Path`/`KeyPath`/`Password` configuration binding actually behaves in practice. Splicing a
  secret-store-resolved password in the way `StateDatabase.Factory` splices the state connection
  string's password would need Kestrel's certificate to be loaded and handed to it *explicitly*
  (a `ServerCertificateSelector`/`ConfigureHttpsDefaults` hook) rather than left to that automatic
  binding — a real, larger feature, and one this environment has no way to verify against a real
  Kestrel HTTPS listener with any confidence. Refusing an encrypted key/protected PFX loudly, with a
  concrete "decrypt it first" instruction, is honest about what was actually built and verified rather
  than shipping unverified secret-splicing logic. The most common real case — a certbot-issued PEM
  cert — already has an unencrypted key by default, so this does not block the phase's primary use
  case.
- **§3 ("reload on renewal without a restart") was not built at all.** Investigating this surfaced
  that `dbdatasync.config.yaml` isn't hot-reloaded by anything in this codebase today —
  `DbDataSyncConfigFileSource` is a one-time snapshot taken at startup, disconnected from the
  `PhysicalFileProvider`-based file-watching `appsettings.json`'s own hot-reload relies on. Every
  existing `Kestrel:Certificates:Default:*` change (including a `cert bind`) already requires a
  restart — stated explicitly in `CertificateBinding.Bind`'s own doc comment. A renewed certificate
  from `use-pem`/`use-pfx` needing the same restart is consistent with that, not a regression this
  phase introduced. `dbdatasync config cert reload` (a loopback admin endpoint) and Kestrel's
  `reloadOnChange` spike are both left for whenever phase 114's ACME work needs the same seam for
  real, at which point building it once for both is worth it — building it now, unverified, for this
  phase alone was not.
- **The plan doc's claim that a "config check`'s existing 'auth / TLS' check … already validates a
  bound HTTPS cert on Windows" was wrong** — no such check existed in `ReadinessChecks.cs` before this
  phase. Built `CertificateCheck` fresh rather than "extending" something that wasn't there, and
  scoped it to the file-based case only (the Windows store-based case remains uncovered by `config
  check`, same as before this phase — not a regression, since `CertificateExpiryService` already
  covers that path independently).
- **`setup`'s new TLS-file step prints the command rather than invoking `CertCommand.UsePem` directly**,
  even though `use-pem` is cross-platform and testable (unlike the Windows-only steps this mirrors).
  Consistency with the rest of that walk-through's certificate handling won out over exploiting the
  one case where direct invocation was possible — a future pass that reconsiders *all* of setup's
  cert steps together is better than this one step alone diverging from its neighbors.
- **No `--password-ref` flag**, despite an earlier draft of the plan doc sketching one. Every other
  secret this codebase writes into `dbdatasync.config.yaml` uses one fixed, hardcoded
  `SecretRefs.ForAppSetting` ref name (`stateConnectionString` being the only precedent) — never an
  operator-chosen ref name passed on the command line. Moot in the end, since no password support was
  built at all this phase (see above), but worth recording that a customizable ref name would have
  been a new pattern, not a reuse of an existing one.

## What this phase does not build

- Issuing or renewing anything — this phase only consumes a file someone else manages (phase 114:
  ACME; the tier-2 planning doc: a self-signed cert DbDataSync generates and rotates itself).
- A Linux equivalent of `cert enroll`/AD CS.
- Encrypted PEM keys or password-protected PFX files — see Decisions.
- Reload-without-restart / `cert reload` — see Decisions.
- `docs/getting-started.md`'s Linux section — that file does not exist yet in this repository.

## How it was verified

- `dotnet build DbDataSync.slnx` clean.
- Full `dotnet test --filter "Category!=Integration"` green solution-wide.
- Full `dotnet test --filter "Category=Integration"` green solution-wide, against the real Docker
  containers already running in this environment.
- Every new behavior above is covered by a real test against real files/certificates (self-signed,
  generated in-test via the already-cross-platform `CertificateBuilder`) — no mocking of the
  certificate-loading APIs.
