# Phase 58 — Custom segmenting strategies for reload/backfill

**Status**: Complete
**Plan reference**: `architecture/planning/done/custom-segmenting-strategies.md`

## What this covers

A new "Custom" segmenting mode alongside Full/List/Range/Auto, backed by a strategy (DuckDB SQL,
source SQL, target SQL, or C#) that produces a labelled list of segment candidates; a Backfill UI that
presents those as a checklist rather than queuing all of them; and a configured default segmenting
list per mapping, which also replaced the old `segments` reader option outright.

## 1. `RangeSegment` gained an optional label

`Describe()` returns it when present and falls back to the generated `"{Column} [{min}, {max})"` when
not — additive, pinned by a test.

## 2. New script slot and strategy config

`ScriptSlots.SegmentingStrategy`, `ISegmentingStrategy`, `SegmentCandidate`, `SegmentingContext`, and
`SegmentingStrategyConfig` with its four kinds. All four return the same four columns —
`label`, `range_start`, `range_end`, `selected` — through one runner
(`SegmentingStrategyRunner`), so no two authoring paths can disagree about what an exclusive end means.
DuckDB.NET is a new dependency, used for exactly one read-only query against an ephemeral in-memory
instance.

`selected` lives on the candidate, not on `BatchReloadSegment`.

## 3. Backfill UI: a checklist

`BackfillForm.tsx` gained a Custom mode, a strategy picker, a preview (`GET
…/segmenting/{strategy}/preview`), per-candidate checkboxes pre-ticked from the strategy's flags, and
select-all/none. Submitting sends the checked candidates themselves. The form also opens pre-filled
from the mapping's stored default, without writing back to it.

## 4. `TableMappingConfig.DefaultSegmenting`, and the retirement

A `List<BatchReloadSegment>` on the mapping, edited by a new `DefaultSegmentingCard`.
`RunExecutor.ResolveSegmentsAsync` reads it and no longer reads
`readerOptions["segments"]` at all. `CustomSegment` is expanded fresh at both consumption points.

## 5. Scheduled reload from a strategy's selected candidates

A `BatchReload` mapping whose default is a `CustomSegment` re-runs its strategy every pass and reloads
only the `Selected == true` candidates.

## What this phase does not build

- Any change to `Auto`'s bucket algorithm.
- A replication-level default/override layer for segmenting — mapping-level only.
- Foreign-key/relational awareness of any kind.
- Any migration from the old reader option — see the retrospective.

---

# Retrospective

The largest of the five phases, and most of the risk was not where the plan doc expected. The four
authoring paths turned out to be one runner with four sources of rows; the interesting problems were
in config storage and in what the UI owes an operator.

## Moving `BatchReloadSegment` was forced, not chosen

The plan doc says the mapping "gains a field typed `IReadOnlyList<BatchReloadSegment>`". That is not
possible where the type lived: `BatchReloadSegment` was in `DataSync.Drivers.Abstractions`, which
references `DataSync.Core` — so config could not name it without a reference cycle.

It moved to `DataSync.Core.Config`. Twenty-seven files reference these types and only four needed a
new `using`, because the driver layer already imports config everywhere. The type's own doc always
claimed it was engine-neutral; this puts it where that claim is structurally enforced rather than
merely asserted.

## YAML needed a hand-written converter, and the bug in it was instructive

`BatchReloadSegment` is a sealed polymorphic hierarchy whose discriminator is a System.Text.Json
attribute. YamlDotNet knows nothing about those: it would write a derived record's fields with nothing
saying which record they belonged to, and cannot construct an abstract type on the way back in.

The configuration-only alternative — `IgnoreUnmatchedProperties` plus a synthetic settable
discriminator property — was rejected because it weakens validation for *every* config file this
serializer touches so that one type can round-trip. A mistyped key in a replication would then be
silently ignored instead of reported. So: `BatchReloadSegmentYamlConverter`, about 130 lines, emitting
the same `mode:` shape the JSON form uses.

The bug worth recording is in `Accepts`. Written as `type == typeof(BatchReloadSegment)`, it produced
a converter that **read but did not write**: deserialization offers the declared element type, while
serialization offers the *runtime* type (`RangeSegment`). The result was YAML with fields and no
discriminator, failing on the next load with "a segment is missing its 'mode'" — a long way from the
line that caused it. `typeof(BatchReloadSegment).IsAssignableFrom(type)` covers both directions.

## `selected` absent means nothing, not everything

The plan doc specifies the flag but not what a strategy that omits the column means. Taking "no
selection information" as "select everything" would turn an unattended pass into a full reload of
every segment a strategy could imagine — the loudest possible failure, on a schedule. A strategy that
does not say which candidates matter is proposing, not deciding, so unattended it does nothing. There
is a test for it.

## Change Tracking's lesson, applied to the SQL paths

The runner formats date bounds with round-trip `"O"` rather than the default. A segment's bounds are
re-bound against the source's own column type later, and a locale-formatted date would be parsed by
whatever the server's locale happens to be. This is the same reason `WatermarkValue.Format` exists;
it would have been easy to reach for `ToString()` and get something that works on one machine.

## Decisions the phase doc left open

- **Previews run synchronously for every kind, not just DuckDB.** The doc left this open out of
  caution about touching a real connection. The resolution is that the *caution* was right and the
  *restriction* was not: a preview is one query an operator explicitly asked for by picking a
  strategy, which is no more alarming than the live script test phase 41 already offers. What differs
  is a strategy bound as a **default**, which runs forever without being asked — and that is where the
  warning went. The preview endpoint opens connections only for a strategy that says it needs them, so
  a DuckDB preview still touches nothing.
- **Strategies live on the replication.** The doc did not say where. Not the mapping: the whole point
  is reuse, and "reload by calendar month" written once beats it written on forty mappings. Not a
  global registry either: a segmenting convention belongs to a set of tables replicated together.
- **A strategy states its own `Column`.** Not inferable from the query — the SQL returns bounds, not
  the thing they bound, and a DuckDB strategy generating month boundaries has no idea which date
  column it is generating them for.
- **The mapping editor treats Full/Auto/Custom as replacing the list, and List/Range as stacking.**
  This answers the doc's last open question. The array type permits mixing; the UI does not, because
  "the whole table" alongside "these three months" is two answers to one question rather than a list
  of two things to reload. Nothing stops a config written by hand from mixing them, and the runtime
  handles it — the editor simply does not offer it.
- **The C# path gets source metadata only when it is a C# path.** The SQL paths express what they need
  in their own query, so resolving a catalog on their behalf would be a round trip with no reader.
- **The repeated-execution warning is `.hint.warn`, beside the strategy picker on the mapping editor.**
  The CSS already had a `--warn` token and no rule using it for text; there is one now.

## The breaking change

The `segments` reader option simply stopped being read, per the plan. There is no changelog file in
this repo, so the note went into the README's backfill section — which needed rewriting anyway, since
it documented the old mechanism as the way to configure a standalone reload.

It is stated as behaviour rather than as an error: a config still carrying the option behaves as
though it had none. Silently reinterpreting a stored reload scope would be a worse failure than an
obvious one, which is the reasoning behind refusing the migration in the first place.

## Verification

- `SegmentLabelTests` (3) — the fallback, the label winning, and the label participating in equality
  (two ranges over the same bounds with different names are two different proposals).
- `SegmentSerializerTests` — a labelled range added to the round-trip set.
- `DefaultSegmentingYamlRoundTripTests` (5) — every mode through a real save/load, a label-less range
  coming back label-less, list values keeping order, an unconfigured mapping loading an empty list,
  and several static entries of mixed kinds as a list.
- `SegmentingStrategyRunnerTests` (9) — the plan doc's own `generate_series` worked example run for
  real, producing four labelled months whose bounds tile exactly; the label becoming the description;
  `selected` carried per row; an absent `selected` column pre-selecting nothing; a missing required
  column named rather than failing on an ordinal; a strategy with no column; a source-SQL strategy
  with no connection refusing plainly; date bounds written round-trippably; and DuckDB running with
  both connections null, which is the claim the whole default rests on.
- Playwright 40 — the full golden path including the backfill flow, green.
- Full backend suite green: 781 unit, 150 integration.

## Open questions

- ~~**Whether non-DuckDB previews run synchronously.**~~ They do; the warning moved to defaults.
- ~~**The C# contract's shape.**~~ `ISegmentingStrategy.ProposeSegments(SegmentingContext)`, with both
  connections and the source's columns.
- ~~**Wording and placement of the repeated-execution warning.**~~ Beside the strategy picker on the
  mapping editor, stating that the query runs on every scheduled pass and which side it queries.
- ~~**Whether static entries may mix with Auto or Custom.**~~ Not in the editor; see above.
- ~~**No UI yet for authoring the strategies themselves.**~~ Closed by phase 61, which built exactly
  the editor this named: phase 48's Checks card in shape, with the Test button, on the replication's
  Overview.
- **No integration test drives a source-SQL or target-SQL strategy against a live server.** The runner
  is shared and the DuckDB path covers the row-to-candidate translation, but the two connection-bound
  kinds are currently only covered by their refusal paths.
