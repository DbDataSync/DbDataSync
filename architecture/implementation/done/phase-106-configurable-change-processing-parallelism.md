# Phase 106 — a replication's change-processing parallelism is configurable, and reaches the worker

**Status**: Done.
**Plan reference**: no `planning/` doc — this came out of a plan-mode session
(`~/.claude/plans/…`, not durable), transcribed below. It is a small, self-contained wiring change,
not a design that needed arguing first.

## Why

Change processing runs one `DbDataSync.TaskRunner` process per replication.
`RunExecutor.ExecuteWorkerAsync(taskName, degreeOfParallelism, ct)` drains that replication's work
queue with `degreeOfParallelism` concurrent consumers — the only real parallelism knob change
processing has. Before this phase:

- **`ProcessSupervisor.BuildStartInfo` never passed `--degree-of-parallelism`**, so every
  API-spawned worker ran at the runner's built-in default of 4, no matter how large the replication.
  There was no way to raise it short of running the runner by hand.
- **`ChangeProcessingConfig.ReaderConfig.Parallelism` / `WriterConfig.Parallelism`** existed in the
  config schema and the SPA's types since phase 1, were wired to nothing (`grep` confirms no
  production reader), and phase 4's own notes flagged them as unimplemented intent. Worse, the SPA
  had been *writing* `parallelism: 1` into every `task.yaml` it created — `1` is not `default(int)`,
  so the serializer's `OmitDefaults` kept it.

So: one real setting, `changeProcessing.degreeOfParallelism`, edited in the UI, persisted to
`task.yaml`, and passed to the worker on every spawn — replacing the two vestigial per-stage fields.

## What this phase built

### Config schema — `src/DbDataSync.Core/Config/ChangeProcessingConfig.cs`
- Removed `int Parallelism` from `ReaderConfig` and `WriterConfig`.
- Added `int DegreeOfParallelism` to `ChangeProcessingConfig`, `[DefaultValue(4)]` + initializer —
  the same omit-on-default mechanism `ReplicationTaskConfig.Enabled` documents. Unlike `Enabled`,
  an explicit 4 and an absent value mean exactly the same thing here, so a plain non-nullable int is
  correct (no nullable "nobody said" distinction to preserve).
- `public const int DefaultDegreeOfParallelism = 4`, shared with the API's fallback so a
  hand-launched runner and an API-launched one agree when neither is told otherwise. It matches
  `TaskRunnerOptions.DegreeOfParallelism`'s own default by construction, not by coincidence.

### YAML deserializer — `src/DbDataSync.Core/Config/YamlConfigSerializer.cs`
- Added `.IgnoreUnmatchedProperties()` to the `DeserializerBuilder`. Existing `task.yaml` files carry
  `reader.parallelism: 1` / `writer.parallelism: 1`; without this they would fail to load against a
  build that no longer models the key. The stale key is dropped on read and disappears on the next
  save. This is **not** the loosening `BatchReloadSegmentYamlConverter`'s comment warns against —
  that is specifically about a segment's discriminator, which still travels through its own
  hand-written converter.

### Validation — `ConfigValidation.ValidateChangeProcessing` + `ConfigRepository.SaveReplicationTask`
- Rejects `DegreeOfParallelism < 1` at save, in the style of `ValidateScheduling`. The runner's
  argument parser enforces the same floor on startup; this catches it before a worker is spawned.

### Wiring the worker — `src/DbDataSync.Api/Services/ProcessSupervisor.cs`
- `BuildStartInfo` gained an `int degreeOfParallelism` parameter and always appends
  `--degree-of-parallelism <n>`. It is not a secret (unlike the endpoint/token, which stay in the
  environment for the reason that comment gives), so it belongs on the command line where an operator
  debugging a worker can see it.
- `EnsureWorkerRunning` reads the value from the replication's config via a new
  `ResolveDegreeOfParallelism(taskName)` helper, which falls back to `DefaultDegreeOfParallelism`
  when the config file is missing — the worker it is about to spawn already exits `ConfigError` for
  that, and callers that care (`TriggerReplication`) load config first.

### UI — `src/DbDataSync.Web/src/pages/replication-detail/ScheduleCard.tsx`
- A `· up to [N] mapping(s) at once` number input on the Schedule card, committed by the same batched
  Save as the frequency / idle-timeout fields beside it. It lives on the Schedule card rather than
  the Pipeline tab because it is a fact about *how the replication runs* — the same kind of fact as
  how often and when — not about which reader/staging/writer the pipeline uses.
- `types.ts`, `ReplicationsPage.tsx` (new-replication default), and `querySource.ts` (reader-override
  seed) updated for the type change.

## How it was verified

- `dotnet build DbDataSync.slnx` — clean (129 `ReaderConfig`/`WriterConfig` construction sites, none
  referenced the removed field).
- `dotnet test` — Core (199), TaskRunner (63), Api (434) green.
- `npm run build` + `npm run lint` in `src/DbDataSync.Web` — clean.
- **New — `tests/DbDataSync.Core.Tests/ChangeProcessingParallelismTests.cs`**: a non-default value
  round-trips; the default is omitted from the file yet reloads as the default; raising then
  restoring the default leaves it unwritten; a hand-written `task.yaml` carrying the retired
  `parallelism:` key loads without throwing and comes back on the default; `< 1` is refused at save.
- **New — `StateOwnershipTests.The_configured_degree_of_parallelism_is_passed_as_an_argument`**:
  `BuildStartInfo` emits `--degree-of-parallelism` with the configured value.
- **`golden-path.spec.ts` test 04** now sets the input, saves, reloads, and asserts both the input
  value and `GET /api/replications/:name` → `changeProcessing.degreeOfParallelism`.
- ~10 Playwright fixtures had `parallelism: 1` removed from their `reader`/`writer` literals.
- Manual (not automated here — needs SQL Server containers): change the value in the UI, Save,
  confirm `task.yaml` gains `changeProcessing:\n  degreeOfParallelism: <n>` with no `parallelism:`
  under reader/writer, trigger a run, and confirm the spawned runner's `/proc/<pid>/cmdline` shows
  `--degree-of-parallelism <n>`.

## Decisions

- **Remove the dead fields rather than leave them.** They were never wired, semantically wrong for
  cross-mapping concurrency (there is one worker and one queue, not a per-stage thread pool), and
  keeping them would mean two vestigial knobs in the schema forever. The migration cost — old
  `task.yaml` files with `parallelism: 1` — is paid once, by `IgnoreUnmatchedProperties()`.
- **One value on `ChangeProcessingConfig`, not per stage.** A single mapping's reader/cache/writer
  run in sequence within one consumer, and `WorkQueueStore.TryClaimNext`'s `NOT EXISTS` self-join
  already guarantees a mapping never holds more than one consumer slot — so the only thing to tune is
  how many mappings run at once, which is one number for the replication.

## Out of scope

- Bounding one `WorkItem`'s size so a large backlog doesn't dominate the slots for an extended
  period — that is `architecture/planning/todo/change-queue-fairness-investigation.md`, unchanged by
  this phase.
- Per-mapping parallelism, or parallelism within a single mapping's read/write.
- A live view of how many consumers a running worker actually has.
