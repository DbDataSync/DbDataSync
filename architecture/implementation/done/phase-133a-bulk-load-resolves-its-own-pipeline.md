# Phase 133a — a BulkLoad run resolves BulkLoadConfig, not ChangeProcessingConfig

**Status**: Complete
**Plan reference**: none — a same-day bug fix to phase 133, found while researching phase 134, not a
planned add-on. Lettered rather than given its own number per `implementation/README.md`'s "add-on
work" rule: this is a small, discrete follow-up entirely *of* phase 133, not independent scope.

## What was wrong

Phase 133 added `BulkLoadConfig` and `PipelineResolution.BulkLoadReader/Cache/Writer` so a Bulk Load
run would default to its own pipeline (`BatchReload` reader, cache/writer inherited from
`ChangeProcessingConfig`) instead of silently reusing the mapping's incremental one. The plan said
plainly: "Backfill starts using it instead of the transient work-item Kind overrides." That part never
actually landed.

Two call sites still resolved a `RunKind.BulkLoad` item's *default* reader/cache/writer (i.e., whenever
no explicit per-work-item override was given, the common case) against `PipelineResolution.Reader/Cache/
Writer` — the **Change Processing** resolvers — rather than the new `BulkLoad*` ones:

- `BulkLoadService.EnqueueAsync`'s `ExpandAsync` (`PipelineResolution.ReaderKind(request.ReaderKind, ...)`),
  used to find a segment-expanding reader for an `Auto` segment.
- `RunExecutor.RunMappingAsync` (`PipelineResolution.Reader/Cache/Writer(task, mapping)`), used for
  *every* `RunKind`, including `BulkLoad`.

The practical effect: an operator triggering a bulk load with no reader/cache/writer override (the
normal case) got the mapping's **incremental** reader — e.g. `MsSqlChangeTracking` — asked to run under
`ReadIntent.InitialLoad` instead of `BatchReload`. For readers with no segment support, this silently
defeated Bulk Load's own segmenting (the exact class of defect the original planning doc's "symptom
that prompted this" section described, just relocated rather than fixed).

Found while researching phase 134 (RunExecutor is the file that phase touches next), not by any test —
none exercised the *unspecified*-Kind path for a BulkLoad item; existing tests only checked lane/locking
behaviour or an explicit override.

## What was fixed

- `RunExecutor.RunMappingAsync`: `effectiveReader`/`effectiveCache`/`effectiveWriter` now branch on
  `item.RunKind == RunKind.BulkLoad`, resolving via `PipelineResolution.BulkLoadReader/Cache/Writer`
  instead of the Change Processing equivalents. `Primary` is unaffected; `Verification`/`ReconcileDeletes`
  are unaffected in practice (they always carry an explicit `item.Kinds`, so the "effective" fallback was
  never actually reached for them — only its value in the diagnostic log line changes, now correctly
  naming the Bulk Load pipeline rather than Change Processing's).
- `BulkLoadService.EnqueueAsync`'s `ExpandAsync`: `PipelineResolution.ReaderKind` →
  `PipelineResolution.BulkLoadReaderKind`.

## How it was verified

A new test, `RunExecutorTests.ExecuteWorkerAsync_ABulkLoadWithNoOverride_ResolvesAgainstBulkLoadConfig_NotChangeProcessing`:
configures `ChangeProcessing.Reader.Kind = "MsSqlChangeTracking"` (a Kind the registered `MsSqlDriver`
offers) and `BulkLoad.Reader.Kind = "NoSuchReaderKind"` (one it does not), enqueues a `BulkLoad` item with
no `WorkItemKinds` override, and asserts the run fails with `"does not support reader kind
'NoSuchReaderKind'"` — the only way to see that error is if resolution actually consulted
`BulkLoadConfig`. Confirmed the test fails (differently — a connection error, proving the old
`ChangeProcessing`-resolved Kind was accepted by the driver) against the pre-fix code by temporarily
stashing the two-line fix and re-running it, then restored the fix and re-ran clean.

`dotnet build` (0 errors) and the full `DbDataSync.TaskRunner.Tests` project (14/14 passing, including
the new test) both verified locally before commit.

## Out of scope

Everything else about phase 134 — this is purely the missed wiring from 133, not a new behaviour.
