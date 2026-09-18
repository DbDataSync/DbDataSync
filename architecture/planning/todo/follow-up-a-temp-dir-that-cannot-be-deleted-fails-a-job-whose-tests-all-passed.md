# A temp dir that cannot be deleted turns an all-green test run into a red job

**Status: open, and it is currently the only thing failing `dotnet-windows` — on `main` as well as on
branches.** Observed on 2026-09-17/18. Filed per `architecture/implementation/README.md`'s "Follow-up
work gets its own doc, not a paragraph."

## What is wrong

`dotnet-windows` fails while every test project in it reports `Failed: 0`. The only failure line is a
class *cleanup* failure:

```
[xUnit.net] [Test Class Cleanup Failure (DbDataSync.Api.Tests.ReaderLagTests)] System.IO.IOException
...
Passed!  - Failed: 0, Passed: 463, ... - DbDataSync.Api.Tests.dll
Passed!  - Failed: 0, Passed: 207, ... - DbDataSync.State.Tests.dll
##[error]Process completed with exit code 1.
```

It is not branch-specific and not class-specific — the class named changes from run to run, which is the
clearest evidence that it is a race rather than a defect in any one test:

| run | branch | class named |
| --- | --- | --- |
| 35285818387 | `main` | `SchedulerServiceReconcileTests` |
| 35291308023 | `phases-145-038-034-035` | `ReaderLagTests` |

Both runs had every test passing. `dotnet-integration`, `dotnet`, `playwright` and `web` were green in
both.

The throw comes from `GitTempDirectory.DeleteRecursively`, via `AuthenticatedApiFactory.Dispose`. That
helper is already the *second* attempt at this problem — its own doc comment says so, describing "three
consecutive runs with nothing in the summary to explain it, and the one line naming the cause buried
mid-log". It now retries for five seconds and throws a message naming the files still holding the
directory open. That was a real improvement to diagnosis. **It did not make the job stop going red, and
for at least one cause it cannot.**

## The part that is confirmed, and the part that is not

**Confirmed, reproduced locally** (`LibraryInstallTests`, same helper, same stack):

```
Could not delete the temp directory 'C:\...\dbdatasync-auth-tests-pjgeujyv.qs3' after retrying for 5s.
Still open: ...\libraries\System.Data.SqlClient\lib\System.Data.SqlClient.dll.
Underlying error: Access to the path 'System.Data.SqlClient.dll' is denied.
```

The locked file is a **library assembly the test process loaded**. On Windows a loaded assembly's file
is held for as long as its load context lives, and a non-collectible context lives for the life of the
process. So no amount of retrying can win this one: the five-second budget is being spent waiting for
something that will not happen until the process exits. This is the `library install` / `LibraryRegistry`
path — exactly the mechanism phase 109c built, working as designed, with the test-side cleanup unaware
of it.

**Not confirmed:** whether the CI occurrences share that cause. The classes named there
(`ReaderLagTests`, `SchedulerServiceReconcileTests`) are not obviously library-loading tests, and the
run's log was overwritten by a re-run before the "Still open:" line could be read. It may be a second
holder entirely — a background service or file watcher still running as the fixture disposes — which
would be a genuine race that retrying *can* win, just not always within five seconds. **Whoever picks
this up should read the "Still open:" line from a fresh failing run first**; the helper already prints
it, and it decides which of the two problems this is.

## Why it is worth fixing rather than re-running

A re-run clears it, which is exactly what makes it expensive: it costs a full `dotnet-windows` cycle
(~14 minutes) each time, it trains everyone to re-run red jobs without reading them, and it means a real
Windows regression would arrive looking identical to the noise everyone has learned to dismiss.

Two directions, and they are not exclusive:

1. **Don't fail the run for an undeletable temp directory.** A leaked directory under the OS temp path is
   what that path is for, and the OS cleans it. Logging loudly and continuing turns a red job with 697
   passing tests into a green job with a warning, which is what the evidence actually supports. This is
   the cheap one, and it is correct for the loaded-assembly cause, where deletion is *impossible* rather
   than slow.
2. **Make the library load context collectible and unload it**, so the assembly's file is actually
   released. This is the real fix for the confirmed cause and has value beyond tests — a long-running
   service that installs a library today can never release its file either — but it is a change to
   `LibraryRegistry`'s design, not a test fix, and it deserves its own phase if anyone wants it.

**The recommendation is (1) now, and (2) only if the runtime behaviour is independently wanted.** Note
that (1) alone would have made both CI runs above green, honestly.

## How to verify when closed

- A run where a temp directory genuinely cannot be deleted does not fail the job, and says so in a way a
  human reads without opening the raw log.
- The "Still open:" diagnostic survives — whatever replaces the throw keeps naming the holder, since
  that line is the only reason this was diagnosable at all.
- `dotnet-windows` is green on `main` across several consecutive runs with no re-runs.
