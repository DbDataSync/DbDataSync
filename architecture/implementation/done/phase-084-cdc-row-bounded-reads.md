# Phase 84 — Row-bounded reads for CDC

**Status**: Not started.
**Plan reference**: `architecture/planning/done/cdc-row-bounded-reads.md`, which resolves
`architecture/planning/todo/mssql-cdc-source-batching-and-guaranteed-delivery.md`'s "Follow-up 1" and
the fix `change-queue-fairness-investigation.md` (`architecture/planning/todo/`) proposed independently.

## The gap

`MsSqlCdcReader` has no row-cap mechanism — confirmed by `grep`, zero references to `BoundedRead`
anywhere in `MsSqlCdcReader.cs`. An uncapped incremental pass reads, stages, and writes everything
between the stored LSN and the database's current max LSN in one uninterrupted `WorkItem`. Two
consequences, both real: it can occupy a worker slot for an unpredictable, unbounded duration
(`change-queue-fairness-investigation.md`), and as of phase 76's default 1800s `CommandTimeoutSeconds`,
it can now fail outright rather than merely run long.

## What to build

### Reader change

Mirror `MsSqlChangeTrackingReader`'s existing shape (`BoundedRead.Read(options)`,
`MsSqlChangeTrackingReader.cs:89`, `ReadResult.Bounded`/`WatermarkAfterRead` at `ReadResult.cs:20-36`),
but ordered correctly for CDC rather than reusing Change Tracking's single-column ordering: `TOP (@n)
WITH TIES` (or `FETCH FIRST @n ROWS WITH TIES`, whichever this reader's existing statement-building
style already uses — check `MsSqlCdcStatement`/`ChangeRowDataReader` first) ordered by
`(start_lsn, seqval)`, since one LSN can span a whole transaction's worth of rows and `__$seqval` is
what orders within it. `ReadResult.WatermarkAfterRead` needs `MsSqlCdcReader` to populate `Bounded` the
same way `MsSqlChangeTrackingReader` already does — a capped read persists how far it actually got, not
the full window's end.

### Default cap, not opt-in

Both mechanisms get a default cap now rather than staying opt-in — resolved in planning, since an
unbounded mapping is exactly the one nobody thought to configure. Pick a default row count (headroom vs.
too many small re-enqueued passes is the tradeoff; look at what a reasonable batch size costs through
staging/writing before picking a number, don't guess one that only accounts for the read side).

### No changes needed elsewhere

`ChangePollingGate` (phase 75) already handles a capped, multi-pass drain correctly — it compares each
mapping's own watermark against a freshly-fetched value every tick, never a cached "last checked" flag,
which is exactly what a mapping mid-drain with no new source writes between passes needs. Confirm this
composition with a test (below) rather than assuming it from the doc's own reasoning.

## What this phase should not do

- The guaranteed-delivery mode (`mssql-cdc-source-batching...md`'s Follow-up 2) or its same-key
  PK-collision prerequisite fix in `Scd2Writer` — both remain separate, unstarted work.
- Any transactional-integrity-across-a-split guarantee — row-level ordering only, not transaction
  grouping.
- Row-bounding for any other reader — Change Tracking already has it, nothing else in scope needs it.

## How to verify

- A test with a backlog larger than the default cap, asserting the pass advances only partway and the
  watermark reflects `WatermarkAfterRead`, not the full window's end.
- A test asserting a subsequent pass picks up exactly where the previous capped one left off — no gap,
  no duplicate rows re-delivered.
- A test confirming a transaction spanning more rows than the cap doesn't lose row-level ordering across
  the split (per `(start_lsn, seqval)`), even though transaction-grouping itself isn't preserved.
- An integration-level test exercising `ChangePollingGate` against a mapping mid-drain across several
  capped passes with no new source writes in between, asserting the gate keeps dispatching it (per
  revision 3's reasoning in phase 75's own doc) rather than misreading "counter unchanged" as "nothing to
  do."
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean if the
  cap becomes a configurable per-mapping option surfaced in the UI (check whether Change Tracking's
  existing `BoundedRead.Descriptor` parameter already gives CDC the same UI surface for free, or needs
  its own).

---

## Outcome

**Shipped.** `MsSqlCdcReader` caps a pass by default, and so does `MsSqlChangeTrackingReader` — the
doc's "both mechanisms" clause, which is the half of this phase that is not about CDC at all.

### What was built

`MsSqlCdcStatement.BuildRead` gained a `bounded` flag. Off, it emits exactly the statement it emitted
before this phase — asserted by a test, because the escape hatch is only an escape hatch if it lands
back on the old query. On, it wraps the CDC function in a derived table that takes
`TOP (@maxRows) WITH TIES ... ORDER BY __$start_lsn`, carries `__$start_lsn` outward aliased as
`BoundedRead.PositionColumn`, and orders the outer select by `(position, __$seqval)`. The position
column is appended last, after the mapped columns, so every ordinal the reader already reads by is
unchanged whether the cap is on or off.

`MsSqlCdcReader` reads its cap, passes `BoundedReadPosition` into `ReadResult`, tracks the last row's
LSN and the row count while streaming, and sets `Reached` only when the pass actually hit the cap. A
pass that drained its window advances to `maxLsn` instead — the position it fixed for itself before
reading. That asymmetry is not tidiness: a quiet table whose watermark froze at its last change
eventually falls below `fn_cdc_get_min_lsn` and expires.

`BoundedRead` grew `DefaultMaxRows`, a second `CappedDescriptor`, and a `Read(options, whenUnset)`
overload.

### The tie is on the LSN alone — the doc's design, corrected

The doc and the plan both say `TOP (@n) WITH TIES` ordered by `(start_lsn, seqval)`. Ordering by both
is right. **Tying on both is a data-loss bug**, and the implementation does not.

A CDC watermark is an LSN: `MsSqlCdcCatalog.ToWatermark` stores one, and the next pass resumes at
`sys.fn_cdc_increment_lsn` of it. An LSN is therefore the finest position this reader can *record*. Tie
on `(start_lsn, seqval)` and a pass may stop halfway through a transaction, record that transaction's
LSN, and have the next pass start strictly beyond it — every remaining row of that transaction silently
gone. Tying on `__$start_lsn` alone makes `WITH TIES` pull in the rest of the transaction whatever the
cap said, so the position reached is always one no row still sits at.

The consequence is deliberate: **a single transaction larger than the cap is delivered whole, in one
pass, over the cap.** The cap bounds the common case rather than guaranteeing a ceiling, because the
alternative is losing rows. The doc's third verification test was written against this behaviour rather
than against the doc's wording.

This is also why the two orderings cannot be one. `WITH TIES` ties on whatever the `ORDER BY` says, so
the cap is taken in a derived table ordered by the LSN, and the rows come out of it ordered by LSN and
seqval. Two orderings for two questions: where may this pass stop, and in what order did these changes
happen.

### Deviations from the doc

**`MsSqlChangeTrackingReader` was changed too**, which the doc's "Reader change" section does not
mention but its "Default cap, not opt-in" section requires: *both* mechanisms get a default cap. It
moved from `BoundedRead.Descriptor`/`Read(options)` to `CappedDescriptor`/`Read(options,
DefaultMaxRows)`. Its incremental path was already bounded-capable from phase 56; only the default
changed. Its first pass stays uncapped for the reason it already documented — that pass reads the table,
not `CHANGETABLE`, and a row cap there is a partial full load with no resumable position to record it.
`MsSqlCdcReader`'s first pass is uncapped for the same reason.

`WatermarkReader` deliberately keeps `Descriptor` and the opt-in `Read(options)`. Bounding is only cheap
if the ordering column is indexed, and a watermark scan runs over a column the operator chose; a
log-based reader orders by the change table's own clustered key and has no such doubt. That is the whole
reason the fallback is a parameter at the call site instead of a constant inside `BoundedRead` — a
default that silently applies to whichever reader is added next would be wrong for half of them.

**No SPA change, and none was needed.** The doc asked this to be checked rather than assumed: the UI
renders whatever `IChangeReader.Parameters` declares, so declaring `CappedDescriptor` on `MsSqlCdcReader`
is the entire frontend surface. Nothing under `src/DataSync.Web` was touched and no `tsc -b` run was
required.

### Judgment calls

- **50,000 rows.** Picked against the whole pipeline, not the read — which the doc explicitly warned
  against getting wrong. Every admitted row is read, staged, and then applied in `ApplyBatch.DefaultSize`
  chunks, so this is ten apply chunks and one staging table's bulk copy. Small enough that a pass
  finishes well inside phase 76's 1800s default command timeout and a worker slot turns over often
  enough for a sibling mapping to get one; large enough that a real backlog is not drained in
  thousand-row sips, each paying again for its catalog lookups, staging table and watermark write.
- **Zero, and only zero, means unbounded.** Before this phase an empty option meant "read the whole
  window"; it now means "use the default". Defaulting the cap on is only defensible if an operator can
  still ask for the old behaviour, so `0` is that request. A negative or unparseable value falls back to
  the default rather than throwing, as `ApplyBatch.Read` already does — but note it falls back to the
  *cap*, not to unbounded. Reading a typo as "read everything" would turn a mistyped knob into precisely
  the uncapped pass this phase exists to prevent.
- **Two descriptors, one option key.** `Descriptor` still says the cap is off unless configured, because
  for the watermark scan it is. A capped-by-default reader must not tell an operator that leaving the
  field empty reads the whole window.
- **The position column is projected, not inferred.** The reader could have re-read `__$start_lsn` from
  the row it just materialised, but the mapped columns are rendered through `RenderColumn` and may be
  transforms; the LSN is not among them. Carrying it explicitly under one shared alias keeps the bounded
  result set the unbounded one plus one trailing column.

### One test that belongs to this phase and is not in its commits

The doc's fourth verification item — `ChangePollingGate` dispatching a mapping mid-drain across several
capped passes with no new source writes between them — was written as
`ChangePollingGateTests.ACdcMappingDrainingUnderARowCap_IsDispatchedOnEveryTickUntilItCatchesUp`. It
passes. It is not in this phase's commits: `ChangePollingGateTests.cs` was simultaneously being rewritten
by phase 85, and splitting one file across two agents' commits was the worse of the two risks. It ships
with phase 85.

The composition it asserts is confirmed either way, and it is confirmed rather than assumed: five ticks,
five capped passes each advancing only the mapping's own watermark, the shared counter never moving, and
the gate dispatching on every one of them until the watermark catches the counter.

### How it was verified

- `MsSqlCdcStatementTests` (+7): the unbounded statement unchanged, `TOP (@maxRows) WITH TIES` on the
  row-limit parameter, the tie on the LSN alone, the inner/outer ordering, the position column appended
  last with nothing else leaking out, the lower bound still incremented, and a transform rendered inside
  the cap with its alias selected outside.
- `MsSqlCdcReaderTests` (+5, `Category=Integration`): a backlog larger than the cap stopping partway
  with a `WatermarkAfterRead` below the window's end; several passes delivering every change exactly
  once with no gap and no duplicate; a transaction larger than the cap delivered whole and in
  `(start_lsn, seqval)` order across the split; a default-configured mapping running the bounded
  statement; and `0` restoring the whole-window read.
- `MsSqlChangeTrackingReaderTests` (+2, `Category=Integration`): the same two default-behaviour tests,
  which are the only assertions that would notice the other half of this phase being reverted.
- `BoundedReadTests` (new, 8): the two fallbacks, the zero escape hatch, the typo cases falling back to
  whichever default the reader named, and the two descriptors agreeing on the key while differing on the
  advertised default.
- Full suite: `Category!=Integration` **1022 passed**, `Category=Integration` **207 passed**. Every
  project owning code this phase touched is green in both filters — `DataSync.Drivers.MsSql.Tests` 119
  and 91, `DataSync.Drivers.Abstractions.Tests` 49, `DataSync.Drivers.Generic.Tests` 144. The remaining
  failures are the pre-existing ones below plus in-flight work from the phase being built alongside this
  one, none of them in a file this phase changed.

### Pre-existing failures, confirmed as such

Three non-integration failures were present before this phase and are unrelated to it. Verified by
running them in a clean `git worktree` of `HEAD` with none of this phase's changes applied, where they
fail identically:

- `InviteCommandTests.Invite_AgainstAnMsSqlConfiguredRepo_Succeeds` — the test writes a connection string
  containing `Password`, which phase 79's `ConfigValidation.RejectEmbeddedCredential` now rejects. The
  test predates the rule.
- `AdminConfigControllerTests.AFileSourcedValue_RoundTrips` and
  `.AFileSourcedStateConnectionString_IsShownAsIs` — both expect a source of `"file"` and get
  `"default"`.

Not fixed here: all three are phase 79/81 territory and fixing them inside this phase's commits would
have hidden them.

`PreviewIntegrationTests.Metrics_ReportTheRunThatJustHappened_AndWhenItCompleted` failed once during a
full-suite integration run and passes in isolation, both in this tree and in the clean one. Flaky under
load, not a regression.

### What this phase did not do

`Scd2Writer`'s same-key PK collision and the guaranteed-delivery mode remain unstarted, as scoped. Row
bounding is a prerequisite for the latter and is now in place. No transaction-grouping guarantee was
added, and the LSN-alone tie is not one — a transaction is never *split* across passes, but two
transactions still may be delivered in the same pass or in different ones, and nothing here makes a pass
transactionally atomic at the destination.
