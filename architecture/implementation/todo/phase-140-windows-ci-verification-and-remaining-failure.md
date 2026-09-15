# Phase 140 — Windows CI: verify this session's fixes, and the one still-unexplained failure

**Status**: Planned, not started.
**Plan reference**: none — logged directly from this session's own investigation of phase 136's new
`dotnet-windows` job (`.github/workflows/ci.yml`), the first real Windows CI run this repo has ever had.

## Why

Standing up `dotnet-windows` (a real `windows-latest` runner exercising every `[WindowsOnlyFact]`-gated
test, previously always `[SKIP]`) surfaced seven distinct, previously-invisible failures across three
CI jobs in one session — five fixed and pushed, two left genuinely open:

**Fixed this session, root-caused, and verifiable on Linux (all confirmed by reproducing the exact
failure locally before and after):**

1. `dotnet` job — `ChangeReaderFirstPassContractTests.EveryDeclaredProofNamesATestThatExists` threw
   `ReflectionTypeLoadException` for `Npgsql`. Not a phase 109h/i/j dependency-exclusion bug despite the
   error naming Npgsql — a filesystem-enumeration-order bug (the test's own file scan matched both
   `bin/` and MSBuild's `obj/` copy of a driver test assembly; `obj/` never gets copied dependencies).
   Fixed by requiring the real `bin/{Configuration}/{TFM}/` path shape.
2. `dotnet-windows` job (the dominant cause, ~100 of its failures) — libgit2 marks its object files
   read-only, and a plain `Directory.Delete(path, recursive: true)` refuses to remove those on Windows.
   Fixed across 28 files in 9 test projects (`GitTempDirectory.DeleteRecursively`, this repo's own
   existing pattern from `Cli.Tests`/`Certificates.Tests`, applied everywhere it was missing —
   including the two shared `WebApplicationFactory` base classes most of `Api.Tests` inherits from).
3. `dotnet-integration` job — `ConcurrentRunsIntegrationTests` failed with "Could not load file or
   assembly 'Microsoft.Data.SqlClient...'" for several of its 8 concurrently-spawned workers. A real,
   unguarded production race in `DriverConnectionFactory.EnsureLibraryInstalledAsync`'s check-then-
   install sequence — concurrent first-touch callers for the same not-yet-installed library all raced to
   install it simultaneously. Fixed with a semaphore + re-check; verified 8/8 real passes against the
   actual running MSSQL containers.

**Fixed this session, root-caused with high confidence, but *not* verifiable without a real Windows
box — this phase's first job is confirming these actually worked:**

4. `dotnet-windows` — `ConcurrentConfigReadTests.AReadDuringAWrite_NeverSeesAHalfWrittenFile` threw
   `AggregateException` ("being used by another process"). `ConfigRepository`'s reads used
   `File.ReadAllText` (default `FileShare.Read`, no `FileShare.Delete`), blocking `WriteAtomically`'s
   concurrent `File.Move(overwrite: true)` — `MoveFileEx` on Windows refuses to replace a file with any
   open handle lacking `FILE_SHARE_DELETE`; POSIX `rename()` has no such restriction, which is why this
   was invisible on Linux. Fixed with a `FileShare.ReadWrite | Delete` read helper across all 6 call
   sites in `ConfigRepository.cs`.
5. `dotnet-windows` — `NotesYamlRoundTripTests`' two round-trip tests failed with `Assert.Equal` string
   mismatches. The repo had **no `.gitattributes` at all**; GitHub's `windows-latest` runners default
   Git for Windows to `core.autocrlf=true`, so a multi-line C# raw string literal is checked out with
   `\r\n` there and `\n` everywhere else — the compiled literal differed by platform before any test
   logic ran. Fixed with `* text=auto eol=lf`.
6. `dotnet-windows` — `ApiFallbackTests.ADeepLink_IsAnsweredByTheAppRatherThanTheRouter` failed with a
   500: `NotSupportedException`, "Negotiate authentication requires a server that supports
   IConnectionItemsFeature like Kestrel." `AddNegotiate()` was registered on every Windows host
   regardless of `Auth:Disabled`; merely having it registered is enough for ASP.NET Core's real,
   Windows-native SSPI implementation to require a real Kestrel connection feature `TestServer` doesn't
   provide, even for a route nothing challenges for Negotiate. Fixed by also gating registration on
   `!AuthOptions.Disabled`.

**Found, investigated, and deliberately left unfixed — this phase's second job:**

7. `dotnet-windows` — `ConnectionsControllerTests.Upsert_NeverReturnsOrCommitsPlaintextPassword` threw a
   `NullReferenceException`. The test opens a *second*, freshly-constructed `LibGit2Sharp.Repository`
   against `_factory.RepoRoot` right after a `PUT` that triggers a commit via the API's own (separate,
   long-lived) `GitCommitService`, and indexes `repo.Head.Tip[$"config/connections/{name}.yaml"]` — the
   `TreeEntry` comes back null, so `.Target` throws. Ran 15/15 clean on this Linux sandbox, confirming
   it's genuinely environment-specific, not a portable race — but the exact Windows-side cause (most
   likely something about a second libgit2 `Repository` handle not seeing a just-written ref/tree
   promptly on Windows, possibly an I/O-flush timing gap between `GitCommitService.CommitChanges`
   returning and the on-disk state being fully visible to a different handle) was not pinned down. A
   speculative fix was deliberately not attempted: this test guards a real security property (a
   connection's password is never committed in plaintext), and a wrong "fix" risks masking a genuine
   correctness bug rather than an environmental one.

## What this phase will do

1. **Check the real CI outcome.** This session's fixes were pushed as five commits ending
   `92ea553` (`main`). Find that push's `dotnet-windows`/`dotnet`/`dotnet-integration` runs via `gh run
   list`/`gh run view` and confirm each of items 1–6 above actually passes for real now — not assumed
   from the local reasoning that produced the fix. Anything still failing needs its own look; don't
   assume a fix worked just because it was logically sound.
2. **Investigate item 7 for real, with actual Windows CI logs to work from** — this session had only
   one Windows run's log to go on (`gh run view --log`), no way to add a diagnostic and re-run, and no
   way to attach a debugger. With `dotnet-windows` now green (or closer to it) on other fronts, a
   deliberately-instrumented run (temporary `Console.WriteLine`s around the commit/reopen sequence,
   similar to how this session diagnosed the `EnsureLibraryInstalledAsync` race by adding diagnostics
   and reading real output) is affordable without ten other failures burying the signal.
3. **Decide whether `ConnectionsControllerTests`' own pattern (open a second `Repository` right after a
   write) is something other tests do too**, and whether the eventual fix belongs in the test (e.g.,
   don't open a second handle — resolve `GitCommitService`/`ConfigRepository` from the factory's own DI
   container instead) or in `GitCommitService`/`ConfigRepository` itself (e.g., an explicit flush, or a
   documented "don't do this" if a second handle right after a write is inherently unsafe on Windows and
   nothing should do it).

## Out of scope

- Phase 134's own two follow-up gaps (`architecture/implementation/README.md`'s 2026-09-15 note) — a
  handful of un-audited Docker-backed driver test files, and Bulk Load reader overrides for
  Change-Tracking-family readers now throwing — unrelated to Windows CI, already tracked there.
- Any other Windows-only behavior not already surfaced by a real CI run. This phase is about finishing
  what the first `dotnet-windows` run exposed, not a general Windows compatibility audit.

## Open questions to resolve during implementation

- Whether item 7's root cause turns out to affect any *other* place in the codebase that opens a second,
  independent `Repository` handle shortly after a write through the shared `GitCommitService` — if so,
  the fix should be systemic (in `GitCommitService`/`ConfigRepository`) rather than local to one test.
