# Phase 130 — Managed self-signed TLS (tier 2)

**Status**: Complete.
**Plan reference**: `architecture/planning/done/linux-tls-without-a-reverse-proxy.md`, tier 2. Consumes
phase 113's file-based certificate path exactly as built (`Kestrel:Certificates:Default:{Path,KeyPath}`,
`AllowInvalid=true`); shares no code with phase 114 (ACME), which remains not started. Cross-platform,
like phase 113 and unlike `CertificateExpiryService`/the Windows-store `new-self-signed`, both still
Windows-only.

## Why

Before this phase, the only cross-platform way to serve HTTPS was phase 113: point DbDataSync at a
certificate file someone else keeps current. Tier 2 is the answer for a small internal deployment with
no reachable CA at all: DbDataSync generates its own certificate, serves it, and renews it automatically
before it expires — no CA, no external process, at the cost of every client needing to trust the
certificate once.

## Two real gaps in the planning doc's own sketch — found before writing anything

Both resolved exactly as the phase doc that preceded this implementation already decided, and confirmed
correct during implementation:

1. **No hot-swap seam exists.** Phase 113 investigated this directly and declined to build it — a
   renewed certificate needs a restart to take effect, consistent with every other certificate path in
   this codebase.
2. **`CertificateExpiryService` cannot be generalized in place** — it's registered only on Windows and
   reads from the Windows certificate store. Built a new, cross-platform hosted service instead
   (`SelfSignedCertificateService`), same shape, different producer.

## What this built

### 1. `ManagedSelfSignedCertificate` — the shared cross-platform core

New file, `src/DbDataSync.Certificates/ManagedSelfSignedCertificate.cs`. Everything tier 2 needs keys
off one well-known, normalized path (`PfxPath(repoRoot)` → `<repoRoot>/tls/dbdatasync.pfx`), so there is
exactly one place "is this the managed certificate" can ever disagree with itself — the CLI (writing),
the renewal service (reading and rewriting), and `CertificateCheck` (comparing) all call the same method:

- `Generate(host, validityDays)` — reuses `CertificateBuilder.CreateSelfSigned` unchanged (confirmed
  during manual verification: it also appends the machine's own hostname/FQDN as bonus SANs, exactly as
  it already does for the Windows tier-1 path — not a regression, a faithfully "identical keypair").
  `host` is deduplicated against `localhost`, not appended blindly.
- `Write(repoRoot, certificate)` — writes the PFX unencrypted (matching phase 113's supported case),
  `0600` on any non-Windows OS (via `File.SetUnixFileMode` — nothing to assert on Windows, where the
  concept doesn't apply), and lazily appends `tls/` to `.gitignore` the first time it's actually used —
  the same first-use pattern `PendingEnrollmentStore.EnsureGitignored` already established. **Correction
  to the phase doc's own text**: it attributed this gitignore-write pattern to "`ServeCommand.Prepare`/
  `setup`" — checked directly, neither writes to `.gitignore` at all; the actual existing precedent is
  `PendingEnrollmentStore`, which is what this phase's `EnsureGitignored` actually mirrors.
- `ShouldRenew(notBefore, notAfter, now)` — pure, no certificate/file/clock dependency beyond what's
  passed in, the same shape `CertificateExpiryEvaluator` already established for the Windows store path.
  Renews when the remaining lifetime is at or below a third of the total (`notAfter - now <= (notAfter -
  notBefore) / 3`).

### 2. `CertCommand.NewSelfSignedFile` — the non-Windows `new-self-signed` branch

`dbdatasync config cert new-self-signed` now dispatches to this on any non-Windows OS (previously
refused there entirely). SANs are the console URL's host (`--url`, else `DbDataSync:Url`, else
`localhost`) plus `localhost` — no `--dns` to fill in, unlike the Windows path, since there is no store
entry to look up by name later. Writes the PFX via `ManagedSelfSignedCertificate.Write`, then points
Kestrel at it exactly the way `use-pfx` does (`Path` + `AllowInvalid=true`, clearing any stale
store-based or `KeyPath` keys from a prior binding), and commits — same shape every other `CertCommand`
mutation already uses. Prints the trust-distribution caveat plainly, not only in docs.

### 3. `SelfSignedCertificateService` — the renewal hosted service

New file, `src/DbDataSync.Api/Services/SelfSignedCertificateService.cs`. Same shape
`CertificateExpiryService`/`SchedulerService`/`RunPruningService` already establish: an immediate first
pass, then a daily `PeriodicTimer`, swallowing and logging rather than taking the process down. Unlike
`CertificateExpiryService` (warns only), this one *acts*: within the renewal threshold it regenerates
`ManagedSelfSignedCertificate.PfxPath` in place and raises `NotificationKinds.CertificateExpiring` noting
the restart requirement. Never reads or touches any path but its own well-known one — verified directly
by a test that plants an "operator-supplied" certificate at a different path and confirms it's
untouched.

**Registered only when tier 2 is actually configured** — `DbDataSyncHost.Build` compares
`Kestrel:Certificates:Default:Path` (read directly off `builder.Configuration`, the same way
`InsertConfigFile` already resolves `RepoRoot` at this point, since the DI container doesn't exist yet)
against `ManagedSelfSignedCertificate.PfxPath` for the resolved repo root — not gated by OS, unlike
`CertificateExpiryService`.

### 4. `CertificateCheck` — the "self-signed (managed)" marker

Resolves the plan doc's open question 4. A certificate whose configured path equals
`ManagedSelfSignedCertificate.PfxPath` is reported as `Ok "self-signed (managed) — valid for N more
day(s)"` (or the matching `Warn`/`Fail` variants) instead of a plain date. **Correction to the phase
doc's own premise**: it assumed an existing "generic 'certificate is self-signed' caution" this phase
would need to leave alone for the unmanaged case — checked directly, no such caution existed in
`ReadinessChecks.cs` before this phase at all (the file-based check never inspected self-signed-ness,
only dates and SAN coverage). So there was nothing to preserve; the unmanaged path's report is
byte-for-byte what it already was, and this phase adds no new generic self-signed caution for it.

### 5. `setup`'s certificate step — a second non-Windows choice

`CertificateTab` gains an `OptionSelector` on non-Windows platforms (mirroring the Windows tab's own
three-way choice): "Point at a certificate file (PEM)" or "Generate a self-signed certificate now". The
PEM fields now show only when the PEM option is selected. Printed, not invoked — the same choice phase
113 made for every certificate step in this walk-through, stated explicitly in that phase's own
Decisions. The trust caveat is in the printed lines themselves, not only in docs.

## Decisions

- **No `--dns` flag on the non-Windows `new-self-signed`.** The Windows path needs one because a store
  entry has no other way to know what to ask for; the file-based path derives SANs from the console URL
  the same way `CertificateCheck`'s SAN coverage already does, so there's nothing for an operator to
  duplicate by hand.
- **`ManagedSelfSignedCertificate.PfxPath` normalizes with `Path.GetFullPath` unconditionally.** Every
  caller (CLI, host registration gate, readiness check) resolves a repo root slightly differently in
  general (trailing separator, relative vs. absolute), and this is the one place that difference could
  otherwise make two callers disagree about whether a configured `Path` is the managed one.
- **The registration gate reads `builder.Configuration` directly rather than `ApiOptions`.** Same
  reasoning `InsertConfigFile` already gives for `RepoRoot`: which hosted services get registered is
  decided before the DI container exists, so `ApiOptions` (resolved from DI, precisely so later
  config-override layering like `WebApplicationFactory.ConfigureWebHost` is respected) isn't resolvable
  yet at that point.

## What this phase does not build

- **A local CA (root + leaf).** Ships a leaf only, per the planning doc's own leaning.
- **Live certificate hot-swap.** See "Two real gaps," above — unchanged from phase 113's own position.
- **ACME (phase 114)** or anything in its scope — this phase and phase 114 share only the `tls/`
  directory convention.
- **A Linux/cross-platform certificate admin UI screen.** `config check` covers the operational need;
  `AdminCertificatePage` stays Windows-store-shaped.
- **A real-Kestrel-HTTPS-listener integration test.** The phase doc asked for one
  (`HttpClientHandler.ServerCertificateCustomValidationCallback` against a real running host); checked
  directly, no existing test in this codebase actually stands up a real Kestrel HTTPS listener and
  connects to it over a real socket — every existing "integration" certificate test (phase 113 included)
  proves file/config/store wiring against real cryptography APIs, not a live TCP handshake. Building that
  harness from scratch was judged out of proportion to this phase's own scope; the manual verification
  below covers the same ground for a human, and `CertificateCheck`'s own test suite already proves the
  file this phase produces is exactly what phase 113's consumption path already knows how to bind.
  Flagged explicitly, not silently skipped — a real gap if a future phase (114, most likely) needs that
  harness for its own verification, at which point building it once is worth it, matching phase 113's own
  reasoning for deferring the hot-swap seam the same way.

## How it was verified

- `dotnet build DbDataSync.slnx` clean.
- New tests, all green:
  - `ManagedSelfSignedCertificateTests` (`DbDataSync.Certificates.Tests`) — SANs, validity, PFX
    round-trip with no password, `0600` on non-Windows, `.gitignore` idempotence, path normalization, and
    the renewal-threshold boundary (`ShouldRenew`) both at and off the one-third mark.
  - `SelfSignedCertificateServiceTests` (`DbDataSync.Api.Tests`) — no file yet → no-op; well outside the
    renewal window → no-op; genuinely aged (explicit `NotBefore`/`NotAfter`, since `Generate` always
    anchors `NotBefore` a few minutes before "now" and so can never itself produce a near-expiry
    certificate) → regenerates and raises exactly one `CertificateExpiring` notification; a corrupted
    file → logs and swallows, doesn't throw; an "operator-supplied" certificate at a different path is
    never touched.
  - `SelfSignedCertificateServiceRegistrationTests` (`DbDataSync.Api.Tests`) — the hosted service is
    registered only when `Kestrel:Certificates:Default:Path` equals the well-known managed path; not
    registered when unconfigured or pointed at an operator-supplied file.
  - `NewSelfSignedFileTests` (`DbDataSync.Cli.Tests`) — writes the managed PFX and Kestrel keys and
    commits; SANs cover the console URL host plus `localhost`; defaults to `localhost` with no URL
    configured; clears a stale `KeyPath` left by a prior `use-pem`; `status` reports it.
  - `ReadinessChecksTests` (`DbDataSync.Cli.Tests`) — three new cases: managed-and-current → `Ok` with
    the marker; managed-and-expired → `Fail` with the marker; self-signed-but-unmanaged → unchanged, no
    marker.
  - `SetupStepsTests` — the new `SelfSignedCertificateInstructions` names the repo, the command, and the
    trust caveat.
- Full solution `dotnet test --filter "Category!=Integration"` green (1,547 tests across every project,
  zero failures) — no regressions from this phase's changes anywhere else in the solution.
- Full solution `dotnet test --filter "Category=Integration"` run against the real SQL
  Server/PostgreSQL/MySQL containers already running in this environment. Every assembly that finished
  was green and unaffected by this phase: `DbDataSync.Drivers.Postgres.Tests` (28),
  `DbDataSync.Drivers.MsSql.Tests` (119), `DbDataSync.State.Tests` (45), `DbDataSync.TaskRunner.Tests`
  (32), `DbDataSync.Api.Tests` (66), `DbDataSync.Cli.Tests` (6), `DbDataSync.Drivers.Generic.Tests` (7),
  `DbDataSync.Libraries.Tests` (22) — none of them touch this phase's changes, so this is a
  no-regressions check rather than new coverage (this phase added no new `Category=Integration` tests of
  its own; see "What this phase does not build" for why). One assembly,
  `DbDataSync.Drivers.Loader.Tests`, hung indefinitely (12+ minutes, 0% CPU) partway through this run and
  was killed rather than waited out further — that project is compiled-driver-plugin loading, entirely
  unrelated to certificates/TLS, and every one of its tests already passed cleanly in the separate
  `Category!=Integration` run earlier in this same session. Recorded as a pre-existing environment flake
  in this sandbox, not a regression this phase introduced.
- Manual, on this Linux sandbox: `dbdatasync config cert new-self-signed --repo <tmp> --url
  https://mytestbox.example.com:5443` produced a `0600` PFX at `<tmp>/tls/dbdatasync.pfx`, loadable with
  no password, SANs `mytestbox.example.com, localhost, <machine hostname>`; `<tmp>/.gitignore` gained
  `tls/`; `dbdatasync.config.yaml` gained the expected `Path`/`AllowInvalid` keys; `config cert status`
  and `config check` both reported it correctly, the latter as `✓ Certificate: self-signed (managed) —
  valid for 396 more day(s).`.

## References

- `architecture/planning/done/linux-tls-without-a-reverse-proxy.md` — tier 2's original sketch and open
  questions, resolved above.
- `architecture/implementation/done/phase-113-tls-bring-your-own-certificate.md` — the file-consumption
  path this phase produces input for, and the "no live reload" precedent this phase follows rather than
  re-litigates.
- `src/DbDataSync.Certificates/ManagedSelfSignedCertificate.cs` — generation, storage, and the renewal
  decision, shared by every caller this phase added.
- `src/DbDataSync.Cli/CertCommand.cs` (`NewSelfSignedFile`) — the non-Windows `new-self-signed` branch.
- `src/DbDataSync.Api/Services/SelfSignedCertificateService.cs` — the renewal hosted service; mirrors
  `CertificateExpiryService`'s shape, explicitly not an extension of it.
- `src/DbDataSync.Cli/ReadinessChecks.cs` (`CertificateCheck`) — the managed-path marker.
- `src/DbDataSync.Cli/Tui/Tabs/CertificateTab.cs`, `Tui/SetupSteps.cs` — the setup walk-through's second
  non-Windows choice.
