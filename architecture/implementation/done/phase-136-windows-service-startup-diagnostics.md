# Phase 136 — Windows service startup failures reach the Event Log

**Status**: Complete.
**Plan reference**: `architecture/planning/done/windows-service-startup-diagnostics.md`.

## Why

Diagnosing the Error 1053 this session (phase 135's own bug) took an hour of back-and-forth and,
ultimately, a one-off Scheduled-Task hack to redirect the process's own stdout/stderr to a file, because
nothing DbDataSync does under a real Windows service is visible anywhere. `Get-Service`/`sc.exe` showed
a normally-registered, `Stopped` service; Event Viewer showed exactly two **Service Control Manager**
entries ("failed to start", "30000 millisecond timeout reached") and nothing else — no `.NET Runtime`
entry, no `Application Error`, nothing naming the real cause.

The real cause was a caught exception. `ServeCommand.RunAsync` wraps exactly one call, `Prepare(root)`:

```csharp
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2SharpException)
{
    Console.Error.WriteLine($"Could not prepare the config repository at '{root}': {ex.Message}");
    return 1;
}
```

`Console.Error` goes nowhere a service can be observed writing to. `UseWindowsService()` does wire an
EventLog logging provider automatically, but only for `ILogger` calls made once
`DbDataSyncHost.Build()`'s DI container exists — `Prepare(root)` runs before `Build(...)` is ever called,
so there is no `ILogger` yet at the point this specific failure happens; a correctly configured EventLog
provider genuinely can't help with this exact failure class. Nothing else in the startup path
(`Build(...)` itself, the startup phase of `app.RunAsync()`) has any handling at all — an exception
there propagates fully uncaught, with unverified behavior under a real service.

## Design

A dedicated, minimal startup-diagnostics path in `ServeCommand`, independent of the ASP.NET Core
logging pipeline, since that pipeline may not exist yet when the relevant failure happens.

### 1. Wrap the whole startup sequence, not just `Prepare()`

`RunAsync` restructured so everything from repo-root resolution through the moment `app.RunAsync()`
starts serving is inside one `try`. On any exception:

- `WindowsServiceHelpers.IsWindowsService()` true → write to the Windows Event Log (below) with the real
  exception type and message, then return a non-zero exit code.
- Otherwise (interactive `serve`, or Linux/systemd) → keep today's behavior: `Console.Error.WriteLine`
  and a non-zero exit code, unchanged from what an operator watching a terminal already sees.

The existing narrow `Prepare()` catch (`IOException`/`UnauthorizedAccessException`/`LibGit2SharpException`)
stays where it is — it already produces the specific, well-worded message this phase needs to actually
surface; the new outer handler is a second, broader net around the rest of startup (`Build(...)`, the
first moments of `RunAsync()`), for exceptions nothing catches today.

### 2. Writing to the Event Log

A small `[SupportedOSPlatform("windows")]` helper, e.g. `WindowsServiceEventLog.WriteError(Exception)`,
wrapping `System.Diagnostics.EventLog.WriteEntry` directly — not through `ILogger`, since the DI
container this failure predates doesn't exist yet. Source name `"DbDataSync"`, log `"Application"`,
matching the existing service name used everywhere else in `ServiceCommand`.

### 3. Registering the Event Log source

`EventLog.CreateEventSource` needs elevated rights the first time a given source name is used.
Registering it lazily, at the moment of a startup failure, risks failing *at exactly the moment it's
needed* if the service account doesn't have that right (LocalSystem does; a restricted named account
might not). Register it once instead, at `service install` time (`ServiceCommand.Install`), which
already runs elevated — a `SourceExists` check first, so re-running `service install` (upgrade, repair)
doesn't error on an already-registered source.

### 4. A startup-succeeded milestone

Right now, under a service, there is no confirmation anywhere that startup *completed* — only SCM's own
`Running` status, which says nothing about whether the app itself is healthy. Hook
`IHostApplicationLifetime.ApplicationStarted` (available once `DbDataSyncHost.Build()` has run) to write
one Information-level Event Log entry ("DbDataSync started — console at `<url>`") when running as a
service. Deliberately just the one milestone, not every routine `Console.WriteLine` line already in
`RunAsync` — avoid turning the Application log into a duplicate console transcript.

### Linux: out of scope

systemd captures a unit's stdout/stderr into the journal by default, so an uncaught exception under
`dbdatasync serve` run via systemd should already reach `journalctl` without any change here. Carried
over from the planning doc as a reasonable assumption based on systemd's standard behavior, not
independently re-verified in this phase — worth a quick confirmation pass during implementation before
leaning on it.

## Checkpoints

1. `WindowsServiceEventLog` helper: `WriteError(Exception)` and `WriteInformation(string)`, both
   `[SupportedOSPlatform("windows")]`, both no-ops (or throw clearly) if called off-Windows — matching
   this repo's existing `OperatingSystem.IsWindows()` guard convention elsewhere in `ServiceCommand`.
2. `ServiceCommand.Install` registers the `"DbDataSync"` Application event source (`SourceExists` guard)
   as part of installing the service — before the `sc.exe create` call, so a source-registration failure
   is visible at install time rather than silently deferred to the first real failure.
3. `ServeCommand.RunAsync` restructured per design item 1: the broader try/catch, routed to
   `WindowsServiceEventLog.WriteError` under `IsWindowsService()`, `Console.Error` otherwise — verify the
   existing `Prepare()`-specific catch and its message are unaffected (still the more specific message,
   just now also reaching the Event Log when running as a service).
4. `ApplicationStarted` milestone wired in `DbDataSyncHost.Build` (or `ServeCommand`, wherever
   `IHostApplicationLifetime` is easiest to reach after `Build()` returns) per design item 4.
5. Test coverage: a real, not mocked, verification that `WindowsServiceEventLog` actually writes an
   entry a subsequent read-back can see (this repo's established "real, not faked" precedent for OS
   interaction) — Windows-only test, matching how other Windows-specific pieces in
   `ServiceCommandTests`/`AdminCertificateServiceWindowsTests` are already gated.
6. Manual verification note in the retrospective: reproduce phase 135's original failure scenario (or
   any other forced startup exception) against a real installed service and confirm the real message now
   appears in Event Viewer under source `DbDataSync`, without the Scheduled-Task workaround this session
   needed.

## Retrospective

Built as designed, with two real signature refinements and one honest, still-open verification gap.

### What was built

- `src/DbDataSync.Cli/WindowsServiceEventLog.cs` (new) — `[SupportedOSPlatform("windows")]` static
  class: `EnsureSourceRegistered()`, `WriteError(Exception, string? message = null)`,
  `WriteInformation(string)`. Every member also guards with a runtime `OperatingSystem.IsWindows()`
  no-op check, matching this repo's existing convention (`CliOptions`, `CertCommand`, `ToolCommand`)
  rather than leaving off-Windows behavior to whatever the underlying package happens to do.
- `DbDataSync.Cli.csproj` gained `PackageReference System.Diagnostics.EventLog 10.0.0` — no existing
  usage anywhere in the repo before this phase; pinned to match this repo's existing
  `Microsoft.Extensions.Hosting.WindowsServices`/`.Systemd` version.
- `ServiceCommand.Install` calls `WindowsServiceEventLog.EnsureSourceRegistered()` (guarded, before the
  `sc.exe create` call), placed after phase 135's `GrantDataDirectoryAccess`.
- `ServeCommand.RunAsync` restructured per design item 1: the whole method body now sits inside one
  outer `try`/`catch (Exception ex)`, with the existing narrow `Prepare()` catch nested inside,
  unchanged in what it catches. A new `internal static Fail(Exception, string? message)` helper
  centralizes the dispatch: `WindowsServiceEventLog.WriteError` under `IsWindowsService()`,
  `Console.Error.WriteLine` otherwise — used by both the inner `Prepare()` catch (passing
  `PrepareFailureMessage`'s own richer text) and the new outer catch (passing `null`, falling back to
  `{ExceptionType}: {Message}`). `ApplicationStarted` milestone wired via
  `app.Lifetime.ApplicationStarted.Register(...)`, guarded to service-only (not merely
  "running on Windows") so an interactive Windows dev run of `dbdatasync serve` doesn't write Event Log
  noise on every start.

### Two real deviations from the design doc's literal wording

1. **`WriteError(Exception)` became `WriteError(Exception exception, string? message = null)`.** The
   design doc's own prose gives the reason before the signature does: *"[the Prepare() catch] already
   produces the specific, well-worded message this phase needs to actually surface."* A bare
   `WriteError(Exception)` writing only `{Type}: {Message}` would have discarded
   `PrepareFailureMessage`'s own extra line (the registered service's account/platform, phase 135) the
   moment it reached the Event Log — exactly the detail an operator debugging Error 1053 needs there
   most. The optional override lets the richer text through while the generic outer-catch case (nothing
   more specific to say) still falls back to the plain `Type: Message` form.
2. **The `ApplicationStarted` delegate needs its own inline `OperatingSystem.IsWindows()` guard**, even
   though it is registered from inside an already-guarded `if` block. The platform-compat analyzer's
   flow analysis does not extend into a closure that runs later (asynchronously, when the event
   actually fires) — a real CA1416 warning surfaced this during the build, not a hypothetical; fixed by
   guarding inside the lambda body too. The same pattern showed up again in
   `WindowsServiceEventLogTests`'s own private `RecentEntryExists` helper, which needed its own
   `[SupportedOSPlatform("windows")]` attribute even though every caller was already gated — a
   method-level attribute doesn't propagate from caller to callee any more than a runtime guard does.

### Test coverage

- `WindowsServiceEventLogTests` (new, `[WindowsOnlyFact]` + `[SupportedOSPlatform("windows")]`,
  matching phase 135's own `ServiceCommandTests` precedent exactly): `WriteError`/`WriteInformation`
  each write a real, uniquely-marked entry and read it back from the real `Application` log by scanning
  backward through `EventLog.Entries`' own indexer (not `Cast<>().Reverse()` — a real, long-lived
  machine's `Application` log can hold many thousands of entries, and this only ever needs the last
  few dozen); `EnsureSourceRegistered` called twice does not throw (the re-run/repair case `service
  install` needs to tolerate). All three report `[SKIP]` on this Linux sandbox, as expected — not faked,
  the same honest posture phase 135 already established for its own Windows-only test.
- `ServeCommand.Fail` — made `internal` (was going to stay `private`) specifically so its non-Windows-
  service dispatch branch could get **real, non-skipped** coverage on this sandbox: two new tests in
  `ServeCommandPrepareTests.cs` prove the exact `Console.Error` text for both the message-given and
  fallback-formatting cases. This is the one piece of this phase's actual logic (not just the Windows
  Event Log plumbing around it) that could be verified for real here, and it now is.
- Full `DbDataSync.Cli.Tests` suite: **138 passed, 4 skipped (all Windows-only), 0 failed** — up from
  137 before this phase (136 passed/1 skipped, phase 133a's count), a net +5 real tests (3 new
  Windows-only, 2 new cross-platform `Fail` tests). Full solution `dotnet build -c Release`: 0 warnings,
  0 errors.

### The systemd/journalctl "quick confirmation pass" the design doc asked for

Not independently verified by actually running a crashing systemd unit here — inspected instead:
`SystemdService.RenderUnit` never sets `StandardOutput`/`StandardError` in the unit it generates, so
systemd's own platform default (`journal`) applies untouched; nothing this repo's own unit file does
opts out of it. That confirms the assumption is architecturally sound (nothing here fights the
default), but it is inspection of the generator, not a real running unit's crash actually landing in
`journalctl` — the same class of gap as the Windows-only items below, named rather than silently
assumed closed.

### What's honestly still unverified

Everything that needs a real Windows box, which this session never had access to. Phase 140 later got a
real `dotnet-windows` CI run and confirmed Checkpoint 5's real EventLog round trip *ran and passed* (a
green `Test` step, not `[SKIP]`, on a real `windows-latest` runner) — but that's pass/fail from CI, not
the literal output read by a human, and Checkpoint 6's own manual scenario was never a test CI could run
at all. Both of those, plus the non-elevated-install question below, are written up together in
`architecture/planning/todo/follow-up-phase-136-140-windows-service-event-log-output-never-read-by-a-human.md`.

No real bugs were found in the *design* during implementation — the one thing this session caught (the
`ApplicationStarted` closure's CA1416 warning) was a build-tooling/analyzer detail, not a logic error,
and is now fixed and documented above rather than silently worked around.
