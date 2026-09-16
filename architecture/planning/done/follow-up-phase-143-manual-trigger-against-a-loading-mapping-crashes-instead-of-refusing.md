# A manual "Run Now" against a still-`Loading` mapping crashes with an unhelpful exception

**Status: fixed 2026-09-15.** See "Fix" at the end. Found writing phase 143's own new integration test
(`BulkLoadIntegrationTests.ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals`,
`architecture/implementation/todo/phase-143-initial-load-race-loses-cleanly.md`) — not part of that
phase's own scope, and not chased further there since it's a distinct, pre-existing gap unrelated to
phase 143's own fix.

## What happened

The test re-triggers a whole replication (`POST .../runs`, enqueuing one Primary pass per mapping) while
one of its two mappings (`map-2`) still had a Bulk Load genuinely in flight from its own earlier,
unrelated auto-triggered initial load. The re-triggered Primary pass for `map-2` failed with:

```
Value cannot be null. (Parameter 's')
```

— the exact signature of `long.Parse(s)` given a null `s`. `MsSqlChangeTrackingReader.ReadChangesAsync`'s
`Changes`-intent branch does `previous = long.Parse(previousWatermark!)` (the `!` asserting a promise the
caller did not keep here).

## Why this happens, precisely

`SchedulerService.FilterHeld` is the **deliberate, intentional** — not merely the only — enforcement
point for `ReadHold`, per its own doc comment: "A manual 'Run Now'
(`ProcessSupervisor.TriggerReplication`) is deliberately left alone: an operator explicitly asking is not
the automatic, unattended resubmission this phase exists to stop, and letting it through is one more way
to notice a mapping is still held." So a manual trigger bypassing the hold is **by design**, not the bug.

The bug is in what "letting it through" actually produces for a mapping in this specific state:

1. A mapping's first-ever pass calls `RequestInitialLoad`, which calls `ChangeWatermarkStore.
   SetPendingLoad` — an upsert that creates the mapping's `ChangeWatermarks` row (if none existed) with
   `ReadHold.Loading`, a `PendingWatermark`, and a `PendingBulkLoadBatchId`, but does **not** set
   `ReadIntent` — so the newly-created row's `ReadIntent` column takes the schema's own
   `NOT NULL DEFAULT 'Changes'`.
2. `RunExecutor` resolves a Primary pass's intent as `readState?.Intent ?? ReadIntentResolution.Default(...)`.
   Once step 1 has happened even once, `readState` is no longer null — it now has a real, stored
   `Intent = Changes` — so `ReadIntentResolution.Default`'s own `InitialLoad` fallback (for "nothing
   stored yet") never runs again for this mapping, even though nothing about it is actually ready for an
   incremental read: `Watermark` is still null (only `PendingWatermark` is set; promotion happens only on
   `BulkLoadState.Completed`).
3. A manual trigger enqueued while the mapping is still `Loading` — which `FilterHeld` deliberately does
   not stop — resolves to intent `Changes` (per step 2) against a still-null `Watermark`, and whichever
   reader's `Changes` branch assumes a non-null value (`MsSqlChangeTrackingReader` does; likely
   `MsSqlCdcReader` and others that store an integer/LSN-shaped watermark as a plain string do too) throws
   an unhandled, unhelpful exception instead of a recognizable failure.

## Why this is worth fixing, separately from phase 143

The doc comment's own stated intent — "letting it through is one more way to notice a mapping is still
held" — is undermined by the actual failure mode: an operator re-triggering a `Loading` mapping should
see something that says "still loading" (or, arguably, just repeat the capture-and-request step
harmlessly, the same as this mapping's very next *scheduled* pass will), not a bare `ArgumentNullException`
message with no connection to what's actually going on. This reads exactly like the kind of "mystery
failure" phase 101's own `PositionExpiredException`/`MetadataNotCachedException` treatment (a known cause
gets a known, named failure, not a stack trace) already exists to avoid — this state just wasn't one of
the cases considered when readers' `Changes` branches were written.

## Candidate directions (not evaluated)

- Readers' `Changes` branches could treat a null watermark as itself a signal — either a clear, typed
  exception (`ReadHold.Loading`-shaped, mirroring `PositionExpiredException`'s own "known cause, known
  fix" framing) or literally re-running the same capture-and-request `RequestInitialLoad` sequence a
  first-ever pass would, converging the same way a scheduled retry eventually would anyway.
- `RunExecutor` could check `readState?.Hold == ReadHold.Loading` before ever dispatching to a reader at
  all (Primary passes only — this must not affect the BulkLoad this hold is itself protecting), producing
  one consistent message regardless of which reader is configured, rather than depending on every reader's
  own `Changes` branch to handle a null watermark gracefully. This still respects `FilterHeld`'s own "an
  operator asking through is fine" design — it just makes what happens next legible instead of a crash.
- Worth checking which other readers' `Changes` branches share this same unhandled-null-watermark shape
  before picking a fix — this doc only confirms `MsSqlChangeTrackingReader`'s own, by direct reproduction.

## Fix

Took the second candidate direction: `RunExecutor.RunMappingAsync` now checks
`item.RunKind == RunKind.Primary && readState?.Hold == ReadHold.Loading` immediately after resolving
`readState`, before the intent-support check and before any connection opens, and throws the new
`MappingLoadingException` (`DbDataSync.Core.Config`) — one message regardless of which reader is
configured, rather than depending on every reader's own `Changes` branch to handle a null watermark. Not
the first direction (a per-reader null check): that would need auditing and fixing every reader's
`Changes` branch individually and would still miss a new one written later, where this check can't be
bypassed by construction.

`RunExecutor`'s outer catch gained a `catch (MappingLoadingException ex)` clause mirroring
`WorkQueueCollisionException`'s own — records `RunFailureKinds.MappingStillLoading` (new), same posture:
nothing for an operator to do, the run that already has `ReadHold.Loading` will clear it, and this
mapping's own next scheduled pass (or another manual trigger, once that happens) proceeds normally.

Verified against `BulkLoadIntegrationTests.ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_
TheLoserFailsCleanly_AndSelfHeals`, extended to cover exactly this: a manual re-trigger while map-2's own
unrelated Bulk Load is still genuinely `Loading` now asserts `Failed` / `MappingStillLoading` / an
error summary containing "still loading", instead of what that test used to have to route around (a
`WaitForLoadToCompleteAsync` call standing in for "don't hit the bug"). 4/4 clean local runs, plus the
full `BulkLoadIntegrationTests` class (8/8).
