# Phase 100 — read intent and hold: storage, defaults, and the API to set them

**Status**: Planned, not started.
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
