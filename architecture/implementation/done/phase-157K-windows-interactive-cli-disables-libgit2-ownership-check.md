# Phase 157 — an interactive CLI command on Windows disables libgit2's repository-ownership check

**Status**: Complete.
**Plan reference**: `architecture/planning/done/windows-service-install-libgit2-ownership-blocks-later-commands.md`.

## Why

Phase 135 made `service install` on Windows take ownership of the data directory for the service
account (`icacls /setowner <account> /T /C`), because libgit2 refuses to open a repository it doesn't
own — the same class of check as git's own CVE-2022-24765 `safe.directory` fix. That fixed the service's
own startup, but only considered the service's identity: once install transfers ownership away from
whoever ran it, that same person running `config check`, `setup`, or a foreground `serve` afterward
against the same data directory is now libgit2's idea of the *wrong* owner, and hits the identical
"repository path ... is not owned by current user" error phase 135's own retrospective quotes — just
approaching it from the other direction. Reported by the user as: "on windows, running config check,
serve, and probably setup and most other command fails after installing as a service."

Two directions were weighed (see the plan doc's own "Two candidate directions" section): disable
libgit2's ownership check outright, or warn-transfer-and-restore ownership around each interactive
command. The second one's real failure mode — a crash between "took ownership" and "restored it" leaves
the directory wrong-owned for whoever runs next, i.e. a recurrence of this exact bug — is what settled it
in the first direction's favor.

## Design

**One call, at CLI startup, gated on not being the Windows Service process**, using a predicate
`ServeCommand.cs` already relies on twice today for an unrelated purpose (the Event Log startup
milestone, and `Fail`'s Event-Log-vs-console branch): `OperatingSystem.IsWindows() &&
Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()`.
`IsWindowsService()` is a runtime check — is this process's parent the Service Control Manager — not a
command-line flag, which matters here because the service is registered with the *identical* `serve
--repo ... --url ...` command line a terminal would type (`ServiceCommand.cs`'s own `binPath`
construction). That means the same one call, placed once, correctly leaves the service alone and
disables the check for every other invocation of every command, with no per-command branching:

```csharp
internal static class GitOwnershipValidation
{
    internal const string WarningMessage =
        "Warning: disabling libgit2's repository-ownership check for this command. Installing " +
        "DbDataSync as a Windows service transfers ownership of the data directory to the service " +
        "account, which otherwise blocks every other command run against the same directory.";

    public static void DisableIfInteractive(TextWriter? warningWriter = null)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            return;

        (warningWriter ?? Console.Error).WriteLine(WarningMessage);
        LibGit2Sharp.GlobalSettings.SetOwnerValidation(false);
    }
}
```

Called once from `Program.cs`, after the `--help`/no-args early return (no point warning on a command
that never touches the repo) and before command dispatch, so it runs ahead of every command's own git
access:

```csharp
GitOwnershipValidation.DisableIfInteractive();

var command = args[0].ToLowerInvariant();
...
```

`GlobalSettings.SetOwnerValidation`/`GetOwnerValidation` were confirmed present on the exact
`LibGit2Sharp` 0.32.0 version this repo already depends on (`src/DbDataSync.Core/DbDataSync.Core.csproj`)
by loading the installed package assembly directly and listing `GlobalSettings`'s public static methods —
not assumed from the package's changelog. It wraps libgit2's own `git_libgit2_opts_set_owner_validation`;
process-wide, not scoped to one path, which is exactly why this is being placed in the CLI's own
short-lived process rather than anywhere inside the long-running `serve`/service host.

**Not changed**: the Windows Service process itself. It keeps the check enabled and keeps failing the way
phase 135 already made it fail (fast, with a clear message naming the registered account) if it somehow
isn't the owner — nothing about that path is touched here.

**Platform scope**: gated on `OperatingSystem.IsWindows()`. Linux already gets ownership right
unconditionally (phase 135's `chown -R` on every `SystemdService.Install`), so this is a no-op there by
construction, not by coincidence.

## Checkpoints

1. `GitOwnershipValidation` (`src/DbDataSync.Cli/GitOwnershipValidation.cs`) per the design above.
2. One call added to `Program.cs`, placed after the help/no-args early return, before command dispatch.
3. Tests (`tests/DbDataSync.Cli.Tests/GitOwnershipValidationTests.cs`): real (not mocked) exercise of
   `DisableIfInteractive` — `[WindowsOnlyFact]` since it can only meaningfully disable anything on
   Windows, matching this repo's existing `WindowsOnlyFactAttribute` convention. Because
   `GlobalSettings.SetOwnerValidation` is genuine process-wide libgit2 state (shared across every test in
   the same test process), every case reads the current value first and restores it in a `finally`, so
   this test can never leave a changed global behind for whatever test the runner happens to schedule
   next.
   - Not running as the Windows Service (the real, un-mocked case every test process actually is):
     warning written, `GlobalSettings.GetOwnerValidation()` becomes `false`.
   - A non-Windows run: no warning, no libgit2 call — this half runs and passes for real on this Linux
     sandbox, unlike the Windows-only case above.

## Retrospective

Built as designed, no changes underway. `GitOwnershipValidation.DisableIfInteractive` added and wired
into `Program.cs`'s startup, right after the existing help/no-args early return. `GlobalSettings.
SetOwnerValidation`/`GetOwnerValidation`'s exact signatures were confirmed by loading the installed
`LibGit2Sharp.dll` via reflection and listing `GlobalSettings`'s public static methods directly, not
assumed.

**Verified on this (Linux) sandbox**: the non-Windows half of `DisableIfInteractive` (no-op, confirmed by
a real, un-skipped test run) and the full `DbDataSync.Cli.Tests` suite otherwise unaffected — 140 passed,
11 skipped (all pre-existing Windows-only cases plus this phase's own new one, see below), 0 failed.

**Not verified here, same posture as phase 135's own retrospective**: the actual Windows behavior — that
an interactive `config check` after `service install` now succeeds where it previously failed — needs a
real Windows box or Windows CI runner, neither available in this session. The `[WindowsOnlyFact]` test
covering the real (non-service) branch is written and in place, reporting `[SKIP]` here rather than
silently passing, and will run for real the next time this executes on Windows.
