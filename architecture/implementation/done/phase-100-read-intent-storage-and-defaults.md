# Phase 100 — read intent and hold: storage, defaults, and the API to set them

**Status**: Done.
**Plan reference**: `architecture/planning/done/reset-a-mappings-watermark-from-the-ui.md`.
First of three — 100 stores it, 101 makes the readers honour it, 102 puts it on screen.

## What this phase will build

**Nothing reads the intent when this phase ends.** That is deliberate and has a precedent: phase 90
built the metadata cache and left every consumer live-querying, and phase 91 switched them over as its
own reviewed change. The same seam applies here — a mapping's intent can be stored, defaulted,
resolved and set over the API, and `RunExecutor` goes on inferring a full load from a null watermark
exactly as it does today. That inference dies in phase 101.

### 1. Two enums, in `DbDataSync.Core.Config`

Beside the other small config enums, and in `Core` rather than `State` because config, state, the
runner and the drivers all need them and `State` already references `Core`.

```csharp
public enum ReadIntent { InitialLoad, Changes, ChangesFromEarliest, ChangesFromLatest }
public enum ReadHold   { None, PositionExpired, Paused }
```

`ReadIntent` is a *request* about the next pass — hence "intent", not "state": `ProvisioningState` is
an observation and the two must not read alike. `ReadHold`'s values are *reasons*, so another
known-cause failure can earn one later (`MetadataNotCached` is the obvious candidate and is **not** in
scope here).

### 2. State store: a migration on `ChangeWatermarks`

The table is already keyed `(TaskName, MappingName, SourceTable)`, which is the granularity an intent
needs. Three changes, one migration, in the shape `ALTER TABLE ChangeWatermarks {{addcolumn}}
WatermarkTimeUtc {{text}} NULL` already established:

- `ReadIntent {{text}} NOT NULL DEFAULT 'Changes'` — every existing row is a mapping that has run, and
  a mapping that has run is doing ordinary incremental reads. That is a true backfill of existing
  rows, not a guess.
- `ReadHold {{text}} NOT NULL DEFAULT 'None'`.
- **`Watermark` becomes nullable.** "`ChangesFromEarliest`, no position yet" has to be storable; today
  the column is `NOT NULL` and an intent without a position would be unrepresentable. Every read path
  has to cope, which is why it is here and not left to 101.

Store methods on `ChangeWatermarkStore`, surfaced through `IRunnerState`: read intent and hold with the
watermark (one row read, not three — the store's existing `Watermark`/`WatermarkTimeUtc` pairing
already makes that argument), and set each independently.

**Also the remote path.** `RemoteRunnerState`, `StateProtocol`, `StateJournal` and `JournalRecovery`
all need the new operations, or a cross-instance deployment silently keeps working while doing nothing.
`JournalRecoveryTests` is the test that notices.

### 3. Config: `DefaultReadIntent`, resolved

`ReadIntent?` on both `ReplicationTaskConfig` and `TableMappingConfig`. **Nullable at both levels** —
null on a mapping means inherit, null on the replication means nobody has said. Not symmetry for its
own sake: phase 46's `Enabled` bug is that a non-nullable value sitting at its default is omitted by
the YAML serializer, so an explicit choice equal to the default would not survive a round trip.

```csharp
public static ReadIntent Default(ReplicationTaskConfig? task, TableMappingConfig? mapping) =>
    mapping?.DefaultReadIntent ?? task?.DefaultReadIntent ?? ReadIntent.InitialLoad;
```

Mirrors `ProvisioningResolution` and `PipelineResolution`: each setting falls back independently.
Application default is `InitialLoad`, so nothing changes for anybody who configures nothing.

What the setting is *for*: setting it to either `Changes…` value means **the application never decides
on a full load by itself**. Every full load then traces to somebody choosing one — the configured
default, an explicit intent, or a resync.

### 4. API

Endpoints to read and set a mapping's intent and hold, on `TableMappingsController` beside
`refresh-metadata`, which is the closest precedent: a `POST` that changes stored operational state
rather than config.

- Setting an intent and clearing a hold are one request where the operator is recovering, so the shape
  should let both move in one call rather than leaving a window where a hold is cleared with the old
  intent still in place.
- **Refuse while a run holds the lock.** An intent cannot move under a pass that is acting on it.
- No validation against reader support yet — that needs the capability interface, which is 101's.

## How it will be verified

- A migration test: existing rows come back `Changes`/`None`, and a row can be written with a null
  watermark and a non-`Changes` intent, which the old schema could not represent.
- Resolution tests: mapping over replication over application default, each falling back
  independently, and an explicit value equal to the default surviving a YAML round trip — the phase 46
  regression, asserted rather than assumed.
- Store tests: intent, hold and watermark round-trip; setting one does not disturb the others.
- `JournalRecoveryTests` covering the new operations, and a cross-instance test asserting a remote
  state client sets an intent the local store then reports.
- API tests: set and read back; a set refused while the mapping's run lock is held.
- **A test asserting `RunExecutor`'s behaviour is unchanged by this phase** — a mapping with an intent
  stored still reads exactly as it did, because nothing consumes it yet. The phase-90 equivalent is
  what made that seam safe to review.
- Full suite green, `tsc -b`/SPA build clean.

## What this phase will not do

- **Not change any reader, or `RunExecutor`'s first-pass decision.** Phase 101.
- **Not declare or check reader support.** The capability interface is 101's, and so is the save-time
  validation that depends on it.
- **Not set a hold on `PositionExpired`**, and not stop a held mapping being scheduled — 101.
- **Not touch the SPA.** Phase 102.
- **Not extend `PauseEvents`.** Notes and history for holds are a follow-up, recorded in
  `planning/todo/pause-history-ui.md`, and the attractive shape there is one history over both grains.

## Open questions to resolve during implementation

- Whether the intent columns belong on `ChangeWatermarks` or in a table of their own. On it, because
  the key is identical and a second table means a second row lifecycle for one mapping — but the table
  is then no longer only about watermarks, and its name says otherwise. A rename is not worth a
  migration; a doc comment probably is.
- Whether `ReadHold` should be one column or a hold plus a reason. One, while the values are reasons
  and mutually exclusive. Revisit if a mapping can ever be held for two reasons at once.

---

## Outcome

Both open questions above were resolved as written during implementation: the columns stayed on
`ChangeWatermarks`, with the trade-off recorded in the migration's own comment rather than in a rename,
and `ReadHold` stayed one column.

### What was built

- **`ReadIntent`/`ReadHold`**, in `DbDataSync.Core.Config/ReadIntent.cs`, beside a `ReadIntentResolution`
  static class mirroring `ProvisioningResolution`'s shape exactly (`Default(task, mapping)` and
  `LevelOf(mapping)`).
- **The migration**, appended to `Migrations.cs`'s `Templates`: `ReadIntent {{text}} NOT NULL DEFAULT
  'Changes'`, `ReadHold {{text}} NOT NULL DEFAULT 'None'`, and `Watermark` made nullable by copying it
  through a temporary column rather than the drop-and-re-add phase 73 used for `StartedAtUtc` — that
  discarded the column's old values because they were already wrong; a stored watermark is exactly the
  position every reader depends on, so this migration keeps it.
- **`ChangeWatermarkStore`**: `MappingReadState` (intent, hold, watermark, watermark time — one row
  read), `GetReadState`, `SetReadIntent`, `SetReadHold` (each an independent upsert, touching only its
  own column so the others are left alone), and `SetReadIntentAndHold` for the one-call recovery case.
  `AppliedPosition.Watermark` became `string?` to match the now-nullable column, with `GetAppliedPosition`
  updated to read a null watermark rather than throwing.
- **The full remote path**: `GetReadState` (prerequisite), `SetReadIntent`/`SetReadHold` (outcomes) added
  to `IRunnerState`, `LocalRunnerState`, `RemoteRunnerState` (journalling on owner-lost, exactly like
  `SetWatermark`), `StateProtocol` (`SetReadIntentRequest`/`SetReadHoldRequest`/`ReadStateResponse`),
  `StateJournal` (`JournalOperation.SetReadIntent`/`SetReadHold`), `JournalRecovery` (replays both,
  skipping — and logging — an entry with no mapping name, the same posture `SetWatermark`'s recovery
  takes), and `RunnerStateEndpoints` (`GET /read-state`, `POST /set-read-intent`, `POST /set-read-hold`).
- **Config**: `ReadIntent? DefaultReadIntent` on both `ReplicationTaskConfig` and `TableMappingConfig`,
  nullable at both levels per the phase-46 reasoning in the plan doc.
- **API**: `GET`/`POST {mappingName}/read-state` on `TableMappingsController`, beside `refresh-metadata`.
  `GET` resolves a mapping with no stored row through `ReadIntentResolution.Default` rather than
  returning a bare null. `POST` sets intent and hold together through `SetReadIntentAndHold`, and refuses
  (409) while either a `Primary` or a `Backfill` `RunLock` is held for that mapping — checked *before*
  anything is read or written, so a refused call never partially applies. A `Verification` lock does
  **not** block it: that run kind never touches `ChangeWatermarks`, so there is nothing for it to race.

### How it was verified

- `ChangeWatermarkStoreTests`: the migration's backfill claim exercised through the mechanism that
  actually produces it (`SetWatermark`, which never mentions the new columns, reads back
  `Changes`/`None`); an intent stored with no watermark ever set; independent round-trips of intent and
  hold; four "leaves the others alone" tests (intent leaves hold+watermark, hold leaves intent+watermark,
  watermark leaves intent+hold, `SetReadIntentAndHold` moves both in one write).
- `ReadIntentResolutionTests` (new, `DbDataSync.Core.Tests`): mapping-over-task-over-default, each level
  independently, mirroring `ProvisioningResolutionTests`.
- `ReadIntentYamlRoundTripTests` (new): the phase-46 regression asserted directly — a replication or
  mapping explicitly choosing `InitialLoad` (the same value the *unset* case resolves to) still reloads
  with it set, and the field is omitted from the file only when nothing was chosen.
- `JournalRecoveryTests`: both new operations applied and idempotent under a repeated replay; an entry
  naming no mapping is skipped and logged, not guessed.
- `RunnerStateEndpointTests.The_remote_implementation_does_the_same_thing_as_the_local_one` extended:
  `RemoteRunnerState.SetReadIntent`/`SetReadHold` over the real loopback socket, read back through the
  owner's own `LocalRunnerState` — the cross-instance proof the phase doc asked for.
  `RemoteRunnerStateTests` also covers the owner-lost journalling path directly.
- `RunExecutorTests.ExecuteWorkerAsync_AStoredReadIntent_ChangesNothingAboutThisPass` (new): two
  identical mappings against the same unreachable connection, one with `ChangesFromEarliest` stored
  ahead of its first pass, one with nothing — both fail identically, neither writes a watermark, and the
  stored intent is exactly what was set before the run. The phase-90-style seam test the doc asked for.
- `MappingReadStateTests` (new, `DbDataSync.Api.Tests`): default resolution for a mapping with no row,
  the replication's configured default, set-then-get, the one-call intent+hold move, refusal while a
  `Primary` lock is held (and that nothing moved), refusal while a `Backfill` lock is held, allowed again
  once the lock releases, and not-found for a missing replication/mapping.
- `dotnet build` succeeded with zero warnings on every project in `src/` and `tests/`, including every
  project not otherwise touched by this phase (`Cli`, all four `Drivers.*`, `Scripting`, `Verification`,
  `Certificates`) — spot-checked individually.
- No SPA change was made or needed; `tsc`/the SPA build were not re-run for that reason.

### Environment limitations hit while verifying, confirmed pre-existing and unrelated

Beyond the expected `Category=Integration` connection-refused failures (no real SQL Server/PostgreSQL in
this sandbox), two further environment defects surfaced, both confirmed by reproducing them identically
on a byte-for-byte clean checkout of `origin/main` in a throwaway worktree — neither touches a file this
phase changed:

- **A Windows file-locking race in `Directory.Delete(..., recursive: true)`** during test teardown for
  every fixture that creates a fresh LibGit2Sharp repo per test (`RunExecutorTests`,
  `ReadIntentYamlRoundTripTests`, `NotesYamlRoundTripTests`, `DisablingRoundTripTests`,
  `ConfigRepositoryTests`, and others): `System.UnauthorizedAccessException` on a loose git object,
  thrown from `Dispose()` *after* the test body's own assertions have already passed. Confirmed on a
  clean checkout; xUnit still marks the test failed because `Dispose()` threw.
- **A Windows/Negotiate authentication regression that developed partway through this session**: every
  `DbDataSync.Api.Tests` test that sends a real HTTP request through the main app's `TestServer` (not
  just this phase's new `MappingReadStateTests`, but pre-existing files like `ResyncTests.cs` and
  `TableMappingsControllerTests.cs`, which passed cleanly earlier in this same session) now fails with
  `System.NotSupportedException: Negotiate authentication requires a server that supports
  IConnectionItemsFeature like Kestrel`. Reproduced identically on a clean `origin/main` checkout. Tests
  that never route through the main app's authentication pipeline (`JournalRecoveryTests`,
  `RunnerStateEndpointTests`, which talk to `StateHost`'s separate anonymous loopback listener) are
  unaffected and pass. `MappingReadStateTests.cs` is written, builds cleanly, and follows the same
  pattern as the equally-affected `ResyncTests.cs`; its logic could not be exercised end-to-end in this
  session because of this regression, which is a sandbox/domain-authentication fact rather than an
  application defect.

### Judgement calls

- **Which `RunKind`s the API's lock check refuses on.** The doc says "refuse while a run holds the
  lock" without naming which. Chose `Primary` and `Backfill` — the two kinds that read or could
  plausibly interact with a mapping's cursor — and deliberately excluded `Verification`, which never
  touches `ChangeWatermarks`.
- **A combined `SetReadIntentAndHold` store method**, beyond the two independent setters the phase doc
  asks for by name, so the API's one-call recovery is one atomic write rather than two sequential ones.
  Not surfaced through `IRunnerState`/the remote path — only the API (which owns the store directly)
  needs it; a runner only ever needs to set one or the other.
- **`GetReadState` added to `IRunnerState`,** not explicitly named in the phase doc's method list but
  implied by "surfaced through `IRunnerState`: read intent and hold with the watermark ... and set each
  independently" — read and set both being surfaced.
