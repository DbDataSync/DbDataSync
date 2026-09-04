# A Bulk Load pipeline: initial load and backfill become one configurable thing

**Status: resolved 2026-09-04 — see Outcome at the end.** Raised 2026-09-04, out of a question about how a mapping's backfill
settings relate to its initial load. The answer — that they are unrelated except for one shared setting
that half of the readers ignore — is the problem, not the explanation.

## The intent, stated plainly

This is a design change, not a bug fix, and the four points are the whole of it:

1. **No change reader implements an initial load of its own.** Not one of them. A reader reads changes;
   that is the entire job.
2. **An initial load and a backfill are the same thing.** Not similar, not parallel — the same
   operation, reached from two directions.
3. **That thing is its own "Bulk Load" pipeline**, configured by the operator — reader, cache, writer
   and segmenting — **separately from the existing "Change Processing" pipeline**.
4. **When a change reader determines that it needs an initial load, it causes a Bulk Load** using
   whatever segmenting strategy and implementation the operator has configured. It does not perform one
   itself.

Everything below is what that costs and what it collides with.

## What is *not* changing — read this first

Three things share the name and are easy to conflate. Only the third is affected:

1. **The `ReadIntent` column on `ChangeWatermarks`** — unchanged. Phase 100 is reused whole.
2. **`ReadIntent.InitialLoad` itself** — unchanged, and load-bearing. It is the signal this entire
   design turns on, and it keeps every other job phase 100 gave it: a stored request about the next
   pass, defaulted through `ReadIntentResolution`, set by an operator, distinct from a `ReadHold`.
   **Nothing here removes an intent.**
3. **Per-reader declaration of whether `InitialLoad` can be honoured** — this is the only thing that
   changes, and "removed" is the wrong word for it.

### `InitialLoad` becomes universally available

Today whether a mapping can do an initial load depends on which reader it is configured for, which is
why phase 101 planned to have each reader declare it. Once the Bulk Load pipeline performs it, **every
mapping can do one regardless of its change reader** — the answer is unconditionally yes, so a
per-reader question with only one possible answer stops earning its place.

That is a strengthening. It also retires phase 101's "an intent the reader cannot honour is never
quietly downgraded" *for this intent only*: `InitialLoad` can no longer be un-honourable. The rule
still matters for `ChangesFromEarliest` and `ChangesFromLatest`, which remain genuinely per-reader.

### The declaration does not disappear — it becomes the one the handover needs

A reader still has to answer a capability question for an initial load to be *safe*, just a different
one: **can it report its current position without reading any rows?** That is what has to happen before
the bulk load starts, and a reader that cannot do it cannot be handed over to safely — see "The
correctness crux" below.

So §1's table keeps a row per reader and keeps its shape. The `InitialLoad` column is replaced by a
position-capture column, which is the thing that actually determines whether the handover works.

**If phase 101 is being built right now**, that is the one section to redirect: not deleted, retargeted.
Its doc should be amended in place, as `implementation/README.md` prescribes for a plan that changes
materially during implementation.

## What is there today

**Every reader implements its own full load**, branching on `previousWatermark is null`:

| Reader | no stored watermark |
| --- | --- |
| `MsSqlChangeTrackingReader` | reads the source table itself |
| `MsSqlCdcReader` | reads the source table, after waiting for the capture floor |
| `TriggerAuditReader` | reads the source table |
| `WatermarkReader` | the same `SELECT`, with no predicate |

This is not accidental — it is **documented as the rule in `detailed-design.md` §4.1 and pinned by a
test**. Phase 99 built `ChangeReaderFirstPassContractTests`, which enumerates every `IChangeReader` and
fails until a new one is declared as either following the rule or exempt from it. The rule exists for a
real reason: a change feed only knows about changes since it was switched on, so a mapping that began
incrementally would be permanently and silently half-replicated.

**A backfill is a different code path.** Its own `RunKind`, its own run, lock and queue item, one item
per segment, handed `null` for the watermark unconditionally so it can never disturb the cursor. Its
reader/cache/writer come from **transient work-item overrides** that `BackfillService` sets — not from
anything the operator configured as a pipeline.

**Segmenting lives on the mapping**, as `TableMappingConfig.DefaultSegmenting`, described as "what a
scheduled `BatchReload` pass processes **and** what the Backfill form starts from".

**Phase 100 has landed**, so the intent already exists and is already stored:

- `ReadIntent { InitialLoad, Changes, ChangesFromEarliest, ChangesFromLatest }` and `ReadHold`, in
  `DbDataSync.Core.Config`.
- `ChangeWatermarks` carries `ReadIntent` and `ReadHold`, with `Watermark` now nullable, read as one
  `MappingReadState(Intent, Hold, Watermark, WatermarkTimeUtc)`.
- **No row resolves through `ReadIntentResolution.Default`**, backed by `DefaultReadIntent` on the
  replication and the mapping, whose application default is `InitialLoad`. So "a brand-new mapping is
  an initial load" is already true and already configurable — this design does not have to invent it.
- Nothing reads the intent yet. `RunExecutor` still infers a full load from a null watermark; phase 101
  is the switch.

One shipped detail changes meaning under this design: `ReadIntent.InitialLoad`'s own doc comment reads
*"Read the source table itself. Today's null-watermark behaviour, now stated rather than inferred."*
It becomes "run the Bulk Load pipeline" — the same intent, performed somewhere else.

## The symptom that prompted this

`RunExecutor.ResolveSegmentsAsync` is called on **every** pass and returns `DefaultSegmenting` whenever
it is non-empty. There is no reader-kind guard — the only early exits are "this work item carries a
segment" and "the mapping configures none".

But only `BatchReloadReader`, `MsSqlBatchReloadReader` and the DuckDb query tokens apply a segment
predicate. `MsSqlChangeTrackingReader`, `MsSqlCdcReader`, `TriggerAuditReader` and `WatermarkReader`
contain no segment handling at all.

So a mapping configured for Change Tracking that also sets `DefaultSegmenting` — **the natural thing to
do, because that is what seeds the Backfill form** — appears to run its segment loop N times per
scheduled pass, each iteration calling a reader that ignores the segment and returns the same change
set. N× duplicated read/stage/apply, N×-inflated `rowsRead`/`rowsWritten`, watermark taken from the
last iteration.

*Traced in code, not reproduced.* `RunExecutor.cs:570` and the loop at 619 are where to look.

**It is a symptom, and the redesign is its structural fix.** Segmenting is a bulk-load concept, but
until now there was no bulk load to own it — so it sat on the mapping where *both* loops could read it,
and only one of them should. Giving bulk load a pipeline does not require moving the setting (see the
Outcome): it is enough that the Change Processing path stops consulting something that was never its
business.

## What changes

### A second pipeline, beside Change Processing

`BulkLoadConfig` at replication level with per-mapping override, mirroring `ChangeProcessingConfig` and
resolved by the same `PipelineResolution` shape: a reader (defaulting to `BatchReload`), a cache, and a
writer — warned about, not constrained, when it does not reconcile (see the Outcome).

**Segmenting is not part of it.** It stays on the mapping where phase 58 put it, because how a table
divides is a fact about the table rather than about a pipeline.

This mostly **promotes something that already exists but is invisible**: the transient work-item Kind
overrides `BackfillService` sets today become real, named, user-visible configuration. The operator
stops discovering what a backfill will use by reading a phase doc.

### Readers stop full-loading, and §4.1's rule inverts

Every `previousWatermark is null` full-load branch comes out of every reader. The reader's answer to
"I have no position" becomes *"I cannot read changes from here"* rather than *"I will read everything"*.

**This directly contradicts what phase 99 pinned three days ago**, and that is worth being blunt about
rather than discovering during implementation. §4.1 has to be rewritten, and
`ChangeReaderFirstPassContractTests` — which today fails a reader that does *not* full-load on first
pass — inverts: it should fail a reader that **does**. The test remains valuable in its new form for
exactly the reason it was written: the rule is implemented per reader, so nothing else would notice a
sixth reader quietly getting it wrong.

### The signal already exists

This is the part phase 100 has already built. `RunExecutor` resolves the intent — stored value, else
`ReadIntentResolution.Default` — and chooses the pipeline from it. **`InitialLoad` stops meaning "the
reader will full-load" and starts meaning "run the Bulk Load pipeline"**; the reader is not called at
all for that pass.

Three entry points into that state, which is what makes initial load and backfill one thing:

- **no `ChangeWatermarks` row** — brand new, or re-pointed at a different source table. Already resolves
  to `InitialLoad` through phase 100's default, with no extra work.
- **`PositionExpiredException`** — the source discarded history the stored position needed. Phase 101
  already turns this into a `ReadHold`; recovery sets the intent, which is where `InitialLoad` and
  `ChangesFromEarliest` become the two real choices phase 102 puts on screen.
- **an operator asking for a backfill** — the same pipeline over a chosen segment set rather than all
  of them. This was always a Bulk Load; it just had no name.

## The correctness crux: when the position is captured

This is the part that is free today and stops being free, and it is the single thing most likely to be
got wrong.

An initial load is only correct if the change feed's position is captured **before** the table is read.
`MsSqlChangeTrackingReader` gets this right by construction: it calls `GetCurrentVersionAsync` first and
returns that as `NewWatermark`, so changes made *during* the full read are replayed on the next pass.
At-least-once, which is the correct side to err on.

Split the load out of the reader and that ordering has to become explicit:

> capture the source position → run the Bulk Load → persist the position → switch the intent to
> `Changes`

Get it backwards and every change made during a multi-hour load is lost silently — the load succeeds,
the counts look right, and the rows are simply never seen again.

That needs a **position-capture primitive on the reader that does not read any rows**. The per-reader
pieces already exist — `MsSqlChangeTrackingReader.GetCurrentVersionAsync` is public and already reused
by `ChangeCounterSource`, and `WatermarkStatement.BuildMaxWatermark` is the watermark reader's
equivalent — but nothing exposes them through `IChangeReader`. An opt-in interface in the shape of
`IPositionAcknowledging` and `ISegmentExpandingReader` is the established pattern.

## What this changes for 99–102

**Phase 100 — done, and nothing in it is wasted.** The intent, the hold, the migration, the resolution
default and the store surface are exactly what this design needs. What changes is only what
`InitialLoad` *means* at the point of use, plus the one doc comment on the enum.

**Phase 101 — in flight, and only partly affected.** Taking its sections in order:

| section | what happens to it |
| --- | --- |
| §1 readers declare which intents they honour | **retargeted, not dropped.** `Changes`, `ChangesFromEarliest` and `ChangesFromLatest` stay per-reader, along with the declaration mechanism, the UI that offers them and the save-time validation. `InitialLoad` stops being a per-reader question — the Bulk Load pipeline makes it universally available — and its column is replaced by "can report its position without reading rows", the capability the handover actually depends on |
| §2 CDC reads its floor inclusively | **untouched, and still the sharpest thing in the phase.** It is about `ChangesFromEarliest`, a genuine reader concern — the distinct inclusive-read statement shape and the untouched `Compare(storedLsn, minLsn)` guard all stand |
| §3 `RunExecutor` reads the intent instead of inferring | **exactly right, and the seam this design plugs into.** The only difference: a resolved `InitialLoad` selects the Bulk Load pipeline rather than being handed to a reader |
| "every intent transitions to `Changes` once a pass applies" | holds, with one caveat — a multi-segment bulk load must transition only after **every** segment succeeds. See the open questions |
| "an intent the reader cannot honour is never quietly downgraded" | stands unchanged for `ChangesFromEarliest` and `ChangesFromLatest`. It stops applying to `InitialLoad` only because that intent can no longer be un-honourable — every mapping can do one — which is the rule being satisfied, not waived |

So the advice is narrow: **finish §2 and §3 as written, and retarget §1's `InitialLoad` column to
position capture**, amending the phase doc in place — which is what `implementation/README.md` already
prescribes for a plan that changes materially during implementation.

**Phase 102 — survives, and grows.** Its intent and hold controls are unchanged. It gains the Bulk Load
pipeline settings, and its "recovery from `PositionExpired` is a choice, presented as one" becomes more
literally true: `InitialLoad` visibly means "run this configured bulk load", which an operator can
inspect before choosing it over `ChangesFromEarliest`.

**Phase 99 — inverted.** §4.1 and `ChangeReaderFirstPassContractTests`, as above. It is the only one of
the four contradicted rather than extended.

---

# Outcome — resolved 2026-09-04

Every question above was answered. **Three phases**, plus amendments to two already in play. Phase
numbers are not reserved here — a number claimed before its file exists is what caused the 95/96
collision on 2026-09-03.

## The decisions

1. **One work item per segment**, exactly the shape a backfill already has — which is what makes them
   genuinely the same thing rather than merely similar. Each segment gets its own run, retry, progress
   and lock, so a failure at segment 38 of 40 does not redo the other 37, and a large table's initial
   load is precisely where that matters. It costs a **load id** grouping the items and a completion
   check that requires *every* sibling to have succeeded before the intent flips.
2. **A new `ReadHold` value while a load is in flight.** `ReadHold`'s own doc calls its values reasons
   and leaves room for another known cause to earn one; this is that. It is what stops a `Primary` pass
   running against a mapping with no valid position — necessary because `RunLocks` are keyed by
   `(TaskName, RunKind, MappingName)` and deliberately let a Primary and a reload run concurrently.
   Phase 102 already renders holds, so "loading" appears on the Monitoring tab for free rather than the
   mapping looking idle for three hours.
3. **`PendingWatermark` gets its own column** on `ChangeWatermarks`, beside the live one. A crashed
   load then cannot leave behind something that reads as a completed position. It implies one in-flight
   load per mapping, which the row granularity already gives. `Watermark` is nullable since phase 100,
   so the live column simply stays empty until the load finishes.
4. **Segmenting stays on the mapping.** It does not move into `BulkLoadConfig`. Segmenting is a property
   of the *table* — how does this table divide — not of a pipeline's reader/cache/writer, and phase 58
   already gave it a real editor there. The defect that started this is fixed by the Change Processing
   loop no longer reading it, which needs no move: **no migration, no breaking change, and a per-table
   fact stays per-table.**
5. **`RunKind.Backfill` is renamed `BulkLoad`.** One name for one thing, which is the design's central
   claim. It costs a migration (the value is persisted in `TaskRuns` and `WorkQueue`), phase 104's kind
   filter, and the SPA's `RunKindBadge`. If "was this asked for, or automatic" turns out to matter in
   run history, that is a separate fact and should be recorded as one rather than smuggled into the
   kind.
6. **A mapping with no resolvable Bulk Load pipeline fails save-time validation, and a run-time hold is
   the backstop.** Both, for the reason phase 101 already gives about intents: save-time validation does
   not close it, because a reader can be swapped or a replication-level default changed afterwards. The
   run-time outcome is a loud hold, never a silent fallback to a reader doing its own full load.
7. **The bulk-load writer is warned about, not constrained.** Reconciliation is required to converge a
   *drifted* target, but a first load into a table that was just created has nothing to remove and an
   upsert-only writer is correct and cheaper there. `WriterCapability.SupportsReconciliation` is already
   declared, so the picker surfaces it exactly as the Backfill form does.

## The phases

**Phase A — the Bulk Load pipeline as configuration.** `BulkLoadConfig` at replication level with
per-mapping override, resolved by the `PipelineResolution` shape; save-time validation; the
`RunKind.Backfill` → `BulkLoad` rename and its migration; the settings UI. **Backfill starts using it**
instead of the transient work-item Kind overrides `BackfillService` sets today — which is mostly
promoting something that already exists into something an operator can see. Nothing about initial load
changes yet: inert in the sense phase 100 was, and reviewable on its own.

**Phase B — initial load becomes a bulk load.** The position-capture capability on `IChangeReader` and
`PendingWatermark`; the new hold; per-segment enqueue under a load id; completion tracking; the intent
flip to `Changes` only when every segment has succeeded. **And the deletion**: every reader's
`previousWatermark is null` full-load branch comes out, §4.1 is rewritten, and
`ChangeReaderFirstPassContractTests` inverts to fail a reader that *does* full-load. The deletion cannot
be separated from the routing — the moment the runner sends `InitialLoad` to the bulk load pipeline,
those branches are dead code, and leaving them would be two paths to the same outcome.

**Phase C — the operator surface**, which is largely phase 102 absorbing the new hold and the pipeline
settings rather than a new phase of its own. Worth naming so it is not forgotten.

**Amend, do not re-plan:**

- **Phase 101, in flight** — finish §2 (CDC's inclusive floor) and §3 (`RunExecutor` reads the intent)
  as written; retarget §1's `InitialLoad` column to position capture. Amend the doc in place, as
  `implementation/README.md` prescribes.
- **Phase 102, planned** — gains the Bulk Load settings and the loading hold.
