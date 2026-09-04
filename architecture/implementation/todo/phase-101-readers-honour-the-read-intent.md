# Phase 101 — the readers honour the intent, and an expired position holds the table

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/reset-a-mappings-watermark-from-the-ui.md`.
Second of three — 100 stores it, this makes it mean something, 102 puts it on screen.

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
