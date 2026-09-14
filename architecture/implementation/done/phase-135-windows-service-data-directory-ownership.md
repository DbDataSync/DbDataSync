# Phase 135 — Windows service install takes ownership of the data directory

**Status**: Complete.
**Plan reference**: `architecture/planning/done/windows-service-data-directory-ownership.md`.

## Why

Root-caused a real production Error 1053 this session. `dbdatasync service install` registering
`LocalSystem` against a data directory that already existed (created earlier by an interactive
`dbdatasync setup` run) fails every start:

```
Could not prepare the config repository at 'C:\ProgramData\DbDataSync': repository path
'C:/ProgramData/DbDataSync' is not owned by current user
```

This is libgit2's ownership-safety check (the same protection as git's own CVE-2022-24765
`safe.directory` fix) — a check on the directory's **owner**, not its ACL. `icacls` showed
`NT AUTHORITY\SYSTEM:(F)` already present, inherited correctly from `%ProgramData%`'s default ACL, and
libgit2 refused anyway because ownership hadn't transferred.

`GrantDataDirectoryAccess` (`src/DbDataSync.Cli/ServiceCommand.cs`) grants ACL rights for a named
`--account` and explicitly skips the call for `LocalSystem`:

```csharp
if (account is null || string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
    return;
```

with the comment "LocalSystem needs no grant — its access already covers a directory anyone just
created." True for access rights, not for ownership — and a named account hits the identical ownership
check too, since the existing `/grant` call never transfers ownership either. Verified manually that
this resolves it:

```powershell
icacls "C:\ProgramData\DbDataSync" /setowner "SYSTEM" /T /C
```

The Linux path already gets this right — `SystemdService.cs:39` runs `chown -R` to the configured
`User=`/`Group=` unconditionally on every install. This phase brings Windows to parity.

## Design

Replace `GrantDataDirectoryAccess`'s early return with an unconditional two-step `icacls` run, for
every account including `LocalSystem` (icacls's recognized name for it is `SYSTEM`, not the string
`LocalSystem` the rest of this file uses — matching the manual verification above):

```csharp
private static void GrantDataDirectoryAccess(string root, string? account)
{
    Directory.CreateDirectory(root);
    var effectiveAccount = account ?? "SYSTEM";

    RunIcacls(root, ["/setowner", effectiveAccount, "/T", "/C"],
        $"take ownership of '{root}' for '{effectiveAccount}'");
    RunIcacls(root, ["/grant", $"{effectiveAccount}:(OI)(CI)M", "/T"],
        $"grant '{effectiveAccount}' access to '{root}'");
}
```

Two changes to the existing `/grant` step beyond just no-longer-skipping `LocalSystem`:

- **`/T` added** — the existing call never had it, so it only set *inheritable* rights for future
  children, never applied to files/subdirectories that already exist (the git repo internals, `state.db`)
  at the point `service install` runs against a pre-existing data directory — the exact scenario that
  triggered this bug in the first place.
- **Runs before `/grant`, not instead of it** — taking ownership alone gives the new owner implicit
  `WRITE_DAC` (the right to change permissions), not necessarily explicit data access; keeping the
  existing `/grant` step (now recursive) after `/setowner` is the same "own it, then also explicitly
  grant it" combination that resolved this manually.

`RunIcacls` factors out the existing `Process.Start("icacls")`/`WaitForExit`/warn-don't-fail pattern
`GrantDataDirectoryAccess` already has, so both steps share it rather than duplicating the
`ProcessStartInfo`/exit-code-warning boilerplate — same non-fatal posture as today: a failure here warns
and lets `service install` continue, it doesn't block registration.

## Design addition — the data directory records who it was registered for

Added before implementation started (2026-09-14), at the user's request: the configured data directory
should know whether a service has ever been registered against it, and under which account — both
`service install`'s Windows path and `SystemdService.Install`'s Linux path, since the same "was this
account the one that set this directory up" question applies on both. This is squarely phase 135's own
territory (the directory-ownership story), not a new phase, so it lands here rather than as a separate
doc.

A new `ServiceRegistration` (`src/DbDataSync.Cli/ServiceRegistration.cs`), modeled directly on the
existing `PendingEnrollmentStore` (`src/DbDataSync.Certificates/PendingEnrollmentStore.cs`): a plain
JSON file (`service-registration.json`) at the repo root, sibling to `config/`, deliberately outside the
git-tracked config repository (per-machine operational state, not configuration meant to be diffed or
reviewed) and `.gitignore`d the same way. Holds `ServiceRegistrationInfo(string Account, string Platform,
DateTimeOffset RegisteredAtUtc)`.

Wired in four places:

- `ServiceCommand.Install` (Windows) writes it (`Account` = `account ?? "LocalSystem"`, `Platform` =
  `"windows"`) once `sc.exe create` actually succeeds — not before, so a failed registration never
  claims a service exists that doesn't.
- `ServiceCommand`'s `uninstall` dispatch gains the root resolution it doesn't have today (matching
  `Install`'s `--repo`/`CliOptions.DefaultRoot` pattern) so it can clear the marker before `sc.exe
  delete`.
- `SystemdService.Install` (Linux) writes it (`Account` = the resolved `user`, `Platform` = `"linux"`)
  once `daemon-reload`/`enable` both succeed, right before the final success message.
- `SystemdService.Uninstall` gains an `args` parameter (it currently takes none) to resolve root the
  same way and clear the marker before disabling the unit.

Two consumers, matching "should help with error messages and configuration checks" directly:

- `ServeCommand.RunAsync`'s existing `Prepare()` catch block (the exact ownership-failure message this
  phase's own root cause produces) grows a second line when a registration marker is present: which
  account/platform the directory was set up for, and when — the detail an operator debugging Error 1053
  needs and today has to go dig for in `service install`'s original console output (if they even kept
  it).
- A new `ReadinessChecks` entry (`ServiceRegistrationCheck`), added right after `RepoCheck` since it's
  about the same directory-identity question. Always `CheckStatus.Ok` — purely informational, not a
  pass/fail judgment (comparing the registered account against the process's *current* identity would
  false-positive constantly, since `config check` is normally run interactively by an admin, not by the
  service account itself) — reporting either "Not registered as a service." or the recorded
  account/platform/timestamp.

## Checkpoints

1. `RunIcacls` helper extracted from the existing inline `icacls` `Process.Start` call in
   `GrantDataDirectoryAccess`, taking the argument list and a description for the warning message —
   no behavior change yet, just the extraction.
2. `GrantDataDirectoryAccess` rewritten per the design above: no more `LocalSystem` early return,
   `effectiveAccount` resolved once, `/setowner` then `/grant` (now with `/T`), both via `RunIcacls`.
3. `ServiceRegistration` added per the design addition above, and wired into `ServiceCommand.Install`/
   `Uninstall` and `SystemdService.Install`/`Uninstall`.
4. `ServeCommand.RunAsync`'s `Prepare()` catch block reads the marker (if any) and appends the
   account/platform/timestamp line; extracted into a small pure helper so it's unit-testable without a
   real host.
5. `ReadinessChecks` gains `ServiceRegistrationCheck`, listed right after `RepoCheck`; existing
   `ReadinessChecksTests.JsonOutput_IsValidAndNamesEveryCheck` updated for the new check name.
6. Test coverage: `ServiceCommandTests` (or wherever `ServiceCommand`'s Windows-only pieces are already
   tested) gets a case proving `LocalSystem` now reaches the icacls calls instead of returning early —
   real filesystem `icacls` execution the way the fix will actually run, matching this repo's own
   "real, not mocked" precedent for install-time OS interaction, not a fake process runner. Plus real
   (not mocked) `ServiceRegistration` round-trip tests, and `SystemdServiceTests` coverage for the
   marker being written/cleared through the existing `FakeSystemdEnvironment` harness.
7. Manual verification note in the retrospective: re-run the exact repro that surfaced this bug (a data
   directory created by an interactive `setup` run, then `service install --account` unspecified,
   i.e. `LocalSystem`) and confirm the service starts.

## Retrospective

Built as designed, plus the service-registration addition agreed before implementation started (see
"Design addition" above). No changes to either design once implementation was underway.

- **Ownership fix**: `RunIcacls` extracted from the old inline `icacls` `Process.Start`;
  `GrantDataDirectoryAccess` rewritten to run for every account (including `LocalSystem`, resolved to
  icacls's own `SYSTEM` name), `/setowner ... /T /C` before a now-recursive (`/T`) `/grant`. This is the
  Windows-worktree side of this repo running on Linux this session, so the actual `icacls.exe` execution
  could not be exercised directly here — instead: the exact manual repro the "Why" section already
  verified (`icacls "C:\ProgramData\DbDataSync" /setowner "SYSTEM" /T /C`) matches the code verbatim, and
  a real, non-mocked test (`ServiceCommandTests.GrantDataDirectoryAccess_LocalSystem_TakesOwnershipInsteadOfReturningEarly`,
  gated `[WindowsOnlyFact]`/`[SupportedOSPlatform("windows")]`) is in place to run for real the next time
  this executes on Windows CI or a Windows dev box — it currently reports `[SKIP]` here rather than
  silently passing, matching this repo's existing `*WindowsTests` convention exactly
  (`WindowsOnlyFactAttribute`, new to `DbDataSync.Cli.Tests`, modeled on `DbDataSync.Api.Tests`' own
  copy). The 124 other tests in the project, including every other `ServiceCommandTests`/
  `SystemdServiceTests` case, ran for real and passed.
- **Service registration addition**: `ServiceRegistration` (`src/DbDataSync.Cli/ServiceRegistration.cs`)
  — a `service-registration.json` marker at the data directory root, gitignored, modeled directly on
  `PendingEnrollmentStore`. Wired into `ServiceCommand.Install`/`Uninstall` (Windows) and
  `SystemdService.Install`/`Uninstall` (Linux, which gained an `args` parameter on `Uninstall` it didn't
  have before, to resolve `--repo`/the default root the same way `Install` already does).
  `ServeCommand.RunAsync`'s `Prepare()` failure message — the exact ownership-error text this phase's own
  bug produces — now appends the recorded account/platform/timestamp when a marker is present, via a new
  pure `ServeCommand.PrepareFailureMessage` helper (unit-tested directly, no real host needed). A new
  `ServiceRegistrationCheck` in `ReadinessChecks`, right after `RepoCheck`, reports the same thing for
  `dbdatasync config check` — always `Ok` (informational only; comparing against the *current* process
  identity was deliberately rejected as a false-positive generator, since `config check` is normally run
  by an interactive admin, not the service account).
- Real bug caught by writing the tests, not a design flaw: `SystemdServiceTests.Uninstall_...`'s original
  call (`SystemdService.Uninstall(env)`) broke once `Uninstall` needed an `args` parameter for root
  resolution — caught immediately by the compiler, fixed by passing `["--repo", _root]` explicitly (and
  a new `Install_ThenUninstall_WritesThenClearsTheServiceRegistrationMarker` test added alongside it to
  actually exercise the write/clear round trip through the real `FakeSystemdEnvironment` harness, not
  just prove the signature compiles).
- New tests: `ServiceRegistrationTests` (real filesystem read/write/clear/gitignore round trip, 6 cases),
  two new `ReadinessChecksTests` cases for `ServiceRegistrationCheck`, two new
  `ServeCommandPrepareTests` cases for `PrepareFailureMessage`, one new `SystemdServiceTests` case for
  the write/clear round trip, one new (Windows-only, currently skipped here) `ServiceCommandTests` case
  for the real `icacls` ownership transfer. `ReadinessChecksTests.JsonOutput_IsValidAndNamesEveryCheck`
  updated for the new "Service registration" check name. Full `DbDataSync.Cli.Tests` suite: 124 passed,
  1 skipped (Windows-only), 0 failed.
- **Not yet done**: the actual Windows repro this phase exists to fix (register a service against a
  data directory an interactive `setup` run already created, confirm the service starts) needs a real
  Windows box or Windows CI runner — not available in this session. Flagged explicitly rather than
  claimed done; the code change matches the verified manual fix from the "Why" section exactly, and the
  one Windows-only test that would prove it end-to-end is written and in place, just not yet run for
  real.
