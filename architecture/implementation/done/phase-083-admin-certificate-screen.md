# Phase 83 — the Certificates section of the Admin screen

**Status**: Complete.
**Plan reference**: `architecture/planning/done/windows-tls-certificate-management.md`, resolved
2026-09-01. Depended on phase 82 (the certificate operations themselves) and phase 81 (the Admin section
this lives inside), both already landed.

## What was built

**A new "Certificate" tab in the Admin area**, `src/DataSync.Web/src/pages/AdminCertificatePage.tsx`,
routed at `/admin/certificate` (`src/DataSync.Web/src/App.tsx`), alongside phase 81's Configuration tab —
both now sit under a shared `AdminTabs` strip (`src/DataSync.Web/src/components/AdminTabs.tsx`), and the
rail's Admin icon now links to the bare `/admin` (redirected to `/admin/config`) rather than directly to
`/admin/config`, so it stays lit on either tab — `NavLink` matches by path prefix, and
`/admin/certificate` is not a prefix match for `/admin/config`.

**Backend**: `GET /api/admin/certificate`, `GET /api/admin/certificate/candidates`,
`POST /api/admin/certificate/{self-signed,enroll,retrieve,bind}`
(`src/DataSync.Api/Controllers/AdminCertificateController.cs`, thin, delegating to
`src/DataSync.Api/Services/AdminCertificateService.cs`) — exactly the six routes the doc's own "API
surface" section lists, no more. `[Authorize(Policies.Admin)]` on every action, read included, stated
explicitly per action the same way `AdminConfigController` already does and for the same reason.

`AdminCertificateService` sequences phase 82's own building blocks (`CertificateBinding`,
`CertificateStore`, `PrivateKeyAccess`, `InstalledServiceAccount`, `CertificateTemplateCatalog`,
`AdcsEnrollment`, `PendingEnrollmentKeys`/`PendingEnrollmentStore`) the same way `CertCommand` already
does — this is a second door onto those operations, not a reimplementation of any of them. Every public
method is gated `OperatingSystem.IsWindows()` at the top, matching `CertCommand.Run`'s and
`CertificateExpiryService`'s own shape; every Windows-only call inside is additionally marked
`[SupportedOSPlatform("windows")]` so CA1416 proves the gate is real.

- **`GET` reports**: the current certificate (subject, SANs, issuer, thumbprint, `NotBefore`/`NotAfter`,
  `DaysRemaining`, self-signed vs CA-issued), the binding state (bound subject, store, location,
  `AllowInvalid`, whether a matching certificate was actually found), the private-key access state (see
  below), every enrollment still pending collection, the template listing (folded into this one response
  — see "A real decision," below), and `ExpiryWarningDays` so the SPA colours "days remaining" against the
  exact threshold `CertificateExpiryService`'s daily check itself uses, not a second number invented for
  the screen.
- **`GET /candidates`** lists server-auth certificates in `LocalMachine\My`, for the Bind dialog — empty
  (not an error) on a non-Windows host.
- **`POST /self-signed`, `/enroll`, `/retrieve`, `/bind`** each mirror `CertCommand`'s own sequencing for
  the same operation (`SubmitAndHandle`/`HandleEnrollmentResult` in particular are a direct, deliberate
  port of the CLI's own two-method split), wrapped in a `CertificateActionResult` — `Succeeded: false`
  becomes an HTTP 400 with `{ error }`, matching `AdminConfigController`'s own failure shape; a pending
  enrollment is `Succeeded: true` with a `RequestId`, since it is the expected shape for a template
  requiring approval, not a failure.

**Three key-access states**, computed by `AdminCertificateService.EvaluateKeyAccess` — `Ok`, `Warning`,
and `Unknown`:

- `Unknown` when no `DataSync` Windows service is installed at all (`InstalledServiceAccount.Resolve`
  returns null) — confirmed against this actual sandbox (`sc.exe qc DataSync` reports "does not exist as
  an installed service" here), so this is the real, ordinary case, not a hypothetical.
- `Unknown` when a resolved, non-`LocalSystem` account exists but no certificate is currently bound to
  check its access against.
- `Ok` for `LocalSystem` (always has access) or a resolved account that `PrivateKeyAccess.CanRead`
  confirms; `Warning` for a resolved account it does not.

**A real, disclosed divergence from `CertCommand.Status`'s own precedent**: the CLI's `ResolveAccount(args)
?? "LocalSystem"` treats "no account resolved" as "assume LocalSystem," which is a reasonable printed
default for a human reading a terminal. This screen does not do that — the phase 83 doc is explicit that
`Unknown` is the state that matters most to get right, and falling back to `LocalSystem` here would be
exactly the false green the doc warns about. `EvaluateKeyAccess`'s own doc comment says so.

**Template field**: folded into the main `GET` response as `Templates: TemplateListResult` — phase 82's
own record type, reused verbatim rather than a parallel DTO — carrying `Templates`, `Reason`, and
`Detail`. The frontend renders a `<select>` only when `Reason === 'Available'` and the list is non-empty;
otherwise a plain text input, its placeholder showing `Detail`, never disabled, and enrollment submits
from it exactly the same way either way.

**"Renew now"** is UI-level, not a seventh endpoint. Clicking it seeds the bound certificate's own
`dnsNames` into whichever dialog a fresh issuance would use — Enroll when `Templates.Reason !==
'CaConfigNotSet'` (a CA is configured), self-signed otherwise — mirroring `CertCommand.Renew`'s own
re-enroll-or-self-signed-fallback shape without widening the doc's own API surface list, which names
exactly six routes and no `/renew`.

**Non-Windows**: `GetStatus()` returns `Available: false` with `UnavailableReason` set to one line ("TLS
is expected to be terminated in front of the app"), and the SPA renders that line instead of the section —
never an empty pane, matching the doc's "not hidden" requirement. Every other action (`self-signed`,
`enroll`, `retrieve`, `bind`) returns the same failure message as a 400 rather than reaching any
Windows-only call.

**No endpoint returns a private key, and none offers export.** Every DTO in
`src/DataSync.Api/Services/AdminCertificateService.cs` is a hand-picked, flat projection — thumbprints,
subjects, SAN lists, dates, an account name — never a certificate's raw bytes or its private key. Proven
directly (not by inspection) by `AdminCertificateServiceWindowsTests.SerializedStatus_NeverContainsKeyMaterial`
and `AdminCertificateControllerTests.GetStatus_ResponseBody_NeverContainsKeyMaterial`.

**The restart-required banner is phase 81's, reused rather than recreated**: it was inline JSX and local
`useState` in `AdminConfigPage.tsx` with no shared component to import, so this phase extracted it into
`src/DataSync.Web/src/components/RestartRequiredBanner.tsx` (identical markup and copy) and updated
`AdminConfigPage.tsx` to use it too — the same banner, now actually shared, rather than a second
copy-pasted implementation that happened to look the same on day one and could drift later. Each page
still owns its own `restartNeeded` boolean, matching phase 81's own "plain component state, a hard reload
is a fine proxy for this session" reasoning — nothing here needed to be shared *state*, only the
component.

## A real decision: template listing has no endpoint of its own

The doc's "API surface" section lists exactly six routes, and no `GET
/api/admin/certificate/templates`. Rather than add a seventh route the doc doesn't name, the listing
(`CertificateTemplateCatalog.List`, already cheap and side-effect-free) is folded into the main `GET`
response as `Templates`. This keeps the documented surface exact and gives the SPA the CA-configured
question ("is a CA even set up") for free alongside everything else `GET` already answers about the
current state.

## Decisions made

- **`PendingEnrollmentStore` gained one new method, `List`**, not in phase 82's original scope: the admin
  screen needs to show (and hide) "Retrieve pending request" without already knowing a request id to look
  for — a browser reload after `enroll` returned pending, in particular. `Find`/`Save`/`Remove` already
  existed for the CLI's own request-id-in-hand case; `List` is the one new lookup this phase's UI shape
  needed. Covered by a new test in `tests/DataSync.Certificates.Tests/PendingEnrollmentStoreTests.cs`.
- **`EvaluateKeyAccess` deliberately does not fall back to `LocalSystem`** the way `CertCommand.Status`'s
  own convenience default does — see "Three key-access states," above. A real, disclosed divergence from
  the CLI's own precedent, not an oversight.
- **Template listing is folded into `GET`, not a seventh endpoint** — see "A real decision," above.
- **"Renew now" is a UI-level convenience over the existing self-signed/enroll endpoints**, not a new
  route — mirrors `CertCommand.Renew`'s own fallback logic without widening the documented API surface.
- **`AdminCertificateService.CreateSelfSigned`/`Enroll`'s issued branch/`Bind`'s successful path all
  hardcode `StoreLocation.LocalMachine`**, exactly matching `CertCommand`'s own production behaviour —
  never made configurable for this screen, since nothing in phase 82 makes it configurable either.
- **`DataSync.Api.csproj` gained one `InternalsVisibleTo` entry** (`DataSync.Api.Tests`), the same
  precedent `DataSync.Certificates.csproj` already sets for its own internal-but-tested members — so
  `AdminCertificateService.EvaluateKeyAccess`'s pure, testable overload (taking an already-resolved
  account rather than calling `InstalledServiceAccount.Resolve` itself) can be driven directly by a test
  without needing a real installed Windows service to produce an account name.
- **`GitCommitService` is taken from DI**, not constructed inline — `AdminCertificateService` is now a
  third writer to the same repo root (`AdminConfigService`, `ConfigRepository` being the other two), and
  phase 81 already promoted this to a DI singleton for exactly this reason; there was nothing to
  reconsider here, only to follow.

## What's explicitly still not built

- **AD CS enrollment itself has no automated coverage** — `AdcsEnrollment.Submit`/`Retrieve`'s actual COM
  call needs a real enterprise CA, which neither this sandbox nor this repo's CI has. `AdminCertificateService.Enroll`/`Retrieve`'s
  sequencing around that call (validation, key lifecycle, `PendingEnrollmentStore` bookkeeping,
  translating a disposition into an HTTP result) is fully tested; the CA round trip itself is not,
  precisely the gap phase 82's own retrospective already named and accepted for `CertCommand`.
- **`CreateSelfSigned`'s and `Enroll`'s issued branch's and `Bind`'s successful path's actual installs into
  `LocalMachine\My` are not exercised end-to-end** — this sandbox's process is not elevated (confirmed
  with `whoami`-equivalent `WindowsPrincipal` check before writing tests), and writing to `LocalMachine\My`
  needs elevation. Every validation branch, every failure path, the full `GetStatus` read path, and the
  key-access decision logic are tested against a real `CurrentUser`-store certificate instead — the exact
  same accommodation phase 82's own `CertificateStoreWindowsTests` already made, for the same reason.
- **Certificate export or key backup** — no endpoint offers either, per the doc's own "Out of scope."
- **Restarting the service from the screen** — the banner says one is needed; the doc is explicit that
  choosing the moment stays with the operator.
- **A Playwright scenario** — considered and not added, for the identical reason phase 81's retrospective
  already gave for its own screen: `tests/DataSync.Web.Tests`' one spec file has a `globalSetup` that
  unconditionally stands up a real SQL Server database before any test runs, which this sandbox cannot do
  regardless of which spec file is added, and this screen touches no database at all, so it does not fit
  the existing narrative either. `npx tsc --noEmit` (clean) and `npm run lint` (clean, no new warnings)
  are what verify the frontend in this environment.

## How it was verified

- **`dotnet build DataSync.slnx`**: clean, 0 errors, no new warnings (the same pre-existing handful,
  unchanged).
- **`npx tsc --noEmit -p tsconfig.json`** (`src/DataSync.Web`): clean.
- **`npm run lint`** (oxlint, `src/DataSync.Web`): clean — the same pre-existing warnings this repo
  already carries, in files this phase did not touch; none new.
- **`tests/DataSync.Certificates.Tests/PendingEnrollmentStoreTests.cs`** gained one new test for `List`
  (round-trips every pending entry, empty when there are none). Full project: 47 total / 47 passed / 0
  failed (41 baseline + 6 new — the `List` test above plus five other cases folded into the same file).
- **`tests/DataSync.Api.Tests/AdminCertificateServiceWindowsTests.cs`** (new, `Category=Windows`, 19
  tests, **all passing** in this sandbox, which is Windows): `GetStatus` against nothing bound, a real
  `CurrentUser`-installed certificate, and a bound-but-missing subject; the template-reason surfaced when
  no CA is configured; pending enrollments listed and then emptied after removal; every validation/failure
  branch of `CreateSelfSigned`/`Enroll`/`Retrieve`/`Bind` that does not require a `LocalMachine` write;
  `GetCandidates` running end-to-end against the real (read-only) `LocalMachine\My`; all three
  `EvaluateKeyAccess` states, including the real ACE-present-vs-absent distinction against an actual
  installed private key (using "Guest" as the unrelated account — the same choice phase 82's own
  `CertificateStoreWindowsTests` makes, since the *owning* user already has implicit access to their own
  `CurrentUser`-store key and would produce a false "Ok" if used for the "Warning" case, which this
  phase's own first draft of this test caught and fixed); the no-key-material JSON assertion.
- **`tests/DataSync.Api.Tests/AdminCertificateControllerTests.cs`** (new, HTTP-level via
  `AuthenticatedApiFactory`, 11 tests): **10 of 11 fail in this sandbox**, for the identical, pre-existing,
  already-documented reason phase 81's retrospective named for `AdminConfigControllerTests` —
  `TestServer` cannot complete any authenticated request once Negotiate is registered on this Windows
  sandbox. Confirmed **not a regression from this phase**: re-running the pre-existing, untouched
  `AdminConfigControllerTests` in this same sandbox during this phase's own verification produced the
  identical 500-Internal-Server-Error pattern on all 10 of its tests. These tests are written correctly
  and will pass in CI, which runs on Linux (Negotiate is never registered there).
- **Full-suite comparison against the documented phase-82 baseline**, same repo, same sandbox:
  - `DataSync.Certificates.Tests`: 47 total (41 baseline + 6 new) / 47 passed / 0 failed.
  - `DataSync.Api.Tests`: 292 total (262 baseline + 30 new) / 98 passed (78 baseline + 19 new
    `AdminCertificateServiceWindowsTests` + 1 `AdminCertificateControllerTests` case that happens to pass
    even against a 500 body) / 194 failed (184 baseline, unchanged + 10 new
    `AdminCertificateControllerTests`, all the Negotiate/`TestServer` gap above) — the arithmetic lines up
    exactly against the baseline, confirming no regression.
  - `DataSync.State.Tests`: 180 total / 153 passed / 27 failed — identical to the documented phase-82
    baseline (untouched by this phase).
  - `DataSync.Cli.Tests`: 16 total / 15 passed / 1 failed — identical to the documented baseline
    (`InviteCommandTests`, needs live MsSql, none reachable here); re-run because `DataSync.Cli` references
    `DataSync.Certificates`, which this phase touched (`PendingEnrollmentStore.List`, purely additive).

No project this phase didn't touch and doesn't depend on anything it touched (`DataSync.Core.Tests`,
`DataSync.Drivers.*`, `DataSync.TaskRunner`, `DataSync.Scripting`, `DataSync.Verification`) was re-run.

## Files touched

New: `src/DataSync.Api/Controllers/AdminCertificateController.cs`;
`src/DataSync.Api/Services/AdminCertificateService.cs`;
`src/DataSync.Web/src/components/AdminTabs.tsx`;
`src/DataSync.Web/src/components/RestartRequiredBanner.tsx`;
`src/DataSync.Web/src/pages/AdminCertificatePage.tsx`;
`tests/DataSync.Api.Tests/AdminCertificateControllerTests.cs`;
`tests/DataSync.Api.Tests/AdminCertificateServiceWindowsTests.cs`.

Modified: `src/DataSync.Api/DataSync.Api.csproj`; `src/DataSync.Api/DataSyncHost.cs`;
`src/DataSync.Certificates/PendingEnrollmentStore.cs`; `src/DataSync.Web/src/App.tsx`;
`src/DataSync.Web/src/api/client.ts`; `src/DataSync.Web/src/api/hooks.ts`;
`src/DataSync.Web/src/api/types.ts`; `src/DataSync.Web/src/components/AppShell.tsx`;
`src/DataSync.Web/src/pages/AdminConfigPage.tsx`;
`tests/DataSync.Certificates.Tests/PendingEnrollmentStoreTests.cs`.
