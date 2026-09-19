# A table mapping added to a running replication can full-load and never start syncing changes

**Status: open, raw thought — not yet designed.**

## The problem

Add a table mapping to a replication that's already running (rather than through a flow that
provisions the source first), and its first Primary tick can auto-trigger a full load — via phase 134's
`IPositionCapturing`/`RequestInitialLoad` path (`src/DbDataSync.State/LocalRunnerState.cs`,
`RunExecutor.cs` around the `RunKind == RunKind.BulkLoad && reader is IPositionCapturing` check) — for a
mapping whose source hasn't actually had change capture provisioned yet (`ProvisioningActions.
EnableSourceChangeCapture`, per-engine in `*Provisioner.cs`, not `ProvisioningState.Satisfied`).

The full load itself may complete. What doesn't happen afterward is ongoing sync: the reader has nothing
real to read a position from, so the mapping never starts picking up changes — quietly, since a
completed bulk load looks like success.

## What probably needs to happen

Before a Primary tick lets a first-ever pass auto-trigger a backfill/initial load for a mapping on a
position-capturing reader, check whether that mapping's source-side provisioning is actually satisfied
first. If it isn't, skip that mapping for this tick rather than running a load that can't be followed by
real change tracking — the same "let it try again next tick" posture phase 143 already uses for a losing
race (see the sibling doc,
`architecture/planning/todo/race-lost-runs-recorded-as-failed-should-not-be.md` — the outcome this
produces is the same shape of "not a failure, not worth alarming on" as the cases that doc is about, so
whatever comes out of that doc is probably also the right vehicle for this one).

## Open questions

- Where exactly does this check belong — `SchedulingEvaluator`/`SchedulerService` before dispatch (cheapest,
  avoids ever enqueuing doomed work), or inside `RunExecutor` right before the `RequestInitialLoad` call
  (closer to the actual decision, but after a run has already been claimed and started)?
- What counts as "provisioning satisfied" here, precisely — re-running the same per-engine
  `PlanEnableSourceChangeCaptureAsync` planning check that the Provisioning tab uses, presumably, but that
  check talks to the source (a real connection) — is that acceptable to do on every tick for every
  never-yet-successfully-run mapping, or does it need caching/backoff the same way `ChangePollingGate`
  gates other per-tick source I/O?
- Does this apply to every position-capturing reader kind, or are some (e.g. Postgres logical
  replication's slot-based reader) already self-guarding in a way that makes this moot for them
  specifically?
- Whether the operator gets any signal at all beyond "this mapping never ran" — silence forever isn't
  obviously better than a wrong full load, even if it's less actively harmful.
