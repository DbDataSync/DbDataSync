# `/request-initial-load`'s failure mode is only hardened for one exception type

**Status: diagnosed, partially fixed, general case not agreed.** Extracted from
`architecture/implementation/done/phase-134-initial-load-becomes-a-bulk-load.md`'s own "Known follow-up
/ not done here" section, where it sat unactioned — moved here per
`architecture/implementation/README.md`'s "Follow-up work gets its own doc, not a paragraph."

## The problem, as phase 134 left it

> `BulkLoadService.EnqueueForInitialLoadAsync`'s failure mode is not hardened. If it throws for a reason
> other than owner-unavailability (e.g. an `AutoSegment` in `DefaultSegmenting` failing to expand
> against an unreachable source), that currently surfaces as an unhandled exception from the
> `/request-initial-load` endpoint, which `RemoteRunnerState.IsUnreachable` would treat as a 5xx —
> meaning the runner could misread a genuine config/segment error as "the owner is gone" and start
> journalling.

## What's changed since

`architecture/implementation/done/phase-143-initial-load-race-loses-cleanly.md` built exactly the
mechanism this note describes needing — but scoped to one specific cause. That phase's own
`RequestInitialLoad` can now lose a `WorkQueue` race and throw `WorkQueueCollisionException`; the
endpoint catches *that one type* and returns a 409 with enough to reconstruct it client-side, so
`RemoteRunnerState` no longer misreads it as the owner being gone.

**Every other way `EnqueueForInitialLoadAsync` can throw is still unhardened.** The `AutoSegment`
expansion failure phase 134's own note named is a real example — `BulkLoadService.ExpandAsync` can throw
`InvalidOperationException` (an unreachable source, an undividable column type) from inside
`EnqueueForInitialLoadAsync`'s own call path, and that still propagates as an unhandled exception from
the endpoint today, unchanged by phase 143.

## Candidate directions, not evaluated

- **Generalize phase 143's own mechanism**: a broad `try/catch (Exception)` around the endpoint's call to
  `state.RequestInitialLoad(...)`, mapping anything that isn't already a `WorkQueueCollisionException` to
  a generic "the request failed for a reason unrelated to the owner's availability" 4xx (400, unlike the
  409 phase 143 uses, since these aren't a *collision*, they're the request itself being invalid) —
  simple, but loses the specific exception type on the runner's side unless every such cause also gets a
  reconstructable DTO the way `WorkQueueCollisionException` did.
- **Only the specific causes worth naming individually** (starting with the `AutoSegment` expansion
  failure phase 134 already named) get their own typed handling, mirroring phase 143's approach one cause
  at a time — more code, but each one can carry a `FailureKind` an operator's UI can act on, the same
  argument phase 143 made for `ConcurrentLoadInProgress`.
- Either way, this needs the same `RemoteRunnerState.RequestInitialLoad` treatment phase 143 built (not
  the generic `Required` helper) — worth checking whether that dedicated `SendRequestInitialLoad` method
  is the right place to extend, or whether a more general "this Prerequisite call can distinguish 4xx
  causes" helper is worth factoring out now that there are two reasons to need one.
