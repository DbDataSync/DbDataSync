# An explicit read intent per mapping, and a hold that stops a broken table replicating

**Status: raised 2026-09-03; redefined 2026-09-04 around an explicit state rather than watermark
surgery, and widened the same day to cover what an expired position should do. Not yet agreed.**

**The driving requirement: process every change the source's feed still holds, without initiating a
full load.**

The first shape of this was "let an operator set the watermark to the earliest/latest detectable
position". That is now rejected in favour of a different mechanism: **store what the next pass is meant
to do, and let each reader interpret it**, instead of inferring it from the absence of a watermark.

## Why the redefinition is the better design

**It removes an inference from absence.** Today a null watermark means "read the whole table" — see
`detailed-design.md` §4.1, which this supersedes. That is exactly the class of bug this repo has
already fought twice: phase 90's rule that "an empty incoming list never clears a populated cache",
and phase 95's `SideRead.IsMissingTable`, which exists because "could not read" and "there is nothing
there" had been collapsed into one empty list. A missing value is not an instruction, and reading it
as one leaves no way to express any *other* instruction.

**It dissolves the CDC problem outright**, which is the concrete payoff. Under watermark surgery,
"start from the earliest available" was **not expressible for CDC**: `MsSqlCdcStatement` opens its
window at `sys.fn_cdc_increment_lsn(@storedLsn)`, so storing `min_lsn` silently skips the change at
`min_lsn`, and storing anything below it trips `MsSqlCdcReader`'s own guard
(`Compare(storedLsn, minLsn) < 0` → `PositionExpiredException`). The two constraints contradicted each
other and the least-bad option was to lose one change quietly.

With an intent there is no sentinel to smuggle past a guard. The state says *read from the floor*, the
reader reads `min_lsn` **inclusively** for that one pass, records the real LSN it reached, and
transitions to ordinary incremental. The guard is untouched, because no position below the floor is
ever stored.

## The states

Named `ReadIntent`, in the shape of the codebase's other small enums (`RunKind`, `ProvisioningState`,
`ScheduleMode`). "Intent" rather than "state" deliberately: it is a *request* about the next pass that
the reader interprets as best it can, not an observation of how things are — which is what
`ProvisioningState` is, and the two should not read alike. (`ReadState` is the runner-up; avoid
`ChangeProcessingState`, which invites confusion with `ChangeProcessingConfig`, an unrelated thing.)

| Value | Means | After the pass |
| --- | --- | --- |
| `InitialLoad` | Read the source table itself. Today's null-watermark behaviour, now stated. | → `Changes` |
| `Changes` | Ordinary incremental read from the stored position. | stays `Changes` |
| `ChangesFromEarliest` | Read everything the feed still holds, without a full load. **The requirement.** | → `Changes` |
| `ChangesFromLatest` | Skip to now; process nothing that came before. | → `Changes` |

All three of the non-ordinary intents transition to `Changes` once their pass has applied its changes,
in the same place and under the same conditions the watermark is written — `RunKind.Primary` only,
after the target write commits. A pass that fails leaves the intent where it was, for the same reason
it leaves the watermark: the work is still to do.

## What each reader can honestly support

Declared by the reader, not assumed by the caller — the opt-in shape `ISegmentExpandingReader` and
`IPositionAcknowledging` already use.

| Reader | `InitialLoad` | `Changes` | `ChangesFromEarliest` | `ChangesFromLatest` |
| --- | --- | --- | --- | --- |
| Change Tracking | ✓ | ✓ | ✓ `CHANGE_TRACKING_MIN_VALID_VERSION` | ✓ `CHANGE_TRACKING_CURRENT_VERSION` |
| CDC | ✓ | ✓ | ✓ `min_lsn`, read inclusively | ✓ max LSN |
| Trigger audit | ✓ | ✓ | ✓ surviving `min(DS_Seq)` | ✓ `max(DS_Seq)` |
| Watermark | ✓ | ✓ | ✗ — **identical to `InitialLoad`**, the feed is the table | ✓ `max(col)`, storing it without reading rows |
| Batch reload | ✓ | ✗ — no incremental mode exists | ✗ | ✗ |
| Scripted / DuckDB query | script's business | script's | script's | script's |

Two things the matrix makes visible that the watermark framing hid:

- **The watermark reader is asymmetric.** `ChangesFromLatest` is genuinely useful for it — adopt a
  table as already-synced by storing `max(col)` without reading a row — while `ChangesFromEarliest` is
  just a full load under another name, and offering it would be a button that lies.
- **Trigger audit's floor is what pruning left**, not what the table once held: it implements
  `IPositionAcknowledging` and deletes `WHERE DS_Seq <= @throughSequence`, so the earliest has to be
  read, never assumed.

An intent a reader does not support should be unofferable in the UI and a loud failure if it arrives
anyway — the same posture phase 91 took with an empty metadata cache, and for the same reason.

## Where it lives

`ChangeWatermarks` gains a column, keyed as it already is by
`(TaskName, MappingName, SourceTable)` — the exact granularity an intent needs. `Migrations.cs` has
the precedent (`ALTER TABLE ChangeWatermarks {{addcolumn}} WatermarkTimeUtc {{text}} NULL`), and this
is one more of the same.

One schema consequence to settle: **`Watermark` is currently `NOT NULL`.** `ChangesFromEarliest` with
no position yet is a state that has to be storable — an intent and no watermark is now a meaningful
row, where before it was a contradiction. Either the column becomes nullable, or a row carries an
intent before it carries a position by some other means. Nullable is the honest one.

## Absence of a row: an application default, overridable the way everything else is

**Resolved 2026-09-04.** A mapping that has never run has no `ChangeWatermarks` row, so something has to
say what that means. It is not an inference — it is a **configured default**, resolved through the same
inheritance every other setting here uses.

- **Application default: `InitialLoad`.** Today's behaviour, unchanged for anybody who configures
  nothing.
- **Overridable at the replication, and at the table mapping.** Most-specific-first, each falling back
  independently, exactly as `ProvisioningResolution` and `PipelineResolution` already do:

  ```csharp
  public static ReadIntent Default(ReplicationTaskConfig? task, TableMappingConfig? mapping) =>
      mapping?.DefaultReadIntent ?? task?.DefaultReadIntent ?? ReadIntent.InitialLoad;
  ```

**The setting is a `ReadIntent`, so the property it buys is stated rather than named.** A replication
adopting tables that are already in sync sets its default to `ChangesFromLatest`; one standing up
against a live feed sets `ChangesFromEarliest`; the cautious answer is to leave it at `InitialLoad`.

Setting it to either `Changes…` value is what gives the requirement its real form: **the application
never decides on a full load by itself.** After this, every full load in the system traces to somebody
choosing one —

- the configured default is `InitialLoad`, which is a choice even when it is the choice not to change
  anything;
- an operator sets the intent to `InitialLoad` on a mapping;
- an operator resyncs, which is the same act with a shorter name.

There is deliberately no fourth path. The one that would otherwise sneak in — a reader quietly
downgrading an intent it cannot honour — is closed below.

**Nullable at both levels, deliberately.** Null on a mapping means inherit; null on the replication
means nobody has said, which resolves to the application default. This is the shape
`ProvisioningConfig` documents and it exists for a concrete reason beyond symmetry — phase 46's
`Enabled` bug: a non-nullable value at its default is omitted by the YAML serializer, so an explicitly
chosen value that happens to equal the default would silently not survive a round trip.

**Validated at save time, not at run time.** Not every reader supports every intent (see the matrix
above), so a replication defaulting to `ChangesFromEarliest` over a mapping using the watermark reader
is a configuration that cannot work. `ParameterCheck` already exists for exactly this class of mistake
— it is called from the mapping `PUT` so that "a per-stage override naming a Kind or a setting that
cannot work is caught while somebody is still looking at the edit, not on the first pass". The same
check, against the resolved reader's declared intents.

**And an unsupported intent at run time must never silently become a full load.** Save-time validation
does not close this: a mapping can be saved with a valid default and have its reader changed
afterwards, or inherit a replication-level default set later. If the resolved intent is one the reader
does not support, the honest outcomes are a loud failure or a `ReadHold` — never a quiet downgrade to
`InitialLoad`, which would be the application deciding on a full load on its own and would break the
guarantee above at exactly the moment nobody was watching. The same posture phase 91 took when it
refused to fall back to a live catalog query on an empty cache.

One consequence worth stating: with a configured default of `ChangesFromEarliest`, a **brand-new**
mapping's first pass reads from the feed's floor instead of loading the table. That is precisely what
"never default to a full load" asks for, and it is a real decision about data — the screen offering it
should say that rows predating the feed will never arrive.

## What this supersedes and breaks

- **`detailed-design.md` §4.1** is rewritten. Its rule — "no stored watermark means read the whole
  source table" — becomes "the stored intent says what to do", with the null-watermark behaviour
  surviving only as `InitialLoad`'s definition.
- **`ChangeReaderFirstPassContractTests`** evolves rather than dies: instead of classifying each reader
  as full-load-on-first-pass or exempt, it asserts each reader declares its supported intents and that
  the declaration is complete. The point it exists to defend — that a new reader cannot be added
  without somebody deciding — is unchanged.
- **`ResyncService`** stops deleting a row and sets `InitialLoad` instead, which is the same act said
  out loud. Its `PositionExpired` gate is then arguably too narrow: with intents, "reload this mapping"
  is a thing an operator can legitimately ask for at any time.
- **`PositionExpiredException`** gains a better remedy to offer — see the next section, which is now
  in scope rather than a consequence.

## An expired position should stop the table, not retry forever

### What happens today

`RunExecutor` catches `PositionExpiredException`, completes the run as `Failed` with
`RunFailureKinds.PositionExpired`, and raises a `NotificationKinds.PositionExpired`. The Runs tab then
offers `Resync` on that row.

**Nothing stops.** There is no backoff, no quarantine and no consecutive-failure logic anywhere in the
codebase — the next scheduled tick enqueues the mapping again, it fails again, and it goes on doing so
every interval indefinitely. Each iteration writes another failed run and raises another notification.
The run history for a replication with one expired table becomes a wall of identical failures that
buries every other failure in it.

And the one remedy offered is `Resync`: clear the watermark, full `BatchReload`. On a large table that
is hours of work to recover from a source that, very often, still holds most of what was missed.

### What it should do

Fail loudly, **stop replicating that table**, say so on screen, and let the operator choose the
recovery. With intents, the choice is real for the first time: `ChangesFromEarliest` catches up from
the surviving floor — minutes, and it loses only what the source genuinely discarded — while
`InitialLoad` is today's reload for when that is not good enough.

### A second column, not a fifth intent

The question was whether `PositionExpired` becomes a `ReadIntent` value or a column of its own. **Its
own**, and pause is what settles it.

The two answer different questions. `ReadIntent` is *what to do on the next pass*; a hold is *whether
there should be a next pass at all*. Collapsing them destroys information at exactly the moments it
matters:

- **Pause has to preserve the intent underneath it.** A mapping paused mid-`ChangesFromEarliest` must
  resume to `ChangesFromEarliest`, not to `Changes`. If `Paused` occupies the intent column, pausing
  overwrites the instruction it is meant to defer.
- **Recovery needs both values at once.** An operator resolving an expired position sets
  `ChangesFromEarliest` *and* clears the hold. Those are two facts about the same row, and one column
  cannot hold them while the decision is being made.
- **The transition rule stays clean.** "Every intent becomes `Changes` once a pass applies its changes"
  holds without exception only if a hold is never an intent.

So:

| | |
| --- | --- |
| `ReadIntent` | `InitialLoad` · `Changes` · `ChangesFromEarliest` · `ChangesFromLatest` — what to do when it runs |
| `ReadHold` | `None` · `PositionExpired` · `Paused` — why it is not running |

Both on `ChangeWatermarks`, both keyed the same way. The fair counter-argument for one column is that
there would be one value to read and the recovery would clear the hold in the same write; two columns
written in one statement get that anyway, which is how `Watermark` and `WatermarkTimeUtc` are already
handled and for the same reason.

`ReadHold`/`HoldReason` naming is open; what matters is that the enum's values are *reasons*, so a new
one can be added when another known-cause failure earns it.

### Pause at table grain, beside the replication's — and where all of it is managed

**Resolved 2026-09-04.** Phase 64's pause is per replication (`Tasks.Paused`, `PauseEvents` keyed by
`TaskName`, no mapping column). `ReadHold`'s `Paused` is a second, finer one — "this one table is
broken, leave the rest replicating", which is exactly the situation an expired position creates.

**The Monitoring tab is where all of it is seen and managed.** Every table-mapping-level action —
pause and resume, the current intent and hold, and choosing a recovery — belongs on that screen. It is
already the per-mapping operational view: a row per mapping, source and target resolved through
inheritance, and its lag. Intent and hold are the same kind of fact about the same row, and putting
them anywhere else would mean an operator watching a replication has to leave the screen that told them
something was wrong in order to do anything about it.

Two things that follow from choosing that screen:

- `MappingLagRow` already carries `data-lag-state`; intent and hold want the same treatment, and the
  row is already on phase 96's `.grid-row auto` variant, so it can grow without the overflow that
  variant was created to fix.
- A replication-level pause and a table-level hold will both be visible on it, which is what makes two
  grains safe rather than confusing — but they still need a stated precedence, so that a table resumed
  under a paused replication reads as "still not running, and here is why".

**Notes and history are a follow-up, deliberately.** Recording *why* somebody held a table, and showing
the history of holds, is worth having and is not needed for the mechanism to work. It is noted in
`planning/todo/pause-history-ui.md`, which already exists for phase 64's unviewable `PauseEvents` — the
attractive shape is **one history table covering both grains**, and one screen over it, rather than a
second history beside the first.

### Two things that fall out

- **Notifications get better by accident.** Today an expired position raises one notification per
  failed tick. Entering a hold is a single event, so it should notify once on entry — which is what
  anybody would have expected it to do.
- **`MetadataNotCached` is the same shape** and has the same problem: a known cause, a known fix, and a
  failure that repeats every interval forever. It is an obvious second candidate for a hold, and it is
  *not* being scoped in here — noting it so the enum is designed with a second reason in mind rather
  than retrofitted for one.

## Still to work out

- **Per mapping or per source table?** The key includes the table, so a multi-source mapping has
  several rows, several intents and several holds. The UI has to be honest about which it is setting.
- **Does a hold block a Backfill too, or only Primary passes?** A backfill does not use the watermark
  and could legitimately be how somebody recovers — but "stopped" that still runs work is a
  contradiction an operator should not have to hold in their head.
- **How does a held table report on the Monitoring and Replications screens?** A replication whose
  every mapping is held is not healthy, and a lag figure for a table that is not running is
  meaningless.
- **Refuse while a run holds the lock** — an intent cannot change under a pass that is acting on it.
- **An audit trail.** `TaskRuns` records a watermark advance per run (phase 71); an intent change and
  the pass that consumed it belong somewhere comparable.
- **`ChangesFromLatest` is deliberate data loss.** Its confirmation should name what is being skipped,
  and it probably wants a phase 43 verification offered beside it — that is what would tell an operator
  whether "this table is already in sync" was true.
- **Does `run-lag.md`'s deferred capability interface merge with this one?** Both are "the reader
  declares what it can answer about positions, with units, or says not applicable". Two interfaces over
  the same set of readers would be one too many.

**Next step**: nothing is blocking a design — both open questions are settled above. Ready for a phase
doc, which will be a large one: a state-store migration, a config setting with resolution and
validation, a reader capability interface, changes to every reader, `ResyncService`, the scheduler's
dispatch, the Monitoring tab, and a rewrite of `detailed-design.md` §4.1. Worth considering whether it
splits — the intent and the hold are separable, and the intent is what the original requirement asked
for.

---

# Outcome — resolved 2026-09-04

Agreed, and split into three phases rather than one. The design here is the plan of record for all
three; each phase doc carries its own slice of it.

- **`implementation/todo/phase-100-read-intent-storage-and-defaults.md`** — the enums, the
  `ChangeWatermarks` migration (including making `Watermark` nullable), the store and remote-state
  paths, `DefaultReadIntent` with its resolution, and the API to set intent and hold. **Nothing reads
  the intent when it ends** — the same deliberate seam phase 90 used for the metadata cache, so the
  behaviour change is reviewed on its own.
- **`implementation/todo/phase-101-readers-honour-the-read-intent.md`** — the reader capability, CDC's
  inclusive floor read, `RunExecutor` consuming the intent instead of inferring from a null watermark,
  the `PositionExpired` hold and the end of retry-forever, `ResyncService`, and the rewrite of
  `detailed-design.md` §4.1. The behaviour change, and the one to review carefully; phase 91 is the
  model.
- **`implementation/todo/phase-102-monitoring-tab-manages-intent-and-hold.md`** — the Monitoring tab
  showing and managing all of it, both pause grains made legible, and the default setting on the
  config screens.

Decisions that were open in this doc and are now settled, for anyone reading it later:

1. **An explicit intent, not watermark surgery.** The first shape of this feature — "let the operator
   set the watermark to the earliest or latest detectable position" — is rejected. It was inexpressible
   for CDC, where the two constraints contradicted each other, and the least-bad option was to lose one
   change quietly.
2. **A separate `ReadHold` column, not a fifth intent value.** Pause settles it: a hold must preserve
   the intent underneath, and recovery needs both values at once.
3. **Absence of a row is a configured default, not an inference** — application default `InitialLoad`,
   overridable at replication and mapping, nullable at both levels.
4. **An unsupported intent never silently becomes a full load.** Save-time validation does not close
   it; the run-time outcome is a loud failure or a hold.
5. **The Monitoring tab owns the table-level actions**, and pause history over both grains is a
   follow-up recorded in `pause-history-ui.md`.
