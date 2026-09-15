# Phase 140 — Windows CI: a real compatibility audit, not just a verification pass

**Status**: Complete — `dotnet-windows` green on run 35001058463 (commit `a2517ad`).
See "Handoff — 2026-09-15" and "CI result — 2026-09-15" at the end of this doc; between them they
supersede several of the hypotheses below, which are kept as written so the correction is legible.
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

## Handoff — 2026-09-15

**Status**: every failure this doc names is fixed and verified green **on a real Windows host** — not a
`windows-latest` runner, but a developer's Windows 11 box running the same `dotnet build -c Release` /
`dotnet test --filter "Category!=Integration"` the `dotnet-windows` job runs. Not yet pushed; no CI run
has confirmed it. Branch: none yet, changes are uncommitted on `main`.

The premise this doc was written under — "no Windows box available", so five fixes went out "confirmed
only by local reasoning" — no longer holds. Everything below was reproduced, root-caused and re-verified
by running the actual tests, which is why three of this doc's four hypotheses turned out to be wrong.

### Item 1 (Negotiate, 253 tests) — the hypothesis was wrong, and the fix is test-only

The doc's framing was that `AuthenticatedApiFactory`-based tests still fail because they register
Negotiate while auth-disabled hosts no longer do. Running one `TestApiFactory` (auth-**disabled**) test
and printing the 500's body disproves it in one line: that host answers
`NotSupportedException: Negotiate authentication requires a server that supports IConnectionItemsFeature`
too. The `!AuthOptions.Disabled` gate never applied to *any* test, because `WebApplicationFactory` layers
its `ConfigureAppConfiguration` in during `builder.Build()` — after the composition root has already read
`builder.Configuration` to make the decision. `DbDataSyncHost`'s own `ApiOptions` registration carries a
comment warning about exactly this trap; the Negotiate gate walked into it. (The gate is still correct in
production, where configuration really is present before `Build()`; it is simply invisible to tests, so it
neither helped nor hurt.)

The real mechanism is broader than "one deep-link fallback route", as this doc suspected but could not
confirm: `NegotiateHandler` implements `IAuthenticationRequestHandler`, so the authentication middleware
runs it on **every** request regardless of which scheme authenticates. Any request through any host with
Negotiate registered answers 500.

Fix: `tests/DbDataSync.Api.Tests/TestServerConnectionItems.cs` (new) — an `IStartupFilter` that supplies a
minimal `IConnectionItemsFeature` (backed by Kestrel's own `ConnectionItems`, whose indexer returns null
for a missing key where `Dictionary` throws — which is how `NegotiateHandler` reads it) to every request,
registered by both factories. This is the second of the three candidate directions this doc listed, and it
wins for a reason worth recording: it fixes the auth-enabled and auth-disabled populations with one change,
needs no production edit, and cannot become a way to authenticate — with the feature present and no
`Authorization` header, `NegotiateHandler` returns without handling anything and the pipeline continues to
the session scheme. The third candidate ("do these tests need Negotiate at all") was checked and answered:
no test calls the one `[Authorize(AuthenticationSchemes = Negotiate)]` endpoint, which is what makes the
shim safe rather than merely convenient.

**`Api.Tests`: 253 failed to 0. 448/448 pass.** Two further failures surfaced underneath, both fixed:

- `ChangeReaderFirstPassContractTests.EveryDeclaredProofNamesATestThatExists` — `FileLoadException:
  Assembly with same name is already loaded`. The existing filter excludes `obj/` but matches only the TFM
  segment, so a tree built in both configurations offers `bin/Debug/net10.0` *and* `bin/Release/net10.0`
  copies of every driver test assembly; and `DbDataSync.Drivers.Loader.Tests` is a real ProjectReference of
  this project, so a copy also sits in this assembly's own output and is already loaded before the test
  runs. Now matches the Configuration segment too, requires the project directory to be named after the
  assembly, and prefers an already-loaded instance over re-loading one. Latent on any machine that has
  built both configurations; CI builds one and never saw it.
- `ChangeCheckPruningTests.WithNoChangeCheckWindow_TheHistoryIsLeftAlone` — a genuine race, nothing to do
  with Windows. `RunPruningService` sweeps **immediately** on startup before settling onto its hourly
  timer, and under a full suite that sweep can land after the test writes its deliberately-ancient row,
  where the default seven-day change-check window deletes the very row the test asserts survives. New
  `UnprunedApiFactory` sets all three retention caps to 0, which `ApiOptions` reads as "no cap" and which
  makes the background loop log that pruning is off and return. Tests that call `PruneAsync` directly
  should not be racing a second sweeper using different arguments.

### Item 2a (Linux-only `Cli.Tests`) — gated where the branch genuinely does not exist, fixed where it does

New `NonWindowsFactAttribute`, deliberately distinct from `LinuxOnlyFactAttribute`: these are the
non-Windows branch of a two-branch switch, which macOS takes exactly as Linux does, so `[LinuxOnlyFact]`
would skip them on a platform that really runs them.

- `ToolCommandTests` — the five tests that drive `ToolCommand.Run` down the POSIX install path are now
  `[NonWindowsFact]`. The five `AddToPath`/`RemoveFromPath` tests are untouched and now genuinely run on
  Windows, which is what a real Windows `tool install` calls.
- `InstallDocsTests.InstallDocs_MentionThisPlatformsDefaultToolDir` — **not** a gating gap.
  `docs/install.md` spells the Windows path as PowerShell's `$env:ProgramFiles\DbDataSync` rather than an
  expanded `C:\Program Files\DbDataSync`, deliberately, since `%ProgramFiles%` is relocatable and
  localized. The test now rebuilds that spelling from the constant, so it still fails if the directory is
  renamed or re-rooted. The doc was right and the test was wrong.

### Item 2a (the `SystemdServiceTests` "design call") — split, because the two cases are not the same

This doc framed both "under home" cases as one decision. They are not.

- `Install_ExecutableUnderHomeWithANonHardenedRoot_WarnsButStillRegisters` — **made platform-aware, not
  gated.** Only the *path* was POSIX-specific: a new `AnExecutableUnderTheUserProfile()` helper uses
  `Environment.SpecialFolder.UserProfile`, which is precisely what `CliOptions.IsUnderUserProfile` reads on
  Windows and resolves to `$HOME` elsewhere, so one expression matches the production check on both
  branches. The logic under test — "is this executable somewhere a service will stop being able to reach" —
  is worth covering on Windows and now is.
- `Install_ExecutableUnderHomeWithTheHardenedDefaultRoot_RefusesRatherThanRegisteringABrokenUnit` —
  **`[LinuxOnlyFact]`.** The refusal fires only when the resolved root *is* `/var/lib/dbdatasync`, and
  `CliOptions.DefaultRoot` equals that on Linux alone: Windows resolves `%ProgramData%\DbDataSync` and
  macOS `/Library/Application Support/DbDataSync`. `--repo` cannot manufacture it either, since
  `Path.GetFullPath` roots a leading slash onto the current drive. So this was latently broken on macOS
  too, where nothing has ever run this suite. `LinuxOnlyFactAttribute`'s doc and skip message were widened
  to cover this second, binary-free kind of use.

### Item 2b (certificate/crypto, 8 tests) — root-caused: a dispatch gap, not a crypto bug

This doc asked for a clean log before assuming test bug vs. real bug. With the failures reproduced
individually the answer is unambiguous, and it is neither: on Windows `CertCommand.Run` dispatches
`new-self-signed` to the **store**-based phase 82 implementation, so `NewSelfSignedFileTests` asserted a
managed PFX that the command they invoked never set out to write. Nothing is wrong with the file-based
path — it is genuinely cross-platform and `SelfSignedCertificateServiceTests` exercises it on whatever OS
runs them. There is simply no Windows door to it.

- `NewSelfSignedFileTests` (5) — `[NonWindowsFact]`, with the reasoning recorded on the class.
- `ReadinessChecksTests.ManagedSelfSignedCertificate_*` (2) — **not gated.** Their subject is
  `ReadinessChecks`' reading of a managed certificate, which is cross-platform; only the setup (shelling
  through `new-self-signed`) was Windows-divergent. A new `BindAManagedSelfSignedCertificate` helper writes
  the managed PFX and points Kestrel at it directly, keeping the coverage on every platform.
- `CertUsePemTests.Status_NothingConfigured_...` (1) — **not gated**, renamed
  `..._ReportsNoCertificateAndPointsAtTheNextStep`. With nothing bound, `status` falls through to
  `StatusForStoreCertificate` on Windows, whose advice is the store route; off Windows it is the file
  route. Both are correct, the claim ("it tells the operator what to do next") holds on both, and Windows
  had no test proving it at all before now. Asserted per platform.
- `ConfigCommandTests.Cert_ForwardsToCertCommand` (1) — its subject is the *forward*, not platform gating;
  it asserted the Windows-only refusal for `cert list` with a comment reading "this environment is Linux".
  Now uses an unknown subcommand, whose handling is identical everywhere.

**Real product gap named, deliberately not closed**: Windows cannot reach phase 130's tier 2 from the CLI
at all, while `DbDataSyncHost` will happily run `SelfSignedCertificateService` there when the config names
the managed path. Whether `new-self-signed` on Windows should offer the file-based route alongside the
store one is a product decision, not a phase 140 cleanup.

### Item 3 (`State.Tests`) — not timing, not the scheduler: a half-stated file-sharing contract

`RemoteRunnerStateTests.Once_the_owner_is_declared_lost_nothing_waits_on_the_network_again` fails with
`IOException: the process cannot access the file ... because it is being used by another process` — it
reads the journal while the `RemoteRunnerState` that owns it is still alive, where its sibling tests
dispose first. The guess in this doc ("likely a timing-sensitive test ... Windows' scheduler or clock
resolution") was wrong.

**A real production bug, fixed in `StateJournal`.** `Append` deliberately opens with `FileShare.Read` —
stating the intent that a journal may be read while it is being written — but `Read` used `File.ReadLines`,
whose default share mode denies writers and therefore cannot open a file a live write handle already holds.
Half a contract. POSIX enforces no sharing at all, which is why the missing half read as working. `Read`
now opens explicitly with `FileShare.ReadWrite`. The test is left as written, since it now genuinely covers
the concurrent case. **`State.Tests`: 197/197.**

### Item 4 (`ConnectionsControllerTests.Upsert_NeverReturnsOrCommitsPlaintextPassword`) — was collateral

Passes with no change of its own. It was downstream of item 1: the `NullReferenceException` indexing
`repo.Head.Tip[path]` came from a commit that never happened, because the request that would have made it
answered 500. The `LibGit2Sharp`-sees-a-stale-tree theory was a red herring, and the open question about
whether it "generalizes to any other place that opens a second `Repository` handle" does not arise.

### Two more clusters this doc never enumerated

- **`Core.Tests` 252/1** — this doc's own counts mention it without naming it:
  `ConcurrentConfigReadTests.AReadDuringAWrite_NeverSeesAHalfWrittenFile`, failing deterministically with
  `UnauthorizedAccessException` out of `WriteAtomically`. **A second real production bug, and the exact
  mirror of item 3.** A previous session added `FileShare.Delete` to the read side for this very test;
  necessary, but not sufficient. `File.Move(overwrite: true)` becomes `MoveFileEx` with
  `MOVEFILE_REPLACE_EXISTING`, which replaces by *deleting* the destination first — and a Windows delete
  with open handles only marks the name for deletion, so the rename that follows finds it still taken and
  fails `ERROR_ACCESS_DENIED`. No share mode a reader can ask for prevents that. Isolated with a scratch
  probe: a writer alone completes 2,000 replacements, a writer with one concurrent reader fails on its
  *first*, and git is not involved. `File.Replace` fails the same way. `File.Move` retried against a time
  budget completed **352 replacements against 9,208 concurrent reads without a failure**, so that is the
  fix — each attempt is still an atomic replace, so the guarantee the method name makes is unchanged.
- **`Drivers.Generic.Tests` 181/3** — *not* a CI failure, and worth recording so the next session does not
  chase it. `Scd2WriterTests` x2 and `KeyReconcileStatementTests` x1 compare generated SQL against raw
  string literals, and 590 files in this working tree still had CRLF: `.gitattributes` (added the same day,
  for exactly this class of bug) normalizes on *checkout*, and a tree checked out before it landed keeps
  what it had. A fresh CI checkout gets LF and these pass. Normalizing the working tree to LF fixed all
  three — and a stale index made `git status` report 607 modified files afterwards while `git diff HEAD`
  correctly reported 13; rebuilding the worktree's index cleared it.

### Numbers, measured not assumed

Whole solution, `dotnet test -c Release --filter "Category!=Integration"` on Windows 11:

| Project | Before | After |
|---|---|---|
| `Api.Tests` | 253 failed / 448 | **0 failed, 448 passed** |
| `Cli.Tests` | 17 failed (CI) | **0 failed, 117 passed, 13 skipped** |
| `State.Tests` | 1 failed / 197 | **0 failed, 197 passed** |
| `Core.Tests` | 1 failed / 252 | **0 failed, 252 passed** |
| `Drivers.Generic.Tests` | 3 failed (local only) | **0 failed, 184 passed** |
| every other project | green | green |

Skips went 2 to 13 in `Cli.Tests`: the 2 pre-existing `[LinuxOnlyFact]` systemd cases, plus 10
`[NonWindowsFact]` and 1 more `[LinuxOnlyFact]` added here. Every one is a branch Windows does not
execute, and each carries its reasoning at the site.

### What is left

1. **Push and read a real `dotnet-windows` run** — the one step of "What this phase will do" not done
   here, and the one this doc is most insistent about. Everything above is verified on a real Windows
   host, which is a far stronger position than the reasoning-only pass this doc was correcting, but it is
   still not the runner.
2. **Four tests fail locally and are expected to pass in CI**, which the run above should confirm:
   `WindowsServiceEventLogTests` x3 (`SecurityException` — `EventLog.SourceExists` needs Administrator to
   enumerate the Security log) and
   `ServiceCommandTests.GrantDataDirectoryAccess_LocalSystem_TakesOwnershipInsteadOfReturningEarly`
   (`icacls` ownership transfer to SYSTEM needs elevation; the assertion saw the owner still as the
   invoking user). This session's shell was confirmed non-elevated. That they pass elevated is exactly
   what phase 136's own retrospective predicted, and it also makes this doc's arithmetic claim about
   phases 135/136 directly checkable in the run's output rather than by subtraction. They are deliberately
   **not** gated behind an "elevated only" attribute: that would risk silently skipping the very coverage
   those phases exist to prove, and CI is elevated. A developer on a non-elevated Windows box will see
   these 4 red; that is a known condition, not a regression.
3. Once green: move this doc to `done/` per the README's workflow.

## CI result — 2026-09-15 — `dotnet-windows` green, phase closed

Commit `a2517ad`, run [35001058463](https://github.com/DbDataSync/DbDataSync/actions/runs/35001058463).

**`dotnet-windows`: success.** Restore, Build and Test all green. `dotnet test` exits non-zero if a
single test fails, so a passing Test step is a direct statement that **every** non-Integration test in
the solution passes on a real `windows-latest` runner — the first time that has ever been true.

The trend across the four most recent runs on `main` isolates this to the work in `a2517ad` rather than
to anything else that landed alongside it:

| run | `dotnet-windows` | `dotnet` | `web` | `playwright` | `dotnet-integration` |
|---|---|---|---|---|---|
| `fd9ca25` | fail | pass | pass | fail | fail |
| `885c569` | fail | pass | pass | fail | fail |
| `cc278dd` | fail | pass | pass | fail | fail |
| **`a2517ad`** | **pass** | pass | pass | fail | fail |

### The last open claim, now settled

Item 2 of "What is left" — the four tests that fail on a non-elevated developer box and were *predicted*
to pass on the elevated runner — is answered by the green Test step: had any of
`WindowsServiceEventLogTests`' three or
`ServiceCommandTests.GrantDataDirectoryAccess_LocalSystem_TakesOwnershipInsteadOfReturningEarly` failed,
the step would be red. So phase 135's real `icacls` ownership-transfer checkpoint and phase 136's three
real Event Log checkpoints **ran and passed on Windows**, which is what this doc's "Confirmed fixed for
real" section could previously only establish by subtracting skip and failure counts. Those phases'
retrospectives are now confirmed by observation rather than by arithmetic.

What is still *not* directly read is the literal Event Log / `icacls` text in the job log — that needs an
authenticated `gh` (the unauthenticated logs endpoint answers 403), and this session had none. The green
Test step is a stronger signal than the arithmetic it replaces, but it is a pass/fail signal, not the
output itself — written up, alongside phase 136's own related gaps, in
`architecture/planning/todo/follow-up-phase-136-140-windows-service-event-log-output-never-read-by-a-human.md`.

### Two jobs still red, neither caused here and neither in scope

- **`dotnet-integration`** — out of scope by this doc's own "Out of scope" section, tracked as phase 141.
  Red on every run in the table, including the three before this work.
- **`playwright`** — red on every run in the table too, so not a regression from this commit. Worth
  flagging because this doc's own "Confirmed fixed for real" section says "the `dotnet` and `playwright`
  jobs are both green as of the latest run": that was true when written and has not been true for at
  least the last four runs on `main`. It is not covered by phase 140 or phase 141, and it is the job the
  `playwright-suite-in-ci.md` planning doc exists to keep honest — it needs its own follow-up.

### Status

Phase 140's stated goal — a real Windows compatibility audit closing out what real CI runs actually
showed — is met, and `dotnet-windows` is green. Moved to `done/`.
