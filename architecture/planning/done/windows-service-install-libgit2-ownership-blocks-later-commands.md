# Windows: `config check`, `serve`, `setup`, and most other commands fail once the data directory has been installed as a service

**Status: direction agreed 2026-09-18 — option 1 below, scoped. Not yet designed in detail or built.**

## The symptom

After `dbdatasync service install` on Windows, running other CLI commands against the same data
directory — `config check`, `serve` (outside the service), `setup`, and reportedly most others — starts
failing. Not intermittent: every affected command fails after install.

## Cause — confirmed as folder ownership

Confirmed: this is the flip side of phase 135
(`architecture/implementation/done/phase-135-windows-service-data-directory-ownership.md`). That phase
made `service install` unconditionally take **ownership** of the data directory for the service account
(`icacls /setowner <account> /T /C`, including `SYSTEM` for `LocalSystem`) — necessary because libgit2
refuses to open a repository it doesn't own (the same class of check as git's own CVE-2022-24765
`safe.directory`), and before phase 135 that made the service itself fail to start.

But that fix only considered the service's own identity. Once install transfers ownership to the
service account, an interactive admin running `config check`/`setup`/a foreground `serve` afterward is
now a *different* owner from libgit2's point of view, and may hit the exact same "repository path ... is
not owned by current user" error phase 135's own "Why" section quotes — just from the other direction.

Phase 135's own `ReadinessChecks` addition (`ServiceRegistrationCheck`) explicitly anticipated an
identity mismatch between the registered account and whoever runs `config check`, but only made it
*informational* — it reports the mismatch, it doesn't work around libgit2 rejecting the mismatch
outright. If that's the real mechanism here, the check was solving the wrong half of the problem.

## What a fix needs to deal with

- libgit2's ownership check is per-directory-owner, not ACL-based — the existing `/grant` ACL step
  (correctly) never fixed this, which is exactly why phase 135 needed `/setowner` in the first place.
- Whatever the fix is, it can't just flip ownership back to the interactive user at `config check` time
  and leave it there, or it breaks the service again the same way un-fixed phase 135 did — this needs to
  work for *both* the service account and an interactive admin, potentially invoked minutes apart, without
  one breaking the other.

## Decision (2026-09-18)

Going with option 1, scoped: **disable libgit2's ownership check for interactive, terminal-run
commands only** (`config check`, `setup`, a foreground `serve`, and "most other commands" per the
original report) — **not** for the long-running Windows Service process, which keeps the check on for
now. Show a warning before disabling it.

This sidesteps most of option 1's "process-wide" concern from below: the service host and an interactive
CLI invocation are already separate OS processes (`sc.exe`-started vs. a terminal launching
`dbdatasync.exe` directly), so "disable it in the CLI's interactive entry points, leave the service
process alone" is a real, natural boundary to hang the `GlobalSettings.SetOwnerValidation(false)` call
on — not a compromise on top of a single shared process. The service keeps failing the way phase 135
already made it fail (fast, with a clear message) if it somehow isn't the owner; nothing about that
changes here. Option 2 (auto-transfer-and-restore) is not being pursued.

## Two candidate directions considered (for reference — option 1, scoped as above, is the one chosen)

1. **Disable the ownership check in libgit2 outright.** A real hook for this exists and was confirmed
   present in the exact LibGit2Sharp version this repo already depends on (`LibGit2Sharp` 0.32.0,
   `src/DbDataSync.Core/DbDataSync.Core.csproj`): the managed assembly exports
   `GlobalSettings`-level `GetOwnerValidation`/`SetOwnerValidation`, wrapping libgit2's own
   `git_libgit2_opts_get/set_owner_validation`. That's a process-wide `git_libgit2_opts` flag, not
   per-repository or per-path — the same blunt instrument git's own `core.fscache`-adjacent globals are,
   not a scoped `safe.directory`-style allowlist. Turning it off removes the protection CVE-2022-24765
   exists for, everywhere this process opens *any* repository, for the lifetime of the process — worth
   weighing against how much that protection actually buys here, given the data directory is not a
   general-purpose git repo an untrusted party could plant files into.
2. **Warn, then transfer ownership automatically, then restore it.** Detect the mismatch (the
   `ServiceRegistrationCheck`/`Prepare()` machinery phase 135 already added knows the registered account;
   comparing it against the current process identity is exactly the comparison that check's own doc
   comment says was deliberately *not* done, to avoid false-positiving on an admin running `config check`
   against a service-owned directory in the ordinary course of things — that reasoning would need
   revisiting, not just reused, since here the mismatch is the actual trigger condition, not a false
   positive to suppress) → warn → take ownership for the current interactive user just long enough to run
   the command → hand ownership back to the service account before exiting. Cheaper on the security
   question than option 1 (no global protection disabled, no window where the directory has the wrong
   owner for longer than one command), but real failure modes to design for: a crash or a killed process
   between "took ownership" and "restored it" leaves the directory owned by the wrong account for the
   *next* run of whichever side didn't get to finish — arguably the same bug, recurring, unless the
   restore is made robust to that (e.g. every command checks and repairs ownership before it does anything
   else, rather than assuming the previous command's restore ran).

## Where the call goes — resolved

`ServeCommand.cs` already has the exact predicate needed, used twice today for a different purpose (the
Event Log milestone write, and `Fail`'s Event-Log-vs-`Console.Error` branch): `OperatingSystem.IsWindows()
&& Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()`.
`WindowsServiceHelpers.IsWindowsService()` is a runtime check (is this process's parent the SCM), not a
command-line flag — the service is registered with the identical `serve --repo ... --url ...` command
line a terminal would type (`ServiceCommand.cs`'s `binPath`), so there is no other way to tell them apart.
That means one call, gated the same way, at the top of `Program.cs` before any command dispatches, covers
every command uniformly: `serve` run by the SCM sees `IsWindowsService() == true` and is left alone;
`serve` (or `config check`, `setup`, anything else) run from a terminal sees `false` and gets the
disable+warning. No per-command branching needed.

**Platform scope**: gated `OperatingSystem.IsWindows()` — Linux already gets ownership right
unconditionally (phase 135's `chown -R`), so nothing there needs to change or is touched by this.

## Outcome

Agreed and implemented as
`architecture/implementation/todo/phase-157K-windows-interactive-cli-disables-libgit2-ownership-check.md`.
