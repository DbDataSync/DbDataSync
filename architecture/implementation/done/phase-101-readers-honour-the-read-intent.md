# Phase 101 — the readers honour the intent, and an expired position holds the table

**Status**: Done.
**Plan reference**: `architecture/planning/done/reset-a-mappings-watermark-from-the-ui.md`, retargeted
partway through by `architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md` (see the
amendment note immediately below).
Second of three — 100 stores it, this makes it mean something, 102 puts it on screen.

## Amendment — §1 retargeted mid-implementation, 2026-09-04

The rest of this file is the **original** design, kept as written — per
`architecture/implementation/README.md`'s convention for a plan that changes materially during
implementation, this is a note on what changed and why, not a silent rewrite.

While this phase was mid-implementation (checkpointed as commit `f251379`, "WIP: Phase 101 in
progress"), `architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md` reached a
design decision that changes §1's scope: **an initial load stops being something a reader does at all**
and becomes its own future "Bulk Load" pipeline (unscheduled, not part of this phase — see that doc's
Phase B). That retarget lands here as follows, and nowhere else in this file:

- §1's table keeps its shape, but the `InitialLoad` column is **retargeted, not deleted**. Once a Bulk
  Load pipeline can perform an initial load, the answer to "can this reader do one" is unconditionally
  yes for every reader — a per-reader question with only one possible answer stops earning its place.
  So `InitialLoad` is no longer declared by any reader through `IReadIntentDeclaring`, and the interface's
  own doc says so. The declaration mechanism, the three genuinely per-reader intents
  (`Changes`/`ChangesFromEarliest`/`ChangesFromLatest`), and the UI/save-time-validation consumers of the
  declaration are all unaffected.
- The capability the old `InitialLoad` column actually protected — **whether a reader can report its
  current position without reading any row** — is what it becomes: a new interface,
  `IPositionCapturing` (`src/DbDataSync.Drivers.Abstractions/IPositionCapturing.cs`), in the same opt-in
  shape as `IPositionAcknowledging`/`ISegmentExpandingReader`. It is declared and correctly implemented
  by every reader with an honest answer (Change Tracking, CDC, Trigger Audit, Watermark) here, in this
  phase — but **nothing calls it yet**. The Bulk Load pipeline that will is out of scope for phase 101,
  exactly as the bulk-load doc's own "what changes for 99–102" table says: "finish §2 and §3 as written,
  and retarget §1's `InitialLoad` column to position capture."
- `RunExecutor`'s intent-refusal check (§3, unchanged in its own right) gained one exemption as a direct
  consequence: `ReadIntent.InitialLoad` is never refused, regardless of what a reader declares — because
  no reader declares it any more, a refusal check with no exemption would incorrectly refuse it for
  every mapping. This is the one line of §3's implementation the retarget actually touches.
- Every reader still performs its own full load on `InitialLoad` exactly as before (the
  `previousWatermark is null` branches this phase's original text describes are untouched). **Deleting
  those branches is explicitly not this phase** — it is the bulk-load doc's own Phase B, future and
  unscheduled. `ChangeReaderFirstPassContractTests` therefore evolves in the direction phase 99 and this
  phase's original §1 already anticipated (declaration-completeness), not in the inverted direction
  Phase B will eventually need (failing a reader that still full-loads).

Everything else below — §2 (CDC's inclusive floor) and §3 (`RunExecutor` reads the intent), and every
verification and open question that isn't about the `InitialLoad` column — stands as originally written
and was finished exactly as designed. See the Outcome section at the end for what was actually built,
how it was verified, and every judgement call made along the way.

## What this phase will build

This is the behaviour change, and the one to review carefully: after it, what a pass reads is decided
by a stored intent rather than inferred from a missing watermark. Phase 91 is the model — the
equivalent switch for the metadata cache, reviewed on its own once the thing to switch to reliably
existed.

### 1. Readers declare which intents they can honour

An opt-in capability, in the shape `ISegmentExpandingReader` and `IPositionAcknowledging` already use,
returning what the reader can do or saying it cannot. The declaration is not decoration: it is what the
UI offers, and what save-time validation checks.

| Reader | `InitialLoad` | `Changes` | `ChangesFromEarliest` | `ChangesFromLatest` |
| --- | --- | --- | --- | --- |
| Change Tracking | ✓ | ✓ | ✓ `CHANGE_TRACKING_MIN_VALID_VERSION` | ✓ `CHANGE_TRACKING_CURRENT_VERSION` |
| CDC | ✓ | ✓ | ✓ `min_lsn`, read **inclusively** | ✓ max LSN |
| Trigger audit | ✓ | ✓ | ✓ surviving `min(DS_Seq)` | ✓ `max(DS_Seq)` |
| Watermark | ✓ | ✓ | ✗ — identical to `InitialLoad`; the feed is the table | ✓ `max(col)`, stored without reading rows |
| Batch reload | ✓ | ✗ — it has no incremental mode | ✗ | ✗ |
| Scripted / DuckDB query | script's business | script's | script's | script's |

Two asymmetries the watermark framing hid and this table must keep:

- The **watermark reader** genuinely wants `ChangesFromLatest` — adopt a table as already-synced by
  storing `max(col)` without reading a row — while `ChangesFromEarliest` for it is a full load under
  another name, and offering it would be a button that lies.
- **Trigger audit's floor is what pruning left.** It implements `IPositionAcknowledging` and deletes
  `WHERE DS_Seq <= @throughSequence`, so the earliest has to be read, never assumed.

### 2. CDC reads its floor inclusively — the thing that made this design necessary

`MsSqlCdcStatement` opens its window at `sys.fn_cdc_increment_lsn(@storedLsn)`, so the stored LSN is
excluded. Under the rejected watermark-surgery design "start from the earliest available" was
**inexpressible**: storing `min_lsn` skips the change at `min_lsn`, and storing anything below it trips
the reader's own `Compare(storedLsn, minLsn) < 0` guard.

With an intent there is no sentinel to smuggle past the guard. `ChangesFromEarliest` reads from
`min_lsn` inclusively for that one pass, records the LSN it actually reached, and transitions to
`Changes`. The guard is untouched and no position below the floor is ever stored. **The inclusive read
is a distinct statement shape, not a decremented bound** — a decrement would put the problem back.

### 3. `RunExecutor` reads the intent instead of inferring

The lookup that today is

```csharp
var previousWatermark = item.RunKind == RunKind.Primary
    ? state.GetWatermark(task.Name, mapping.Name, watermarkKey)
    : null;
```

becomes a resolved intent — stored value, else `ReadIntentResolution.Default(task, mapping)` from phase
100 — passed to the reader alongside the watermark. A Backfill still gets neither; it does not use the
cursor and must not disturb it.

**Every intent transitions to `Changes` once a pass applies its changes**, written in the same place
and under the same conditions the watermark is: `Primary` only, after the target write commits, before
acknowledgement. A failed pass leaves the intent alone, for the reason it leaves the watermark — the
work is still to do.

**An intent the reader cannot honour is never quietly downgraded.** Save-time validation does not close
this — a mapping's reader can be changed afterwards, or a replication-level default set later — so the
run-time outcome is a loud failure or a `ReadHold`, never a silent fall back to `InitialLoad`. That
would be the application deciding on a full load by itself, at exactly the moment nobody is watching,
and it is the guarantee the whole design exists to give. Same posture as phase 91 refusing to fall back
to a live catalog query.

### 4. An expired position holds the table instead of retrying forever

Today `PositionExpiredException` completes the run `Failed` with `RunFailureKinds.PositionExpired` and
raises a notification — and **nothing stops**. There is no backoff, quarantine or consecutive-failure
logic anywhere in the codebase, so the next tick enqueues the mapping again, it fails again, and it
goes on every interval indefinitely: another failed run and another notification each time, burying
every other failure in that replication's history.

- On `PositionExpiredException`, set `ReadHold.PositionExpired`. The run still completes `Failed` with
  the same `FailureKind` — the pass did not happen, and a separate status would drop it out of every
  "how many failed" count, which is the reasoning already written at that catch site.
- **A held mapping is not dispatched.** Wherever work is enqueued per mapping, a hold skips it.
- **Notify once, on entering the hold**, not once per tick. This is a strict improvement that falls out
  of the change rather than being designed: a hold is a single event.

### 5. `ResyncService` sets an intent rather than deleting a row

`ClearWatermark` + `BatchReload` becomes `ReadIntent.InitialLoad` + clearing the hold — the same act
said out loud. Its `PositionExpired` gate becomes too narrow once intents exist and should be
reconsidered here: "reload this mapping" is a thing an operator can legitimately ask for at any time.

### 6. Documentation that this makes untrue

- **`detailed-design.md` §4.1** is rewritten: "no stored watermark means read the whole source table"
  becomes "the stored intent says what to do", with the old behaviour surviving as `InitialLoad`'s
  definition.
- **`ChangeReaderFirstPassContractTests` evolves rather than dies** — instead of classifying each reader
  as full-load-on-first-pass or exempt, it asserts every reader declares its supported intents and that
  the declaration is complete. The property it defends is unchanged: a new reader cannot be added
  without somebody deciding.

## How it will be verified

- **Per reader, per declared intent, against a real database** (`Category=Integration`): the row set
  each intent produces is the one the table above claims. Specifically, that `ChangesFromEarliest` on
  CDC returns the change at `min_lsn` — the one the rejected design silently dropped, and the single
  most important assertion in this phase.
- A test asserting an unsupported intent fails loudly and **does not** read the whole table — the
  guarantee, asserted rather than trusted.
- Intent transitions to `Changes` after a successful pass, and does not after a failed one.
- An expired position sets the hold, and a held mapping is not dispatched on the next tick — the
  "fails forever" behaviour, pinned so it cannot come back.
- One notification on entering the hold, not one per tick.
- The evolved contract test, and §4.1 rewritten to match.
- Full suite green, both categories.

## What this phase will not do

- **No SPA changes.** Phase 102 — until then a hold can only be set and cleared over the API, which is
  enough to test it and not enough to ship it.
- **No new `ReadHold` reasons.** `MetadataNotCached` is the obvious second candidate and stays out.
- **No `PauseEvents` extension** — history and notes are a follow-up, see
  `planning/todo/pause-history-ui.md`.

## Open questions to resolve during implementation

- **Does a hold block a Backfill, or only Primary passes?** A backfill does not use the cursor and could
  legitimately be how somebody recovers — but "stopped" that still runs work is a contradiction an
  operator should not have to hold in their head.
- **Where the dispatch check goes** — the scheduler, the work-queue enqueue, or the runner claiming an
  item. Lowest is safest; highest avoids queue rows nobody will ever run.
- Whether an unsupported intent should fail the run or set a hold. A hold is more useful and is a
  second way to enter one; failing is simpler and louder. Probably the hold, with the reason naming the
  intent and the reader.

---

## Outcome

All three open questions above were resolved during implementation, and are recorded under Judgement
calls below along with the §1 retarget's own decisions.

### What was built

**§1, retargeted (see the amendment note at the top of this file):**

- `IPositionCapturing` (`src/DbDataSync.Drivers.Abstractions/IPositionCapturing.cs`) — a `Task<CapturedPosition>
  CapturePositionAsync(sourceConnection, source, options, cancellationToken)` method and a
  `CapturedPosition(string Position, DateTimeOffset? PositionTimeUtc)` record, opt-in in the same shape
  as `IPositionAcknowledging`. Implemented by `MsSqlChangeTrackingReader` (wraps the already-public
  `GetCurrentVersionAsync`), `MsSqlCdcReader` (wraps `MsSqlCdcCatalog.GetMaxLsnAsync`), `TriggerAuditReader`
  (wraps its own max-sequence query; `PositionTimeUtc` is always null — no engine mapping from a sequence
  to a time exists for this mechanism), and `WatermarkReader` (wraps `WatermarkStatement.BuildMaxWatermark`,
  requiring the same `watermarkColumn` option every other call on it does). Not implemented by
  `BatchReloadReader`/`MsSqlBatchReloadReader` (no position of their own, only an echoed-back watermark),
  `DuckDbQueryReader`, or `ScriptedQueryReader` (a script decides what its own watermark means).
- `InitialLoad` removed from every reader's `SupportedIntents`: `MsSqlChangeTrackingReader`, `MsSqlCdcReader`,
  `TriggerAuditReader` now declare exactly `{Changes, ChangesFromEarliest, ChangesFromLatest}`;
  `WatermarkReader` declares `{Changes, ChangesFromLatest}` (unchanged — it never declared
  `ChangesFromEarliest`). `BatchReloadReader`, `MsSqlBatchReloadReader` and `DuckDbQueryReader` stop
  implementing `IReadIntentDeclaring` entirely (their only checkmark was `InitialLoad`, which is no longer
  part of the declared vocabulary) — `ScriptedQueryReader` already did not implement it.
- `RunExecutor.RunMappingAsync`'s refusal check gained `intent != ReadIntent.InitialLoad` ahead of the
  `IReadIntentDeclaring` check, so `InitialLoad` is exempt from refusal for every reader, declaring or not.
- `ChangeReaderFirstPassContractTests` (`DbDataSync.Api.Tests`) rewritten from "classify each reader as
  full-load-on-first-pass or exempt" to "classify each reader as declaring (with a non-empty,
  `InitialLoad`-free set, proven against a real database by a named test) or exempt (with nothing honest
  to declare, and provably not implementing the interface)". The property defended — a new reader cannot
  be added without somebody deciding — is unchanged; only what is being decided about changed.
- New `PositionCapturingContractTests` (`DbDataSync.Api.Tests`): the roster of who implements
  `IPositionCapturing` and who does not, reflection-only, no database needed.
- `detailed-design.md` §4.1 rewritten: "no stored watermark means read the whole source table" becomes
  "the stored intent says what to do", `InitialLoad`'s old per-reader table collapses to a single row
  (every reader's own `InitialLoad` behaviour, unchanged), and the section gained a paragraph on
  `IPositionCapturing` and on the dispatch-skip/hold-does-not-block-Backfill rules below.
- `ReadIntent.InitialLoad`'s own doc comment and the enum's outer doc comment updated: no longer "nothing
  reads this yet" (phase 100's own words, now false), and `InitialLoad` documented as meaning "run the
  Bulk Load pipeline" once one exists, with today's per-reader full-load branches named as the interim
  behaviour.

**§2 (CDC's inclusive floor) — verified as already complete in the WIP, tests added:**

- `MsSqlCdcReaderTests.ChangesFromEarliest_ReturnsTheChangeAtTheFloor_InclusiveOfMinLsn` — the single most
  important assertion in this phase. Two rows, then `sys.sp_cdc_cleanup_change_table` prunes the first and
  advances the capture instance's own `min_lsn` to sit exactly at the second row's LSN (the real mechanism
  CDC's own retention cleanup uses, not a simulation of one). `ChangesFromEarliest` returns that second row;
  the same LSN read via the *rejected* design — stored as an ordinary watermark and read with `Changes` —
  returns nothing, reproducing the exact bug this phase exists to close.
- `MsSqlCdcReaderTests.ChangesFromLatest_AdoptsTheCurrentMaxLsn_WithoutReadingAnyRow` and the equivalent
  `CapturePositionAsync` test.
- The analogous pair for Trigger Audit (`ChangesFromEarliest_ReturnsTheRowAtTheSurvivingFloor_InclusiveOfPruning`,
  which prunes via `AcknowledgeAsync` rather than CDC's cleanup proc, and `ChangesFromLatest_...`), for
  Change Tracking (`ChangesFromEarliest_ReadsEverythingTheFeedStillHolds`, `ChangesFromLatest_...` — no
  inclusive-boundary bug to reproduce there, since `CHANGE_TRACKING_MIN_VALID_VERSION` is already a valid
  `@previousVersion` by the engine's own contract), and for Watermark (`ChangesFromLatest_...` only —
  `ChangesFromEarliest` is undeclared for this reader). `CapturePositionAsync` tests for all four.

**§3 (`RunExecutor` reads the intent) — verified as already complete in the WIP, one gap closed:**

- The InitialLoad-refusal exemption above.
- `RunExecutorTests.ExecuteWorkerAsync_AnUndeclaredIntent_IsRefused_BeforeAnyConnectionIsOpened` (new):
  the Watermark reader, asked for its undeclared `ChangesFromEarliest`, is refused with a message naming
  the intent and the reader — and, checked directly, never gets as far as opening the (deliberately
  unreachable) connection at all.
- `RunExecutorTests.ExecuteWorkerAsync_InitialLoad_IsNeverRefused_EvenWhenUndeclared` (new): the same
  Watermark reader, asked for `InitialLoad` (which it also does not declare), is *not* refused — it
  reaches and fails on the unreachable connection instead, proving the exemption is real rather than an
  accident of this reader happening to support it.
- `ExecuteWorkerAsync_AStoredReadIntent_ChangesNothingAboutThisPass` renamed to
  `..._AStoredSupportedReadIntent_FailsIdenticallyToNoStoredIntent` and re-pointed: its premise ("nothing
  reads the intent yet") stopped being true on purpose, so it now proves the narrower thing that remains
  true — a *supported, declared* intent causes no different behaviour from no stored intent at all, both
  reaching the same unreachable connection rather than being bounced by a refusal.
- `PrimaryPassOutcomeTests` (new — none existed before this phase, despite `PrimaryPassOutcome.cs` itself
  being WIP-complete): every starting intent (`InitialLoad`, `Changes`, `ChangesFromEarliest`,
  `ChangesFromLatest`) transitions to `Changes` on a successful pass; a pass that read nothing changes
  neither the watermark nor the intent; the watermark time is recorded alongside the position.

**§4 (an expired position holds the table) — the two genuinely new mechanisms:**

- `SchedulerService.FilterHeld`/`ResolveHold`: every mapping a tick finds due for a scheduled `Primary`
  pass is checked against its stored `ReadHold` before the change-polling gate ever sees it (cheaper: a
  held mapping costs no source round-trip either). A mapping this cannot resolve (config gone, no
  dialect, more than one source) is dispatched unheld, matching `ChangePollingGate`'s own fail-open
  posture for the reason it gives.
- `ResyncService` rewritten: `ClearWatermark` + forcing a `BatchReload` becomes
  `SetReadIntentAndHold(InitialLoad, None)` followed by an ordinary `Primary` enqueue (not a `Backfill` —
  `InitialLoad` is a read intent any `Primary` pass can resolve, which is the entire point of having one
  rather than a special-cased reload path) and `ProcessSupervisor.EnsureWorkerRunning`. Its gate widened
  from "only `RunFailureKinds.PositionExpired`" to unconditional — any run identifies which mapping to
  reload, and nothing about that run's own outcome gates the request.
- `SchedulerServiceHoldTests` (new, `DbDataSync.Api.Tests`): a held mapping is not dispatched on the next
  tick (`AHeldMapping_IsNotDispatchedOnTheNextTick`); one notification survives three ticks, not three
  (`AHeldMapping_IsNotifiedAboutOnce_NotOncePerTick` — proving the "notify once" guarantee falls out of
  the dispatch filter, exactly as predicted, rather than needing logic of its own); a held mapping's
  Backfill still enqueues (`AHeldMappings_BackfillStillEnqueues`); and a dependency-resolution smoke test
  unaffected by the environment issue below.
- `ResyncTests` updated to match: `Resync_SetsInitialLoadAndClearsTheHold` replaces the old
  watermark-clearing assertion, and `AnOrdinaryFailure_IsStillResyncable` replaces the old
  `AnOrdinaryFailure_IsNotResyncable` (inverted on purpose — the gate widened).

### How it was verified

- `dotnet build` clean (0 warnings, 0 errors) on every project in `src/` and `tests/` touched directly or
  transitively — spot-checked individually, including every driver/test project that merely calls
  `IChangeReader.ReadChangesAsync` and needed the new `intent` parameter threaded through (see Judgement
  calls: this WIP left ~13 pre-existing test files across `DbDataSync.Drivers.MsSql.Tests` and
  `DbDataSync.Drivers.Postgres.Tests`, plus `DriverCapabilityOptInTests` and `DuckDbQueryReaderTests`, not
  compiling at all).
- Every non-`Category=Integration`, non-`TestApiFactory`-HTTP test this phase touched or added passes:
  `PrimaryPassOutcomeTests` (7/7), `ChangeReaderFirstPassContractTests` (5/5),
  `PositionCapturingContractTests` (13/13), the two new `RunExecutorTests` refusal/exemption tests and the
  retargeted supported-intent test (assertions pass; see the Windows Dispose flake below),
  `SchedulerServiceHoldTests.BuildScheduler_ResolvesEveryDependency` (proves the new constructor
  dependencies — `ChangeWatermarkStore`, `DriverRegistry` — resolve from the real DI container,
  independent of the HTTP issue below).
- Every `Category=Integration` test this phase added (CDC's inclusive-floor test and its siblings, the
  Trigger Audit/Change Tracking/Watermark `ChangesFromEarliest`/`ChangesFromLatest`/`CapturePositionAsync`
  tests) fails with a connection-refused/timeout against a real SQL Server or Postgres — confirmed
  pre-existing (no database reachable in this sandbox), same failure mode as every pre-existing
  Integration test in the same run. Written and reviewed for correctness against real engine semantics
  (`sys.sp_cdc_cleanup_change_table`'s documented low-water-mark behaviour, in particular) but not
  actually run end-to-end.
- `SchedulerServiceHoldTests`' three behavioural tests (dispatch skip, notify-once, Backfill-still-runs)
  could not be exercised end-to-end: their setup uses `TestApiFactory`'s HTTP client to write config,
  which hits the pre-existing `NotSupportedException: Negotiate authentication requires a server that
  supports IConnectionItemsFeature like Kestrel` this sandbox has (confirmed by reproducing it against
  `ChangePollingGateTests`, an untouched, pre-existing file using the identical setup pattern). Written
  correctly per the same convention every other `TestApiFactory`-based test in this codebase already
  uses; the dependency-resolution smoke test above at least confirms the DI wiring half is sound.
- `RunExecutorTests` and other tests constructing a fresh LibGit2Sharp repo per test hit the same
  pre-existing Windows `Directory.Delete` file-lock flake phase 100's own retrospective already documented
  (`System.UnauthorizedAccessException` thrown from `Dispose()`, after the test body's own assertions have
  already passed) — reproduced identically for tests this phase did not touch (`ConfigRepositoryTests`, by
  stashing this phase's changes and re-running against the bare WIP commit).
- No SPA change was made or needed; phase 102's territory.

### Judgement calls

- **The dispatch check lives in `SchedulerService`, the highest of the three places the original doc
  named** (scheduler, work-queue enqueue, runner claiming an item). It is the one place that stops a held
  mapping from ever becoming a `WorkQueue` row or a `TaskRuns` history entry nobody was going to let run —
  the same reasoning `ChangePollingGate` already applies one line below it, for a quiet source instead of
  a held one. A manual "Run Now" (`ProcessSupervisor.TriggerReplication`) is deliberately left unfiltered:
  an operator explicitly asking is not the automatic, unattended resubmission this phase exists to stop,
  and it is one more way to notice a mapping is still held.
- **A hold blocks a scheduled `Primary` pass only, never a `Backfill`.** `SchedulerService.FilterHeld` only
  ever runs over mappings due for a scheduled Primary pass; `BackfillService`'s own enqueue path is a
  separate method that never consults `ReadHold` at all, so this needed no new code, only a test proving
  the two paths are actually independent.
- **An unsupported intent sets a hold, not a bare failure — but not a new one.** The mechanism already
  existed for `PositionExpiredException`; a mapping refused for an undeclared intent fails the run the
  same way an unsupported combination always has (a loud `InvalidOperationException`, `RunStatus.Failed`,
  no `FailureKind`), which an operator fixes by changing the mapping's reader or its stored intent — not
  by a resync, so folding it into `ReadHold.PositionExpired` would point at the wrong recovery. No new
  `ReadHold` value was added for it, matching this phase's own "no new `ReadHold` reasons" boundary.
- **`ResyncService` no longer routes through `BackfillService` at all.** The original design ("clears the
  watermark and queues a `BatchReload` over a full segment") predates the retarget; once `InitialLoad` is
  a read intent any `Primary` pass resolves, constructing a `BackfillRequest` for a reload that the next
  ordinary pass will now perform itself is a needless second path. `ResyncService`'s dependencies changed
  from `BackfillService` to `WorkQueueStore`/`ProcessSupervisor` directly.
- **The pre-existing `ReadChangesAsync` call sites needed for the WIP's own signature change** (an `intent`
  parameter inserted where none existed before) were fixed as a compile-only mechanical pass: a literal
  `null` previous-watermark became `ReadIntent.InitialLoad`, anything else became `ReadIntent.Changes`,
  and a reader that ignores `intent` entirely (`BatchReloadReader`/`MsSqlBatchReloadReader`/
  `DuckDbQueryReader`/`ScriptedQueryReader`) always got `InitialLoad`. No pre-existing test's assertions,
  scenario, or behaviour were changed — only the missing argument was added.
- **`IPositionCapturing` as the interface name**, over alternatives like `IPositionReporting`: "capturing"
  matches the bulk-load doc's own verb for the operation ("capture the source position → run the Bulk
  Load → ...") and reads as an action a caller performs, which is what the eventual Bulk Load pipeline
  will do with it — "reporting" reads more like a passive property.
