# The Event Log tests guard registration but not the write, and use the real machine log either way

**Status: open.** Recorded as a row in
[the CI flake catalogue](follow-up-ci-is-red-on-most-pushes-from-unrelated-flaky-tests.md) (run
`35495888790`, 2026-09-20), marked "partly" covered — the
[existing Event Log follow-up](follow-up-phase-136-140-windows-service-event-log-output-never-read-by-a-human.md)
is about the output never being *read* by a human, which is a different concern from this failure.
Investigated 2026-09-21.

## The symptom

`dotnet-windows` → `WindowsServiceEventLogTests.WriteError_ARealEntryIsReadableBackFromTheApplicationLog`:

```
Cannot open log for source 'DbDataSync'.  — Access is denied
```

## The guard has a hole exactly the shape of this failure

Every test in the class starts with `EnsureSourceRegisteredOrExplain()`, which does this and only this:

```csharp
try { WindowsServiceEventLog.EnsureSourceRegistered(); }
catch (SecurityException ex) { Assert.Fail("... Run this suite from an elevated prompt ..."); }
```

That is a good guard, and it is aimed one step too early. It catches a `SecurityException` from
**registration**. The observed failure is an access denial from the **write** that follows —
`WindowsServiceEventLog.WriteError(...)` — which is a different call, and "Cannot open log for source" is
not a `SecurityException`, so it sails past the `catch` and surfaces as a bare failure with none of the
elevation context the guard exists to supply. Whoever reads that CI log gets the symptom without the
sentence explaining it.

So the first fix is small and worth doing regardless of the cause: **guard the write the same way the
registration is guarded**, so a permissions failure reports as a permissions failure.

## What the cause probably is — and what to check first

Not asserted, because the run's detail was not captured and the class deliberately prints diagnostics that
would settle it. Two candidates, in order:

1. **Registration succeeded but had not taken effect yet.** Creating an Event Log source is not
   instantaneous from the writer's point of view; the first write can fail with exactly "Cannot open log
   for source X" until the Event Log service has picked it up. This fits a run where registration did not
   throw and the very next statement failed.
2. **The write path is not running with the privileges the registration path had** — the runner is
   `runneradmin`, so this is less likely, but it is cheap to rule out.

**Read the output the class already writes** (`ITestOutputHelper`: source exists, resolved log name, what
was scanned) from the next real occurrence before choosing. The guard's own message prints whether the
process is elevated, which distinguishes these two immediately — it just needs to be reachable from the
write path.

## The larger problem behind the specific one

These tests write to the **real machine Application log and read it back**. That is shared, machine-global
state on a CI runner and on every developer box, and it has consequences the specific failure only hints at:

- **They fail permanently for anyone not running elevated.** The guard's own advice is "Run this suite from
  an elevated prompt" — so on an ordinary dev machine these three tests are red on every single run, forever.
  That is a standing local failure people learn to scroll past, and learning to scroll past failures is how a
  real one gets missed. (Observed directly: on an unelevated Windows checkout these are three of the thirteen
  always-failing tests, alongside `ServiceCommandTests.GrantDataDirectoryAccess_LocalSystem_…`.)
- **`ScanDepth = 50` is a bet on the machine being quiet.** The comment says it is "wide enough that a
  parallel test writing its own entry in between cannot push ours out of range" — which is a statement about
  other *tests*, not about the machine. The real Application log is written by the whole OS.

Whether these should run unelevated at all is the decision worth making: gate them behind an explicit opt-in
(an environment variable the CI job sets) so they are *skipped* rather than *failed* where they cannot work,
and the suite's red means something everywhere. That is a change in what the suite claims, so it belongs to
whoever owns phase 136/140's testing intent rather than being done quietly here.

## How to verify when closed

- A permissions failure on the write reports with the same elevation context the registration failure does.
- On an unelevated machine these tests skip with a reason, or pass; they do not fail.
- The next real occurrence has the class's own diagnostic output attached to this doc, and the cause above is
  narrowed from two candidates to one.
