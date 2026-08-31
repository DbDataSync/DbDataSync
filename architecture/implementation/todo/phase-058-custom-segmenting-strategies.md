# Phase 58 — Custom segmenting strategies for reload/backfill (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/custom-segmenting-strategies.md`

## What this covers

A new "Custom" segmenting mode alongside today's Full/List/Range/Auto, backed by a script (C#,
source-dialect SQL, or DuckDB SQL) that produces a labeled list of segments; a Backfill UI that presents
those as a checklist rather than queuing all of them automatically; and a **configured default
segmenting strategy per mapping**, so an operator doesn't re-specify the same mode/bounds/strategy at
every backfill.

## 1. `RangeSegment` gains an optional label

```csharp
public sealed record RangeSegment(string Column, string RangeMin, string RangeMax, string? Label = null)
    : BatchReloadSegment
{
    public override string Describe() => Label ?? $"{Column} [{RangeMin}, {RangeMax})";
}
```

A segment produced by a custom strategy carries the operator's chosen label (`"2024-03"`); every other
existing caller is unaffected — `Label` defaults to `null`, and `Describe()` falls back to today's
generated text exactly as before.

## 2. New script slot: segmenting strategy

**Four authoring paths** (revised 2026-08-30, adding Target SQL), same pattern as phase 43's verification
queries:

- **DuckDB SQL** — no source or target connection at all. The script's SQL runs against an ephemeral,
  in-memory DuckDB instance and must return exactly **four** columns: `label`, `range_start`, `range_end`
  (inclusive/exclusive, matching `RangeSegment`'s existing convention everywhere else), and **`selected`**
  (boolean — whether this candidate should be pre-checked/auto-included by default). A new, narrow use of
  DuckDB.NET in the codebase — a single read-only query against an in-memory instance, unrelated to and
  much smaller than `state-store-concurrency.md`'s "not adoptable yet" verdict for the state store.
- **Source SQL** — a query in the source's own dialect returning the same four logical columns, run
  against the source connection. Needs a `ParameterDescriptor`-declared column picker (phase 42) so the
  strategy knows which column it's segmenting, the same shape verification checks already use.
- **Target SQL** — the same shape as Source SQL, but run against the **target** connection instead. The
  reason to offer this at all: an operator may already maintain aggregate or metadata tables on the
  target side (a control table recording which periods need reloading, a rollup table cheaper to query
  than the live source) — segmenting driven by what the target already knows, not a fresh source scan.
- **C#** — a new script contract (peer to `sourceQueryBuilder`), given source *and target* metadata and
  connections, returning a list of segment candidates (label, range, `Selected`) directly — strictly more
  power than either SQL path, since it can read from either side or both.

**`selected` lives on the candidate record the strategy returns, not on `RangeSegment`/
`BatchReloadSegment` itself.** It's a property of what the strategy proposes; once a candidate is turned
into an actual segment to run (whether by an operator checking its box, or by a scheduled pass filtering
to `Selected == true`), the flag has done its job. No new field on the segment types from §1.

## 3. Backfill UI: checklist, not automatic queuing

`BackfillForm.tsx`:

- `Segment` mode dropdown gains a **Custom** option, alongside Full/List/Range/Auto — not a replacement
  for Auto, a fifth entry pointing at a bound segmenting-strategy script.
- Choosing a Custom strategy runs it and renders every proposed segment as a row with its label and a
  checkbox (pre-checked per the strategy's `selected` flags), plus a select-all. **DuckDB strategies
  preview synchronously** the moment one is picked (safe — no connection touched). Source-SQL/target-SQL/
  C# strategies may want an explicit "preview" action rather than running automatically on selection,
  since they do touch a real connection — decide during implementation (see Open questions); this is
  about a one-off interactive preview click, separate from §5's repeated-execution warning for a
  *default* binding.
- Submitting sends every **checked** segment in `segments: BatchReloadSegment[]` — the mutation already
  accepts a plural array (today's form just always builds a one-element one); the checklist path is the
  first real user of more than one.

## 4. A configured default segmenting strategy per mapping — a list, not a single segment

**Revised 2026-08-30**, reconciling with (and retiring) the old reader-option mechanism.
`TableMappingConfig` gains a field typed `IReadOnlyList<BatchReloadSegment>` — the same array shape
`SegmentSerializer.SerializeMany`/`DeserializeMany` already handle, and the same shape a Custom checklist
backfill submission already sends. One list-shaped config, three uses: the mapping's stored default, an
ad hoc backfill override, and (per §5) what a scheduled `BatchReload` pass consumes.

- **Empty/unset list means Full, no segmenting** — naming what already happens today (`BackfillForm`'s
  default state is already `mode: 'full'`), not a behavior change for a mapping that configures nothing.
- **New segment kind: `CustomSegment(string ScriptName)`**, added to the `BatchReloadSegment` hierarchy
  alongside `Full`/`List`/`Range`/`Auto`. A marker, consumed and expanded exactly once — the same pattern
  `AutoSegment` already establishes ("never persisted as, or handed to a reader/writer as, a runtime
  segment") — by running the bound strategy and substituting its `selected` candidates in its place.
- **`TableMappingForm.tsx`** gets a "Default reload segmenting" section: pick Full, Auto (one bucket
  config), or Custom (one strategy reference) as the *entire* list in one entry — or build a static list
  by hand, adding any number of `List` entries (each its own column + values) and any number of `Range`
  entries (each its own column + bounds), an add/remove row editor in the same spirit as
  `ColumnMappingEditor`'s list. This is what "lists of segment values and lists of segment ranges" means
  concretely, and it's what replaces the old hand-typed-JSON `segments` reader option.
- **`BackfillForm.tsx` pre-fills from the mapping's configured default list** when opened — every entry
  stays editable/removable for that one backfill, and editing there does not write back to the mapping's
  stored default (ad hoc means ad hoc).
- **Custom mode's default is the script reference, not a frozen segment list.** `CustomSegment` in the
  list is expanded fresh at reload time — a default pointing at a Year-Month DuckDB generator keeps
  tracking "now," not whatever it computed when the default was configured.
- **Retires the old mechanism, doesn't coexist with it.** `readerOptions[SegmentSerializer
  .SegmentsOptionKey]` stops being read by `ResolveSegmentsAsync` — see §5. Removed from the reader's
  options entirely; no longer something an operator hand-types into the generic KeyValueTable editor.

## 5. Scheduled reload from a strategy's `selected` candidates

**This is what makes `selected` more than a UI convenience** — a scheduled, self-updating reload: a
mapping whose reader is `BatchReload`, with a Custom default segmenting strategy that flags "the last N
months" as selected (recomputed relative to *today* on every run), gets reloaded automatically on the
replication's own schedule — no new scheduling concept needed, because the consuming mechanism this reuses
already runs on a schedule today.

**Reconciled 2026-08-30: one source, not two.** `RunExecutor.ResolveSegmentsAsync` (`DataSync.TaskRunner`)
today reads its scheduled `BatchReload` pass's segment list from `readerOptions[SegmentSerializer
.SegmentsOptionKey]`. That read is **replaced**, not supplemented, by reading **the mapping's
default-segmenting list** (§4) instead — expanding any `AutoSegment` (existing behavior) and any
`CustomSegment` (new: run the bound strategy, take its `Selected == true` candidates) in place, then
processing the resulting list exactly like today's static array already is: same loop, same "processing N
configured segment(s) in this pass" logging, same everything downstream. There is exactly one place this
is configured going forward.

**Resolved 2026-08-30: documented breaking change, no migration.** The old `segments` reader option
simply stops being read — no automatic conversion. Anyone using the old mechanism reconfigures by hand
into the new default-segmenting field; this is called out plainly in release notes rather than handled
silently.

**All four authoring paths are eligible for the scheduled path — resolved 2026-08-30, not restricted to
DuckDB.** A source-SQL, target-SQL, or C# strategy running on every scheduled pass means a real query
against a real connection every time the replication runs, but **that cost judgment belongs to the
operator, not to DataSync.** An operator may already maintain cheap aggregate/metadata tables on either
side specifically so this kind of query is fast — it isn't this tool's business to decide that on their
behalf and restrict the option pre-emptively.

**What DataSync owes instead is honesty about what's about to happen, not gatekeeping.** Wherever a
non-DuckDB strategy is bound as a mapping's *default* (the thing that actually runs unattended, on
schedule — see §4), the UI must say plainly, next to the strategy picker, that this query will be
**executed repeatedly, on the replication's schedule, against the source and/or target** — not a one-time
preview. This is a labeling requirement, the same spirit as phase 41's "live query against `X`" labeling
for script testing: make the consequence visible, then let the operator decide. No warning is needed for
DuckDB defaults, since nothing repeated there touches a real connection.

## What this phase does not build

- Any change to `Auto`'s existing even-bucket algorithm or its expansion timing.
- Foreign-key/relational awareness of any kind (unrelated to this phase; see phase 57 for the dev harness
  side of "no FK consideration").
- A replication-level default/override layer for segmenting — mapping-level only (see §4 and Open
  questions).

## How to verify when built

- A DuckDB strategy using `generate_series` produces Year-Month segments with the correct inclusive/
  exclusive bounds and labels, previewed with no connection opened.
- A source-SQL strategy against a real table produces the equivalent list, using a column picked via the
  declared `ParameterDescriptor`; a target-SQL strategy does the same against the target connection.
- The Backfill checklist shows every proposed segment, respects select-all, and queuing with a subset
  checked produces exactly that many `RangeSegment`s, each carrying its label through to the run's
  `SegmentLabel` in run history.
- A `RangeSegment` built the old way (Range mode, hand-typed bounds) still shows its generated
  `"{Column} [{min}, {max})"` description — confirms `Label` is additive, not a breaking change.
- A mapping with no configured default still backfills as Full when the operator changes nothing in the
  form — confirms the implicit default is unchanged.
- A mapping with a configured Range (or Custom) default opens the Backfill form pre-filled to it, and an
  operator can still override every field for a one-off reload without altering the mapping's stored
  default.
- A mapping defaulting to a Custom DuckDB strategy re-executes that strategy at each backfill, reflecting
  the current date/data rather than a frozen list from when the default was set.
- A DuckDB strategy's `selected` candidates start checked in the Backfill checklist; unflagged ones start
  unchecked.
- A `BatchReload`-reader mapping with a Custom default strategy, run on its normal schedule with no
  operator interaction, reloads exactly the `selected == true` segments each pass — and a strategy using a
  relative date (e.g. "last 3 months from today") produces a different concrete range on two runs taken a
  month apart, confirming it recomputes rather than caching.
- A mapping whose default is a source-SQL, target-SQL, or C# strategy shows the repeated-execution warning
  on its editor; a DuckDB default shows no such warning.
- **The old `segments` reader option is no longer read by `ResolveSegmentsAsync`** — a mapping that still
  has one set is not silently honored; it behaves as if unset (Full/no segmenting) until reconfigured
  through the new field, matching the documented breaking change.
- The default-segmenting editor supports adding multiple `List` entries and multiple `Range` entries in
  one mapping's default, each independently editable/removable, and persists/reloads that list correctly.
- A mapping's default list containing several static `Range` entries reloads exactly those ranges on
  schedule, matching what the old `segments` reader option would have produced for the same configuration.
- Full suite green.

## Open questions

- Whether source-SQL/target-SQL/C# strategy previews run synchronously like DuckDB's, or need an explicit
  preview step given they touch a real connection.
- Exact `segmentingStrategy` script contract shape for the C# path, now carrying both source and target
  metadata/connections.
- Exact wording/placement of the repeated-execution warning — a design detail, not a design question;
  the requirement (it must be visible on any non-DuckDB default) is settled.
- Whether the default-segmenting UI allows mixing hand-added static entries with Auto or Custom in the
  same list, or treats them as mutually exclusive choices.
