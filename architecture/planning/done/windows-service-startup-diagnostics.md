# A Windows service startup failure is currently invisible

**Resolved 2026-09-14.** Turned into `architecture/implementation/todo/phase-136-windows-service-startup-diagnostics.md`
— a broader try/catch around startup, a `WindowsServiceEventLog` helper (registered at `service install`
time), and a startup-succeeded milestone. Linux/journald scoped out, carried forward as an assumption
worth a quick confirmation pass during implementation rather than independently re-verified here.

Found while diagnosing the Error 1053 report this session (see the companion doc on data directory
ownership for the actual bug that was failing). The diagnosis took an hour of back-and-forth and,
ultimately, a one-off Scheduled-Task hack to redirect the process's own stdout/stderr to a file — no
real operator should ever have to do that to find out why their service won't start. That gap is the
subject of this doc, independent of whatever specific bug happens to be causing a given failure.

## What we found, reproduced against a real failure

`Get-Service` confirmed the service was registered and `Stopped`. `sc.exe query`/`sc.exe qc` showed a
normal, correctly-registered service. Windows Event Viewer showed exactly two entries, both from
**Service Control Manager**: a generic "service failed to start" and a "30000 millisecond timeout was
reached" — neither naming any real cause. No `.NET Runtime` entry, no `Application Error` (1000), no
`7024`/`7034` process-termination event — nothing from the application itself, anywhere.

The real cause turned out to be a **caught** exception: `ServeCommand.RunAsync`
(`src/DbDataSync.Cli/ServeCommand.cs`) wraps exactly one call, `Prepare(root)`, in a try/catch:

```csharp
try
{
    Prepare(root);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2SharpException)
{
    Console.Error.WriteLine($"Could not prepare the config repository at '{root}': {ex.Message}");
    return 1;
}
```

`Console.Error` goes nowhere a Windows service can be observed writing to — services have no console.
So the one place `RunAsync` already tries to report a clear error does so somewhere structurally
invisible under the one execution mode (a real service) where a clear error matters most.

## Why `UseWindowsService()`'s own logging integration doesn't help here

`Microsoft.Extensions.Hosting.WindowsServices`'s `UseWindowsService()` does wire an EventLog logging
provider automatically — but only for `ILogger` calls made once `DbDataSyncHost.Build()`'s DI container
exists. `Prepare(root)` runs *before* `DbDataSyncHost.Build(...)` is ever called (`ServeCommand.cs`,
lines ~43-53) — there is no `ILogger`, no DI container, nothing yet at the point this specific failure
happens. A correctly-configured EventLog provider genuinely cannot help with this exact failure class;
it only helps for exceptions raised after the host exists.

## The wider gap

Only `Prepare()` has any error handling at all. Nothing wraps `EnsureDuckDbInstalledAsync` (which
already logs and swallows internally, so that one's fine), `DbDataSyncHost.Build(...)` itself, or the
startup phase of `app.RunAsync()` (a hosted service's `StartAsync` throwing, for instance). Any failure
in those propagates fully uncaught, with unverified behavior under a real service — we don't know from
this session's investigation whether an uncaught exception there produces an Event Log entry a real
operator could act on, only that the one failure we did reproduce (a caught one) produced nothing.

## Proposed shape

A dedicated, minimal startup-diagnostics path for `dbdatasync serve`, independent of the full ASP.NET
Core logging pipeline (since that pipeline may not exist yet when the relevant failure happens):

- Wrap meaningfully more of `RunAsync` than just `Prepare()` — ideally the whole method, `Build(...)`
  included — in one top-level handler.
- When `WindowsServiceHelpers.IsWindowsService()` is true, write any startup-time exception directly to
  the Windows Event Log via `System.Diagnostics.EventLog`, with the real exception type and message, in
  addition to the existing `Console.Error` path (which still matters for an interactive `serve` run).
- Decide where the Event Log source gets registered. `EventLog.CreateEventSource` needs elevated rights
  the first time a given source name is used — better to register it once, at `service install` time
  (which already runs elevated), than lazily on first failure under whatever account the service
  happens to be running as.
- Linux: systemd already captures a unit's stdout/stderr into the journal by default, so an uncaught
  exception under `dbdatasync serve` run via systemd likely already reaches `journalctl` — needs
  confirming, but if true this phase is Windows-only in scope.

## Open questions

- Should routine startup milestones (the `Console.WriteLine` lines already there — "DbDataSync is
  starting", the resolved paths, the console URL) also mirror to Event Log at Information level? Right
  now, under a service, there is no confirmation anywhere that startup *succeeded*, beyond SCM's own
  Running status. Leaning toward: yes, at least one "started successfully" line, without mirroring every
  routine line (avoid turning Application log into a duplicate console transcript).
- Does this fully cover a hosted service's own `StartAsync` throwing, or does that need separate
  handling closer to where hosted services are registered in `DbDataSyncHost.Build`? Needs checking
  what the Generic Host actually does with a hosted-service startup exception before assuming the
  top-level `RunAsync` wrapper alone catches it.
