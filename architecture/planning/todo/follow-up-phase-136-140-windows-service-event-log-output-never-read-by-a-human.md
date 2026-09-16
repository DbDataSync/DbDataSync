# Phase 135/136's Windows Event Log and `icacls` work: pass/fail confirmed on CI, literal output never read

**Status: partly closed 2026-09-15 — item 3 answered and fixed, items 1 and 2 still open.** Extracted
from `architecture/implementation/done/phase-136-windows-service-startup-diagnostics.md`'s own "What's
honestly still unverified" section and
`architecture/implementation/done/phase-140-windows-ci-verification-and-remaining-failure.md`'s own "CI
result" section, both of which named this and left it — moved here per
`architecture/implementation/README.md`'s "Follow-up work gets its own doc, not a paragraph."

## What's actually still open, after phase 140

Phase 136 shipped Windows service startup diagnostics (Event Log writes) and named three things as
"honestly still unverified," needing a real Windows box. Phase 140 later got a real `dotnet-windows` CI
run and closed most of this **by inference, not by direct observation**: a green `Test` step on
`windows-latest` means every non-Integration test passed, including
`WindowsServiceEventLogTests`' three real Event Log round-trip tests and phase 135's own real `icacls`
ownership-transfer test — so the tests *ran and passed*, confirmed by the step's own exit code rather
than by arithmetic (phase 140's earlier, weaker evidence). Phase 140's own words: "The green Test step is
a stronger signal than the arithmetic it replaces, but it is a pass/fail signal, not the output itself.
Naming the remaining gap rather than calling it closed."

**What remains open, precisely:**

1. **Nobody has read the literal Event Log / `icacls` text the tests produced.** *(Largely addressed —
   the tests now report what they observed; what remains is only the passing-run case, see "Update"
   below.)* The tests asserted specific content and passed, but the actual log lines had never been read
   by a human, only inferred from a green checkmark.

2. **Phase 136's own Checkpoint 6 — a *manual* scenario, not a test** — reproducing phase 135's original
   Error 1053 startup failure against a real installed Windows service, and confirming the diagnostic
   message appears in Event Viewer without the Scheduled-Task workaround phase 135's own investigation
   needed. This was never a test at all, so no CI run — green or not — closes it. **Still not done**, and
   it needs an elevated Windows shell willing to install and deliberately break a real service.

3. ~~**Whether a *non-elevated* real Windows install's first `service install` can call
   `EventLog.CreateEventSource` successfully.**~~ **Answered, and it was worse than "no" — now fixed.**
   See below.

## Item 3: answered on a real non-elevated Windows shell, and fixed

Run for real (`dbdatasync service install --repo <temp>`, non-elevated, real Windows 11), the answer is
not merely that the source cannot be registered. The command **crashed**:

```
  Ran icacls to take ownership of '<repo>' for 'SYSTEM'.
  Ran icacls to grant 'SYSTEM' access to '<repo>'.
Unhandled exception. System.Security.SecurityException: The source DbDataSync was not found on
computer ., but some or all event logs could not be searched.  Inaccessible logs: Security, State.
   at System.Diagnostics.EventLog.SourceExists(String source, ...)
   at DbDataSync.Cli.WindowsServiceEventLog.EnsureSourceRegistered() in ...\WindowsServiceEventLog.cs:line 45
   at DbDataSync.Cli.ServiceCommand.Install(String[] args) in ...\ServiceCommand.cs:line 137
```

Three things worth naming, because none of them were predicted:

- It throws at **`EventLog.SourceExists`**, not at `CreateEventSource`. The guard that exists precisely
  to make re-running `service install` safe is itself the call that cannot run unelevated — it has to
  search every log, including `Security`, to answer.
- `DbDataSync.Cli`'s `Program.cs` has **no top-level exception handler**, so the operator gets a raw
  .NET unhandled-exception stack trace and **exit code 127**, with nothing anywhere saying "run this
  elevated."
- Worst of the three: `GrantDataDirectoryAccess` runs **before** this and had already rewritten part of
  the data directory's ownership and ACLs. The command left the machine **half-modified** and then died.

**Fixed** by an explicit elevation refusal at the top of `ServiceCommand.Install`, before anything
touches the machine — mirroring what `ToolCommand`'s Unix path has always done (`Needs root. Run: sudo
…`). It now prints what is needed and why, changes nothing, and exits 1. Covered by
`ServiceCommandTests.Install_NotElevated_RefusesBeforeTouchingAnything`, which asserts both the message
and that the directory's owner is unchanged; it drives the new test-only `elevatedOverride` parameter,
since CI's `windows-latest` runners are elevated and the branch is otherwise unreachable there — the
same idiom `SystemdService.Install`'s own `executableOverride` already uses.

`WindowsElevation.IsAdministrator()` was extracted from `RealToolPathEnvironment`'s private copy, which
had the only implementation, so there is still one answer to "am I elevated" rather than two.

## Correction: authenticated `gh` does **not** close item 1

This doc previously said item 1 was "cheap to close once someone has authenticated `gh` access." That is
wrong, and was checked rather than assumed: with `gh` authenticated, the `dotnet-windows` job log for a
real run contains **zero** occurrences of any Event Log content — no written entry text, no `icacls`
output, nothing matching `DbDataSync started`. `dotnet test`'s console reporter names only failures and
skips, never what a passing test read back, and the tests printed nothing of their own.

## Update: the tests now report what they observed

The real fix was not to read harder but to make the tests say something. Both offenders asserted an
opaque boolean with a fixed sentence — `Assert.True(RecentEntryExists(marker), "the entry just written
was not found in the real Application log")` — which is useless in both directions: a failure said
nothing about what *was* in the log, and a pass showed nothing at all.

They now carry the observed state in the assertion message itself, and write what they scanned to
`ITestOutputHelper`:

- `WindowsServiceEventLogTests` reports how many `Application` entries it scanned, every entry it found
  from source `DbDataSync` (timestamp, type, first line), and the matched entry verbatim — then asserts
  the entry type separately, quoting the message if it is wrong. `EnsureSourceRegistered_CalledTwice…`
  no longer asserts merely "did not throw": it checks the source really exists afterwards and is bound
  to `Application`, reporting both. A silently no-op'd registration used to pass it.
- `ServiceCommandTests.GrantDataDirectoryAccess_LocalSystem…` now reports the owner **and every ACE**.
  That immediately paid for itself: the first failing run showed
  `NT AUTHORITY\SYSTEM Allow Modify, Synchronize` present as an explicit, non-inherited ACE while the
  owner was still `AD\dlshryoc` — i.e. `/grant` had succeeded and only `/setowner` needed elevation.
  The old message ("expected SYSTEM, got AD\dlshryoc") had been read all session as the whole operation
  failing. Those are exactly the two halves `GrantDataDirectoryAccess`'s own doc comment distinguishes.
- The elevation failure explains itself rather than surfacing a raw `SecurityException` from
  `FindSourceRegistration`: "This process is NOT elevated, and EventLog.SourceExists must search every
  log (including Security) to answer, which requires Administrator."

**What this closes, and what it does not.** A *failing* run is now fully inspectable, on CI as well as
locally — VSTest includes a failed test's captured output in the console at ordinary verbosity
(confirmed by running it). A *passing* run still shows nothing, because the console reporter does not
print output for passing tests. Closing that last piece needs either
`--logger "console;verbosity=detailed"` (which would flood a 1,400-test log) or `--logger trx` with the
`.trx` uploaded as an artifact — the same shape phase 144 already gave the `playwright` job for its JSON
report, and what phase 140's doc originally wished for. Not done here; it is a CI change, not a test one.

## Why the rest is worth closing, not just noting again

Item 2 is exactly the kind of gap a real operator could hit that CI structurally cannot catch — CI runs
elevated, and the reproduction needs a hand-installed, hand-broken service. Item 3 turned out to be
precisely that kind of gap too, and it was a live crash on the most ordinary mistake an operator can
make: forgetting to open the terminal as administrator.

## Update 2 — the trx landed, and reading it found two more things

The `.trx` upload went into `dotnet-windows` and the first artifact was retrieved and read. Two findings,
both from looking at the artifact rather than trusting the change.

**The first version of the trx step was wrong.** It passed
`--logger "trx;LogFileName=dotnet-windows.trx"`, and `dotnet test` on the solution runs each test project
separately — so every project overwrote the same file and the artifact held **207 results out of ~1,500**
(`State.Tests` alone, the last to finish). The mechanism had been verified on a single project and not on
the multi-project case that CI actually runs. Fixed by dropping `LogFileName`: the default name is
per-project and timestamped, so they coexist (checked by running two projects into one directory).

**What actually made `dotnet-windows` red for three consecutive runs.** Not the "No test matches" line on
`Drivers.Loader.Tests` — that was checked and exits 0 on its own. Buried mid-log:

```
[xUnit.net] [Test Class Cleanup Failure (DbDataSync.Api.Tests.ResyncTests)] System.IO.IOException
```

A fixture teardown throw. xUnit reports it as a *cleanup* failure, which sets the run's exit code **while
appearing in no project's pass/fail counts** — so every project printed `Passed!` and the job still went
red, with nothing in the summary to explain it. Correlation confirms it: the three red runs have exactly
one such line each, the green run has none.

The cause is `GitTempDirectory.DeleteRecursively`. It already handled the half of Windows deletion that
libgit2 causes (read-only object files → `UnauthorizedAccessException`), but not the other half: an
**open handle**, which throws `IOException` and which clearing attributes does nothing for. At the end of
a test that ran a real host there is always something still letting go — a pooled SQLite connection,
libgit2's pack files, a spawned worker on its way out. All six copies now retry for five seconds, and if
they still lose, throw naming the directory *and* the files another handle still holds — so the next
person reads a cause instead of an exception type.

This is the same lesson as the tests above, one level out: the failure existed, it was reported, and it
was unreadable. Three sessions' worth of red CI was attributed to a filter quirk that turned out to be
innocent.
