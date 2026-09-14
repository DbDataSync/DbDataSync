# Phase 136 — Windows service startup failures reach the Event Log

**Status**: Not started.
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

Not yet implemented.
