# Phase 140 — Windows CI: a real compatibility audit, not just a verification pass

**Status**: Planned, not started.
**Plan reference**: none — logged directly from this session's own investigation of phase 136's new
`dotnet-windows` job (`.github/workflows/ci.yml`), the first real Windows CI run this repo has ever had.
**Materially rescoped 2026-09-15**, same day it was opened: what looked like "verify five fixes, chase
one failure" turned out to be a first-contact Windows compatibility audit once the actual run data was
read in full rather than sampled. See "How the scope grew," below.

## Why

Standing up `dotnet-windows` (a real `windows-latest` runner exercising every `[WindowsOnlyFact]`-gated
test, previously always `[SKIP]`) surfaced its first real run at **270 of 447 `Api.Tests` failing**, plus
smaller failure clusters in `Cli.Tests`, `Core.Tests`, and `State.Tests` — this repo's code has simply
never been run on Windows before, end to end, and it shows.

## How the scope grew

This phase was originally opened after investigating a *sample* of that first run's failures — the ones
with distinctly-named exceptions (`AggregateException`, `NullReferenceException`, `NotSupportedException`)
rather than all 270. Five fixes went out on that basis, confirmed only by local reasoning (no Windows box
available). Checking the *next* real run's actual numbers — not assumed, read — showed:

- `Core.Tests`/`State.Tests`/`Cli.Tests` recovered almost completely (the libgit2 read-only-cleanup fix
  really was the dominant cause there).
- `Api.Tests` moved from 270 failed to **253 failed**, essentially unchanged, and unchanged *again*
  between the run right after the `Auth:Disabled`/Negotiate fix and the run after everything else landed
  too. That fix was correct for what it targeted (`ApiFallbackTests`, which uses an auth-*disabled*
  factory) but left the much larger population of `Api.Tests` classes built on an auth-*enabled* factory
  (`AuthenticatedApiFactory` — `UserManagementTests`, `NotificationsEndpointTests`,
  `AdminConfigControllerTests`, and others) completely untouched, still failing with the same `500`
  shape.
- `Cli.Tests` (once the libgit2 fix landed) still showed 17 failures never sampled before, falling into
  two clusters unrelated to anything already fixed.

None of this was chased further in the moment — flagged here instead, with the real numbers, so the next
pass starts from what actually failed rather than a fresh sample.

## Confirmed fixed for real (not just reasoned about)

Reconciling the exact pass/fail/skip counts across runs before and after this session's fixes:

- **The libgit2 read-only cleanup fix** (28 files, 9 projects) is confirmed working — `Core.Tests` 252/1,
  `State.Tests` 197/1 (that one remaining `State.Tests` failure is unexplored; small enough to fold into
  this phase's own sweep rather than needing its own line item), and the bulk of `Cli.Tests`/`Api.Tests`
  recovered.
- **Phase 135's own real-Windows checkpoint** (`ServiceCommandTests.GrantDataDirectoryAccess_LocalSystem_TakesOwnershipInsteadOfReturningEarly`,
  the real `icacls` ownership-transfer test, `[SKIP]` everywhere until now) and **phase 136's own three**
  (`WindowsServiceEventLogTests` — real Event Log write/read-back ×2, source re-registration ×1) all
  **ran for real on Windows and passed** — confirmed by exact arithmetic, not direct observation (xUnit's
  console runner only names failures and skips, not passes): `Cli.Tests` went from 134 total (130
  passed/4 skipped/0 failed, always-skipped on Linux) to 134 total (115 passed/2 skipped/17 failed) on
  Windows. The 2 skips are the `[LinuxOnlyFact]`-gated `SystemdServiceTests` pair (correctly skipping on
  Windows, as intended). `115 = 130 − 2 (now-skipped Linux tests) − 17 (newly-failing Windows-only-
  assumption tests, see below) + 4` — the `+4` only balances if all four previously-`[SKIP]`ped
  Windows-only tests now pass. Worth a follow-up pass to actually *read* their real Event Log/icacls
  output in the CI log rather than trust arithmetic alone, but this is strong evidence phases 135 and 136
  are for real what their own retrospectives could only claim "logically."
- The `dotnet` and `playwright` jobs are both green as of the latest run (`playwright` fixed by another
  session; `dotnet` by this phase's own obj/bin fix).

## Confirmed NOT fixed, or newly found — the real remaining work

### 1. Negotiate + `TestServer` breaks *every* authenticated in-process test on Windows, not just one route

The `ApiFallbackTests` fix (`!AuthOptions.Disabled` gating `AddNegotiate()`) only helps hosts built with
auth disabled. `AuthenticatedApiFactory`-based tests — a large fraction of `Api.Tests`, by design, since
that factory exists specifically to exercise the real authenticated/authorized paths — still register
Negotiate on Windows and still fail with the same `500`
(`NotSupportedException: ... IConnectionItemsFeature ...`) shape, seemingly regardless of which endpoint
or which scheme actually handles the request. **253 of 448 `Api.Tests` still fail**, unchanged across two
runs bracketing every fix this session made. This is almost certainly the same root mechanism as the one
already understood (Negotiate's real Windows SSPI implementation needing a Kestrel-only connection
feature `TestServer` never provides) but triggering far more broadly than "the one deep-link fallback
route" — worth confirming precisely (does *any* authenticated request 500 once Negotiate is registered,
or only some?) before deciding the fix. Candidate directions, not yet evaluated against each other:
- A test-only Negotiate stand-in/fake that satisfies the scheme-registration requirement without
  touching real SSPI.
- Confirming whether `IConnectionItemsFeature` can be supplied to `TestServer` at all (a custom
  `IStartupFilter`/middleware adding a minimal implementation before authentication runs).
- Revisiting whether `AuthenticatedApiFactory`-based tests need Negotiate registered at all if they never
  actually authenticate *through* Negotiate specifically (do any of them?).

### 2. Two distinct, real Windows-only gaps in `Cli.Tests` (17 failures, unsampled until now)

**a. Linux-only functionality tested without platform gating** (`ToolCommandTests`, 5 failures:
`Install_AsRoot_LinksUsrLocalBin_AndChmodsTheToolDir`, `Install_Twice_IsANoOp_TheSecondTime`,
`Uninstall_NotElevated_PrintsTheSudoCommand_AndRemovesNothing`,
`Install_NotElevated_PrintsTheSudoCommand_AndWritesNothing`,
`Install_ADirUnderTheUserProfile_WarnsButStillProceeds`; likely also `InstallDocsTests.InstallDocs_MentionThisPlatformsDefaultToolDir`,
"Expected: 1, Actual: 0" — reads like a platform-specific doc string assertion). These test
`dbdatasync tool install`'s POSIX-specific behavior (`/usr/local/bin` symlinks, `chmod`, sudo messaging)
with no `[LinuxOnlyFact]`-equivalent gate — the same class of gap `LinuxOnlyFactAttribute` was built for
earlier this session, just not applied here because these tests were never sampled from the first run.
**Two more `SystemdServiceTests` cases beyond the two already gated**
(`Install_ExecutableUnderHomeWithANonHardenedRoot_WarnsButStillRegisters`,
`Install_ExecutableUnderHomeWithTheHardenedDefaultRoot_RefusesRatherThanRegisteringABrokenUnit`) also
failed — these use the *fake* systemd environment (not real `id`/`systemctl`), so they were correctly
left ungated, but they manufacture a POSIX-style `/home/someone/dbdatasync` path via
`Environment.GetEnvironmentVariable("HOME")` and `Path.Combine`, which produces a mixed-separator string
on Windows that `CliOptions.IsUnderUserProfile` (Windows branch: checks `SpecialFolder.UserProfile` only,
explicitly skips the `/home/`/`/root` fallback) will never match — the tests' own *logic under test* is
POSIX-specific even though the harness is fake. Whether the right fix is `[LinuxOnlyFact]` on these two
as well, or making the fake path construction platform-aware so the test still proves something on
Windows, is a real design call, not a mechanical gate.

**b. A genuinely new, unexplored Windows certificate/crypto issue** (`NewSelfSignedFileTests` ×5,
`ReadinessChecksTests.ManagedSelfSignedCertificate_Current_CertificateCheckReportsTheManagedMarker`/`_Expired_...`,
`CertUsePemTests.Status_NothingConfigured_ReportsNoCertificateAndPointsAtUsePemUsePfx`,
`ConfigCommandTests.Cert_ForwardsToCertCommand` — 8 failures total). Not yet root-caused: the one failure
whose assertion text was legible in the interleaved console log (`CertUsePemTests`) showed a plain
`Assert.Contains` substring mismatch, not a crash — but the console output for concurrently-running xUnit
tests interleaves badly enough (multiple tests' stack traces genuinely intermixed on adjacent lines) that
attributing the other seven with confidence needs a cleaner log (e.g. `--logger trx` or serial execution)
rather than more guessing from this one. This is file-based self-signed PEM/PFX generation and status
reporting — distinct from phases 82/83's already-Windows-aware certificate *store* work — and may be a
real bug in `ManagedSelfSignedCertificate`/`CertCommand`'s file-based path on Windows, not just a test
gating gap. Investigate before assuming either "test bug" or "real bug."

### 3. One `State.Tests` failure — named, not yet understood

`State.Tests` went from 189/0 (Linux) to 197/1 (Windows, 8 new tests from phase 134 included):
`RemoteRunnerStateTests.Once_the_owner_is_declared_lost_nothing_waits_on_the_network_again`. Likely a
timing-sensitive test (the name itself suggests a network-wait/timeout assertion) that behaves
differently under Windows' scheduler or clock resolution — a real possibility, not yet confirmed. Small
in isolation; worth naming and understanding in the same pass as the above rather than leaving it as an
unlabeled asterisk.

### 4. `ConnectionsControllerTests.Upsert_NeverReturnsOrCommitsPlaintextPassword`

Unchanged from this phase's original scope — see the original investigation: a second, freshly-opened
`LibGit2Sharp.Repository` doesn't see a just-committed tree entry on Windows (`NullReferenceException`
indexing `repo.Head.Tip[path]`), root cause not pinned down, no speculative fix attempted since this test
guards a real security property (a connection's password never lands in git in plaintext).

## What this phase will do

1. **Get a clean, non-interleaved log of a `dotnet-windows` run** (`--logger trx`, or accept the
   interleaving and cross-reference by timestamp) — most of the open items above need one to root-cause
   precisely rather than guess from adjacent console lines.
2. **Characterize item 1 (Negotiate) properly**: does every authenticated request 500 once Negotiate is
   registered, on any auth-enabled host, or is there a narrower trigger? This is the highest-impact open
   item by far (253 tests) and needs an actual answer before a fix is chosen, not another guess.
3. **Gate or fix the Linux-only `Cli.Tests` gaps** (item 2a) — mechanical for `ToolCommandTests`
   (`[LinuxOnlyFact]`, matching this session's own precedent); a real design decision for the two
   `SystemdServiceTests` "under home" cases (gate, or make the fake path platform-aware).
4. **Root-cause the certificate/crypto failures** (item 2b) with a clean log before assuming test bug vs.
   real bug.
5. **Name and understand the one `State.Tests` failure** (item 3).
6. **Investigate item 4** (`ConnectionsControllerTests`) as originally scoped — a deliberately-
   instrumented run, now affordable without hundreds of other failures burying the signal once 1–5 above
   are closer to done.
7. **Re-run the whole `dotnet-windows` job after each fix** and update the counts here — this doc's own
   numbers are a snapshot from one run; don't assume a fix worked without checking the next real run,
   the same discipline that caught items 1 and 2 being under-scoped the first time.

## Out of scope

- Phase 134's own follow-up items (Bulk Load reader-override regression, `EnqueueForInitialLoadAsync`
  failure-mode hardening) — unrelated to Windows CI, tracked in phase 134's own retrospective and
  `architecture/implementation/README.md`'s 2026-09-15 note.
- `dotnet-integration`'s own remaining ~46-test failure wave — a *Linux* (`ubuntu-latest`) job, unrelated
  to anything Windows-specific; tracked separately as phase 141.
- Any Windows-only behavior not surfaced by a real CI run yet. This phase closes out what real runs have
  actually shown; it is not a blind, exhaustive Windows compatibility pass.

## Open questions to resolve during implementation

- Whether item 1's eventual fix is narrow (a targeted `IConnectionItemsFeature` shim for `TestServer`) or
  requires rethinking how this codebase's test suite exercises Negotiate-adjacent auth at all on Windows.
- Whether the two `SystemdServiceTests` "under home" cases should gate or be rewritten platform-aware —
  a real design call, not answered here.
- Whether item 4's root cause, once found, generalizes to any other place in the codebase that opens a
  second, independent `Repository` handle shortly after a write through the shared `GitCommitService`.
