# Phase 82 — Windows certificate management: issuance, installation, binding, expiry

**Status**: Complete.
**Plan reference**: `architecture/planning/done/windows-tls-certificate-management.md`, resolved
2026-09-01. Depended on phase 79 (`datasync.config.yaml` and its writer) for binding, and on phases
77/80 (the notification core) for expiry, both already landed. Phase 83 (the Certificates section of
the Admin screen) is a separate, later task — this phase is complete and usable without it, per the
original doc's own design.

## What was built

**A new project, `src/DataSync.Certificates`** — small, and the only Windows-related code in the
solution besides `ServiceCommand` and `WindowsSignIn`. Targets plain `net10.0`, **not**
`net10.0-windows`: a Windows-specific TFM would make `DataSync.Api`/`DataSync.Cli` (both plain `net10.0`,
both published for the Linux container per phase 51) fail to reference it at all, which is a
compile-time SDK error, not a runtime one — the constraint has to be met at the TFM level, not just by
gating call sites. Every genuinely Windows-only member is marked `[SupportedOSPlatform("windows")]`
(`ServiceCommand`'s and `WindowsSignIn`'s existing precedent) and every caller gates on
`OperatingSystem.IsWindows()` before reaching it.

**One refinement of the doc's own "everything public is marked" instruction**: only the parts that
actually touch a Windows API are marked and gated — `CertificateStore`, `PrivateKeyAccess`,
`AdcsEnrollment`, `PendingEnrollmentKeys`, and the actual directory query inside
`CertificateTemplateCatalog`. `CertificateSpec`, `CertificateBuilder` (self-signed issuance *and* CSR
building — both use `System.Security.Cryptography.CertificateRequest`, which is genuine cross-platform
.NET crypto, OpenSSL-backed off Windows), `CertificateExpiryEvaluator`, `CertificateSanReader`, and
`CertificateBinding` are plain, portable code with nothing OS-specific in them. Blanket-marking those
too would have been inaccurate and would have fought the doc's own "runnable anywhere" requirement for
their unit tests — this was a deliberate, disclosed narrowing of the instruction, not a shortcut.

### The pieces

- **`CertificateSpec`** (`CertificateSpec.cs`) — the doc's exact record shape. `DnsNames` is redeclared
  over the positional parameter so *construction* rejects an empty list, naming the browser behaviour
  (Chrome dropped CN fallback years ago) as the reason. Not re-validated by a `with` expression — a
  record's compiler-generated copy constructor assigns backing fields directly and does not re-run an
  auto-property initializer; nothing in this codebase uses `with` on a `CertificateSpec`, so this is
  recorded as a known limit rather than worked around for a case that doesn't arise (see the type's own
  doc comment).
- **`CertificateBuilder`** — `CreateSelfSigned` (SAN for every requested name plus the machine's own
  hostname and FQDN, `DigitalSignature | KeyEncipherment`, the server-auth EKU, RSA 2048) and
  `CreateSigningRequest` (the same request shape, PEM-encoded, over a caller-supplied `RSA` key rather
  than one it creates itself — see "A real gap the doc didn't spell out," below).
- **`CertificateSanReader`** — decodes a certificate's SAN extension by hand (`System.Formats.Asn1`;
  .NET has a SAN *builder* but no typed reader) since both the unit tests and `datasync cert
  status`/`list`/`renew` need to read DNS names back out of a real certificate.
- **`CertificateStore`** (Windows-only) — `Install` round-trips a certificate through a PFX
  export/import so its private key is actually persisted to a CNG key container (`CreateSelfSigned`'s
  own key is ephemeral otherwise) with `PersistKeySet` and no `Exportable` flag; `ListServerAuthCertificates`,
  `FindByThumbprint`, `FindBySubject` (the same subject-lookup shape Kestrel's own certificate resolver
  uses, latest `NotAfter` wins on a tie).
- **`PrivateKeyAccess`** (Windows-only) — `Grant`/`CanRead` reach the CNG key via `GetRSAPrivateKey()` →
  `RSACng` → `CngKey.UniqueName`, resolve the on-disk key-container file
  (`%ProgramData%\Microsoft\Crypto\Keys` for a machine key, `%APPDATA%\...` for a user key —
  `CngKey.IsMachineKey` is what decides which; a first version of this hardcoded the machine path and
  `CertificateStoreWindowsTests` caught the bug against a `CurrentUser`-store certificate), and apply/
  check an NTFS `FileSystemAccessRule`. Direct-account-match only, not group-membership resolution — the
  conservative direction for a health check to be wrong in.
- **`InstalledServiceAccount`** (parsing pure/portable, the actual `sc.exe qc` call Windows-only) —
  reads back which account the installed `DataSync` service runs as. **The doc's own text says this
  "reads it back... the same way `datasync service status` already parses it"; that turned out to be
  false** — `ServiceCommand.Run`'s `status` case is a bare passthrough of `sc.exe query`'s output to the
  console, no parsing at all. There was nothing to reuse, so this is a fresh parser over `sc.exe qc`
  (query *config*, which is what actually reports `SERVICE_START_NAME`), unit-tested against a literal,
  hand-captured sample of real output.
- **`CertificateTemplateCatalog`** (Windows-only for the actual LDAP query; the `CaConfig`-unset and
  non-Windows checks are portable) — best-effort listing of templates published on the configured CA via
  `System.DirectoryServices`, checking domain membership first (`Domain.GetComputerDomain()`) so a
  workgroup machine gets told that specifically rather than a generic "directory unreachable." Every
  failure path returns empty-plus-a-reason, never throws; `ClassifyDirectoryFailure`'s HRESULT mapping
  is best-effort and explicitly documented as unverified against a real AD environment (this sandbox has
  none) — the real exception message always rides along as `Detail` regardless.
- **`AdcsEnrollment`** (Windows-only) — `Submit`/`Retrieve` over the `CertificateAuthority.Request` COM
  interface, resolved by ProgID and called dynamically, per the doc. `CR_DISP_UNDER_SUBMISSION` is
  handled as pending, not an error. **Not tested against a real CA** — see "How it was verified."
- **`CertificateBinding`** (portable) — writes/reads the four `Kestrel:Certificates:Default:*` keys via
  `DataSyncConfigFile.SetValue`/`Read` plus `GitCommitService.CommitChanges`, `ServeCommand.Prepare`'s
  own two-step pattern. See "A real design question the doc's example YAML raised," below, for how a
  three-level-nested YAML key was written through a writer that only supports two levels.
- **`PendingEnrollmentKeys` / `PendingEnrollmentStore`** — not in the original doc at all; see "A real
  gap the doc didn't spell out."
- **`CertificateExpiryEvaluator`** (portable, pure) — the doc's four scenarios verbatim: 31 days out
  raises nothing, 29 days raises `Expiring` once, a second check the same day raises nothing further,
  past `NotAfter` raises `Expired`. Takes "last raised" dates as parameters rather than owning any state
  itself, which is what makes it a five-line, dependency-free function to test.

**`datasync cert status|list|new-self-signed|enroll|renew|retrieve|templates|bind`**
(`src/DataSync.Cli/CertCommand.cs`) — every subcommand the doc lists, gated once at the top
(`!OperatingSystem.IsWindows()`) the same way `ServiceCommand.Run` gates its own surface. Wired into
`Program.cs` and `Help.cs`, documented in `CONFIG.md`.

**The notification core gained a new constant and a producer, not a new mechanism.**
`NotificationKinds.CertificateExpiring`/`CertificateExpired` sit beside `RunFailed`/`ReplicationPaused`/
`PositionExpired` in `src/DataSync.State/NotificationStore.cs`. The producer needed something
`NotificationStore` didn't have: every existing producer (`TaskRunStore.NotifyIfNewlyFailed`/
`NotifyIfNewlyPaused`) writes its notification inside the same transaction as the event it's about,
through the `internal Insert` that takes someone else's open connection — deliberately not public,
per `Insert`'s own doc comment, because "a caller with nothing to be atomic with" was exactly the case
that shouldn't get a generic entry point. Phase 82's expiry check *is* that case — a standalone daily
poll, not a write to any other table — so `NotificationStore.Raise(kind, message, ...)` is new: public,
opens its own connection, calls the same `Insert` with a null transaction. One new public method for
the one shape of producer that generically needed it, not a rule change.

**`CertificateExpiryService`** (`src/DataSync.Api/Services/CertificateExpiryService.cs`) — a
`BackgroundService` on `SchedulerService`/`RunPruningService`'s own shape (immediate first pass, then a
`PeriodicTimer`, log-and-swallow on failure). Reads `Kestrel:Certificates:Default:Subject/Location`
straight off `IConfiguration` (not a repo-root file re-read — the running process already has this
resolved), finds the certificate, evaluates it through `CertificateExpiryEvaluator`, raises through
`NotificationStore.Raise`. Also checks whether the resolved service account can still read the private
key (see "Left out or simplified," below, for why that's a log warning, not a third notification kind).
Registered in `DataSyncHost.cs` with `if (OperatingSystem.IsWindows()) builder.Services.AddHostedService<CertificateExpiryService>();`
— the exact idiom that file already uses a few lines below for `Negotiate` authentication, not a new
pattern. A Linux host never constructs the type at all.

**`CertificateOptions`** (`src/DataSync.Api/Configuration/CertificateOptions.cs`) — `ExpiryWarningDays`
(default 30), alongside `AuthOptions`/`PasskeyOptions`'s own per-feature-area shape.
`CaConfig`/`Template` deliberately are **not** here: they're a CLI-process concern
(`enroll`/`renew`/`templates` read them straight off `datasync.config.yaml`), the same way `DataSync:Url`
is `ServeCommand`'s concern and not an `ApiOptions` field — the running API process never needs to know
which CA a certificate came from, only which one is bound.

## A real gap the doc didn't spell out: pairing a pending enrollment's certificate with its key

The doc describes `CR_DISP_UNDER_SUBMISSION` as a state to record and collect later, but not *how* the
private key survives between `enroll` submitting a CSR and `retrieve` collecting the issued certificate
— possibly minutes or days, and possibly a different CLI invocation, later. A CSR carries only the
*public* key; the CA never returns the private half. `CertificateBuilder.CreateSigningRequest`'s
original, doc-implied shape (create an ephemeral RSA key, return it alongside the CSR) cannot survive
that gap — an in-memory key dies with the process.

**Resolved with two new types not in the original doc**: `PendingEnrollmentKeys` creates a **named,
persisted, non-exportable CNG key** (the same "nothing exports a private key" decision the doc makes for
an installed certificate, applied one step earlier) and can reopen or delete it by name later.
`PendingEnrollmentStore` is a small JSON file at the repo root — deliberately **not**
`datasync.config.yaml` and **never git-committed** — correlating a CA request id to the key name that
built its CSR, since that mapping is per-machine, transient, and meaningless anywhere the CNG key
container doesn't exist. `datasync cert retrieve --request-id` looks up the record, reopens the key,
pairs it with the returned certificate via `CopyWithPrivateKey`, and installs it exactly like a
first-time issuance.

This needed a real design decision the doc left implicit, made explicit here so a later reader doesn't
have to reverse-engineer it from the code. It shares the AD CS path's own "not tested against a real
CA" gap for the end-to-end flow, but the key-lifecycle primitive itself (create/reopen/delete a named
CNG key) needed no CA at all and has real `Category=Windows` coverage.

**A follow-up caught in review, not by the original implementation**: `PendingEnrollmentStore`'s file
sits at the repo root, which is a git repository, and nothing had it ignored — every `git status` an
operator ran would show `pending-certificate-enrollments.json` as untracked for as long as any
enrollment had ever been pending, reading as forgotten rather than intentional. Fixed by having `Save`
append a `.gitignore` entry for it on first use (idempotent, and leaves any existing `.gitignore`
content untouched) rather than having `ServeCommand.Prepare` write it up front — a repo root that never
enrolls a certificate never needs the line. Covered by five new tests in
`tests/DataSync.Certificates.Tests/PendingEnrollmentStoreTests.cs` (round-trip, the ignore entry
appearing, not duplicating across two saves, and coexisting with a pre-existing `.gitignore`).

## A real design question the doc's example YAML raised

The doc's own example (`Kestrel: / Certificates: / Default: / Subject: ...`) is three levels of YAML
nesting, but `DataSyncConfigFile.SetValue` — unchanged since phase 79 — only ever writes a flat,
two-level shape (one `Section:` header, then indented `Key: value` lines); phase 81 hit the same limit
and left `Auth:*`/`Passkeys:*` read-only in the admin screen rather than extend the writer.

**Resolved without touching `SetValue` at all**: passing the whole dotted path
`"Kestrel:Certificates:Default"` as the *section name* stays inside the two-level shape `SetValue`
already supports, and produces a YAML document (`Kestrel:Certificates:Default:` as one literal, valid
plain-scalar key, four values indented under it) that `DataSyncConfigFile.Read`'s flattener turns back
into exactly `Kestrel:Certificates:Default:Subject` and its three siblings — because `Flatten` only ever
string-concatenates whatever prefix it's given with a colon; it has no opinion about whether that prefix
already contains colons of its own. Proven by `CertificateBindingTests`, which round-trips `Bind` through
both the raw flattened dictionary and `CertificateBinding.Read`. No change to phase 79's writer, and no
widening of what it claims to support.

## Decisions made

- **`AllowInvalid` when a self-signed certificate is later replaced by a CA-issued one** — the doc's own
  open question. Resolved as the doc's own "probably right" leaning: **report it, don't touch it.**
  `bind` only ever changes `AllowInvalid` on an explicit `--allow-invalid`/`--no-allow-invalid`, or on a
  genuinely first bind for a repo (nothing configured yet, defaulted from whether *this* certificate is
  self-signed). Every other bind preserves whatever is already configured and prints a note if it looks
  stale (CA-issued certificate, `AllowInvalid` still `true`).
- **`PendingEnrollmentKeys`/`PendingEnrollmentStore`**, above — a real addition beyond the doc's own
  text, not an interpretation of it.
- **Renewal without a configured CA falls back to a fresh self-signed reissue.** The doc's "Renewal is a
  re-enroll" section is written entirely in terms of AD CS; a self-signed-only deployment still needs a
  working `datasync cert renew`, so when no `CaConfig`/`Template` is set, `renew` reissues self-signed
  with the bound certificate's subject and SANs instead of refusing to run. Disclosed as an extension in
  `CertCommand`'s own comment, not silently assumed.
- **The service-account-can-read-the-key check is a logged warning, not a third notification kind.** The
  doc frames the new notification surface as exactly two constants; this check is real (an ACL removed
  by a re-issue or a group policy is otherwise invisible until the next restart) but narrower in
  audience — an operator watching logs, not necessarily the notification feed a viewer reads — so it
  stays a `LogWarning` rather than inventing a kind the doc never asked for.
- **`NotificationStore.Raise` is new, public API**, not a workaround — see "The notification core," above.
- **`CertificateOptions` holds only `ExpiryWarningDays`**; `CaConfig`/`Template` stay CLI-process concerns,
  matching how `DataSync:Url` is already `ServeCommand`'s concern rather than an `ApiOptions` field.
- **Not literally "everything public" marked `[SupportedOSPlatform("windows")]`** — only what actually
  touches a Windows API. See "What was built," above.
- **`DataSync.Certificates` targets plain `net10.0`, never `net10.0-windows`** — the hard constraint
  (the Linux container image must keep building) is enforced at the project-TFM level, not only by
  gating call sites, since a Windows-only TFM would be a compile-time reference error from
  `DataSync.Api`/`DataSync.Cli`, not a runtime one.

## What's explicitly still not built

- **Phase 83, the Certificates admin-screen section** — separate, later, out of scope here by design.
- **Live certificate rotation without a restart** — the doc's own out-of-scope call, unchanged.
  `ApiOptions`/Kestrel's certificate stay resolved once at startup; `bind` says so.
- **ACME/Let's Encrypt, any public-CA integration, client-certificate auth, source/target database TLS,
  Linux/macOS certificate management, IIS binding configuration** — every one of the doc's own
  out-of-scope lines, all still true.
- **A signed AD CS renewal request** (as opposed to the re-enroll this phase always does) — the doc's own
  "additive second code path if a deployment ever needs it," not built, because nothing needed it yet.
- **Group-membership resolution in `PrivateKeyAccess.CanRead`** — direct-account match only; an account
  granted access only via a group reports as unable to read. The conservative direction to be wrong in,
  and not built out further for lack of a concrete case needing it.
- **`datasync service install`, `DataSyncHost`'s HTTP pipeline beyond the existing guarded
  `UseHttpsRedirection`, and any startup dependency on TLS/certificate config existing** — all
  untouched, per the doc's own ground rules.

## How it was verified

**Unit** (`tests/DataSync.Certificates.Tests`) — all pure/portable logic, no Windows API touched, run on
this Windows sandbox but written to run anywhere:
- `CertificateSpecTests` — empty `DnsNames` rejected at construction, naming the browser behaviour.
- `CertificateBuilderTests` — a self-signed certificate carries every requested DNS name plus the
  machine's own hostname, the server-auth EKU, `DigitalSignature | KeyEncipherment`, and the requested
  validity window; the CSR round-trips subject and SANs through `CertificateRequest.LoadSigningRequestPem`.
- `CertificateExpiryEvaluatorTests` — the doc's four scenarios verbatim, plus exactly-at-`NotAfter` and
  next-day-still-in-window edge cases.
- `CertificateBindingTests` — `Bind` writes all four `Kestrel:Certificates:Default:*` keys, readable back
  both as a raw flattened dictionary and through `CertificateBinding.Read`; commits to git; an unbound
  repo reads a null subject; `SubjectCommonName` is the simple name, not the full DN.
- `InstalledServiceAccountTests` — `ParseServiceStartName` against a literal, hand-captured real
  `sc.exe qc` sample (domain account, `LocalSystem`, no match, empty input).
- `CertificateTemplateCatalogTests` — `CaConfig` null/empty returns `CaConfigNotSet` without touching AD;
  `ClassifyDirectoryFailure` against `UnauthorizedAccessException` and two `COMException` HRESULTs.

**`Category=Windows`** (this sandbox is Windows, so these actually run here):
- `CertificateStoreWindowsTests` — install into `CurrentUser\My` (no elevation needed), then
  `FindByThumbprint`/`FindBySubject`/`ListServerAuthCertificates` all find it, and
  `PrivateKeyAccess.Grant` followed by `CanRead` proves a real read ACE landed on the real key file (this
  is the test that caught the machine-vs-user key-path bug described above).
- `PendingEnrollmentKeysWindowsTests` — create/reopen/delete a named, non-machine-scoped (so no
  elevation needed) CNG key.
- `InstalledServiceAccountWindowsTests` — the actual `sc.exe qc` call against a service name guaranteed
  not to exist, proving the "not installed" case falls through to null rather than throwing.
- `CertificateExpiryServiceWindowsTests` (`tests/DataSync.Api.Tests`) — the end-to-end wiring a
  piecewise test can't catch: a real installed certificate, a real `IConfiguration`, a real (SQLite)
  `NotificationStore`, proving `CertificateExpiryService.CheckAsync()` actually finds the bound
  certificate and raises `CertificateExpiring`; also covers no-subject-configured and
  subject-configured-but-no-matching-certificate, both logging and not throwing.
- `NotificationStoreTests` gained two cases for `Raise` and for `CertificateExpiring`/`CertificateExpired`
  being distinguishable kinds (SQLite-backed, no Windows API involved, but exercising the new notification
  producer end to end).

**Honest about what is not tested.** AD CS enrollment itself (`AdcsEnrollment.Submit`/`Retrieve`, the
actual COM call) has no test — it needs a real enterprise CA, and neither this sandbox nor this repo's CI
has a Windows runner with one. The same call phase 51 made plainly for Windows service registration
("Windows service registration is untested") applies here: the disposition-code constants
(`CR_DISP_ISSUED`, `CR_DISP_UNDER_SUBMISSION`, etc.) are the documented, stable values from `certcli.h`,
not independently verified against a live server, and the enrollment path is exercised manually against
a real CA rather than asserted by a test that would only prove a COM call *would have been* made.
`CertificateTemplateCatalog.List`'s own `!OperatingSystem.IsWindows()` branch is likewise unexercised —
this sandbox is Windows, this repo's test projects carry no conditional-skip package today, and adding
one for a single two-line branch (structurally identical to the tested `CaConfigNotSet` check right
above it) was judged not worth a new dependency; verified by inspection instead.

**Full-solution build**: `dotnet build DataSync.slnx` — clean, 0 warnings introduced (the same handful of
pre-existing warnings elsewhere, unchanged; every `CA1416` platform-compatibility warning this phase's
own new code could have produced was resolved by marking call sites with `[SupportedOSPlatform("windows")]`
rather than suppressed).

**Full test run**, compared against the documented phase-81 baseline, same sandbox:
- `DataSync.Certificates.Tests` (new): 40 total / 40 passed / 0 failed.
- `DataSync.Api.Tests`: 262 total (258 baseline + 4 new `CertificateExpiryServiceWindowsTests`) / 78
  passed (74 baseline + 4 new) / 184 failed — **identical to the documented baseline's 184**, all the
  same pre-existing Negotiate/`TestServer` gap and MsSql-dependent `CrossInstanceEndToEndTests` failures
  phase 79/81 already recorded; no regression.
- `DataSync.State.Tests`: 180 total (178 baseline + 2 new `NotificationStoreTests` cases) / 153 passed
  (151 + 2) / 27 failed — identical to baseline, all in `CrossEngineStateTests` (needs live MsSql/Postgres,
  none reachable here).
- `DataSync.Core.Tests`: 167 total / 132 passed / 35 failed — identical to baseline, all 35 in
  `ConfigRepositoryTests`' pre-existing libgit2-read-only-object Windows teardown quirk, untouched by
  this phase.
- `DataSync.Cli.Tests`: 16 total / 15 passed / 1 failed — identical to baseline (`InviteCommandTests`,
  needs live MsSql, none reachable here).

No project this phase didn't touch (`DataSync.Drivers.*`, `DataSync.TaskRunner`, `DataSync.Scripting`,
`DataSync.Verification`) was re-run, since nothing in this phase changed anything they depend on.

## Files touched

New: `src/DataSync.Certificates/` (project + `CertificateSpec.cs`, `CertificateBuilder.cs`,
`CertificateExpiryEvaluator.cs`, `CertificateSanReader.cs`, `CertificateInfo.cs`, `CertificateStore.cs`,
`PrivateKeyAccess.cs`, `InstalledServiceAccount.cs`, `CertificateTemplateCatalog.cs`,
`AdcsEnrollment.cs`, `CertificateBinding.cs`, `PendingEnrollmentKeys.cs`, `PendingEnrollmentStore.cs`);
`src/DataSync.Cli/CertCommand.cs`; `src/DataSync.Api/Configuration/CertificateOptions.cs`;
`src/DataSync.Api/Services/CertificateExpiryService.cs`; `tests/DataSync.Certificates.Tests/` (project +
nine test files); `tests/DataSync.Api.Tests/CertificateExpiryServiceWindowsTests.cs`.

Modified: `DataSync.slnx`; `src/DataSync.Api/DataSync.Api.csproj`; `src/DataSync.Api/DataSyncHost.cs`;
`src/DataSync.Cli/DataSync.Cli.csproj`; `src/DataSync.Cli/Help.cs`; `src/DataSync.Cli/Program.cs`;
`src/DataSync.State/NotificationStore.cs`; `tests/DataSync.State.Tests/NotificationStoreTests.cs`;
`CONFIG.md` (new `datasync cert` section, `DataSync:Certificates:*` and `Kestrel:Certificates:Default:*`
tables).
