# Custom segmenting strategies for reload/backfill

**Status: draft, 2026-08-29 — scoping against the current segment model before writing an implementation
phase.**

## The ask

For full and partial reload/backfill, let an operator define a **custom segmenting strategy** per table —
authored in C#, source-dialect SQL, or DuckDB SQL — instead of only the built-in modes. Worked example:
given a date column, segment by Year-Month.

## What exists today

`BatchReloadSegment` (`DataSync.Drivers.Abstractions`) is a closed, sealed hierarchy: `Full`, `List`
(explicit values), `Range` (half-open bounds on a column), and `Auto` (a request to split a column's
*actual* value range into N even buckets, expanded once via `ISegmentExpandingReader` at enqueue time —
never persisted or handed to a reader as a runtime segment).

**This is already the right extension point, and the Year-Month example is exactly what `Auto` can't
do.** `Auto` splits a value range into evenly-sized numeric buckets — it has no notion of a calendar
boundary. A custom strategy needs to *produce* a list of `List`/`Range` segments some other way (querying
distinct year-months actually present, say), not express something no existing segment kind can hold —
the output shape (`RangeSegment`, one per Year-Month with computed bounds) already exists. This is
additive: a new *source* of segments, consumed at the same enqueue-time point `Auto` expansion already
happens, not a new segment kind.

## Shape: a new script slot, same pattern as verification queries

This is structurally the same problem phase 43 solved for "how does an operator author a check" — and
the answer should be the same shape, now **four authoring options** (Target SQL added 2026-08-30):

- **C#** — a new script contract (peer to `sourceQueryBuilder`, `verificationQueryGenerator`): given
  source *and target* metadata and connections, return the concrete segment list. Full power — can
  inspect distinct values, compute arbitrary bucket logic, read either side, anything the operator's code
  can express.
- **Source SQL** — a query in the source's own dialect that returns segment boundaries directly (for the
  worked example: `SELECT YEAR(dt), MONTH(dt), MIN(dt), MAX(dt) FROM t GROUP BY YEAR(dt), MONTH(dt)`,
  or engine-equivalent), which DataSync maps into `RangeSegment`s. Runs where the data already is.
- **Target SQL** — the same shape, run against the target instead. The reason to offer it: an operator
  may already maintain cheap aggregate/metadata tables on the target side — a control table, a rollup —
  and segmenting driven by what the target already knows can be cheaper than a fresh source scan.
- **DuckDB SQL** — no source or target connection at all (see below).

**Whether running Source/Target SQL or C# repeatedly (on a schedule) is "too expensive" is explicitly not
DataSync's call.** An operator may have exactly the cheap aggregate/metadata access that makes this a
non-issue for their table — the tool's job is to make the repeated-execution consequence visible (see
"Strategy output gains a `selected` flag" below), not to restrict the option pre-emptively on their
behalf.

## DuckDB SQL: resolved — no source or target access at all

**Settled 2026-08-29.** The DuckDB option needs no connection to anything. It's a self-contained
generator: an admin who already knows a table's write pattern (say, "roughly one row a day since 2020,
evenly spread") writes a DuckDB SQL query using DuckDB's own functions — `generate_series`, date
arithmetic — that computes the segment list from that domain knowledge directly, with no source read at
all:

```sql
SELECT
    strftime(d, '%Y-%m')         AS label,
    d                            AS range_start,
    d + INTERVAL 1 MONTH         AS range_end
FROM generate_series(DATE '2020-01-01', DATE '2026-01-01', INTERVAL 1 MONTH) AS t(d)
```

This resolves the mechanism question this doc originally raised: there's no extract-then-query and no
federated attachment to design, because DuckDB never touches the source or the target. The query runs
against an ephemeral, in-memory DuckDB instance and nothing else.

**Output contract, fixed**: exactly three columns — `label`, `range_start`, `range_end` — one row per
proposed segment. Bounds follow the convention `RangeSegment` already uses everywhere else: **start
inclusive, end exclusive**.

**This is a new, but narrow, first use of DuckDB.NET in the codebase** — worth being explicit that it
does not reopen `state-store-concurrency.md`'s "not adoptable yet" verdict. That was about the state
store's need for `UPDATE`/`DELETE`/upsert over the Quack remote protocol; this is a single read-only
`SELECT` against an in-memory, ephemeral instance with no persistence and no `ATTACH` — a materially
smaller ask, evaluated on its own.

## Backfill UI: the computed segments become a checklist

**New, from the same refinement.** Once a strategy (any of the three — DuckDB, source SQL, or C#) has
produced its candidate list, the Backfill form doesn't queue all of them automatically. It **runs the
strategy, shows every proposed segment with its label and a checkbox, and lets the operator select any
number of them** before queuing — reload three specific months, not necessarily all six the strategy
proposed.

- Since DuckDB strategies touch no connection, running one to preview its output is cheap and safe to do
  synchronously the moment the operator picks it in the form — no background job, no polling.
- `useBackfill`'s request already accepts a plural `segments: BatchReloadSegment[]` (today's form only
  ever builds a one-element array); the checklist submits however many the operator checked, with no
  request-shape change needed.
- **The label needs to survive into the run.** Today's `RangeSegment.Describe()` auto-formats
  `"{Column} [{RangeMin}, {RangeMax})"` — a custom strategy's whole point is a human-chosen label like
  `"2024-03"` instead. `RangeSegment` needs an optional `Label`, used by `Describe()` (and therefore the
  run's `SegmentLabel`, shown in run history) when the segment came from a strategy, falling back to
  today's generated text when it didn't.

## A configured default per mapping, not just an ad hoc choice each time

**New, 2026-08-30.** Segmenting shouldn't be something an operator re-specifies at every backfill. A
table mapping gets a **configured default segmenting strategy** — any mode, including Custom pointing at
a bound script — that the Backfill form starts from. Ad hoc override stays fully available (pick a
different mode, different bounds, a different strategy, for one backfill without touching the mapping's
default); the default just removes the need to redo the same configuration every time for a table that's
always segmented the same way.

**The implicit default — when a mapping configures nothing — is Full, no segmenting at all.** This is
not a new behavior, it's naming what already happens: `BackfillForm`'s current default state is already
`mode: 'full'`. An unconfigured mapping keeps behaving exactly as it does today.

**Same shape, reused, not a second config format — and revised to be a list, not a single segment (see
"Reconciled 2026-08-30" below).** The default is stored as `IReadOnlyList<BatchReloadSegment>`, the same
array shape a Custom checklist submission and the old persisted `segments` reader option both already
use — `TableMappingConfig` gains one field for it. There's exactly one way to describe "how this table
segments," used both as the mapping's default and as an ad hoc override, and it can hold Full/Auto/Custom
as a single-entry default *or* any number of hand-added List/Range entries.

**Custom mode's default is a script reference, not a frozen list.** Storing "the default is these five
literal ranges" would go stale the moment a DuckDB strategy like the Year-Month example is meant to track
the present (a `generate_series` bounded at "today" needs re-evaluating each time, not baked in once).
The default for Custom mode is which strategy to run; the strategy still executes fresh every time a
reload actually happens, exactly as an ad hoc Custom choice already would.

**Where it's configured**: presumably a small section on the mapping's own editor
(`TableMappingForm.tsx`) using the same mode/field controls `BackfillForm` already has, since it's
literally the same config shape rendered in a second place.

**Resolved 2026-08-30: mapping-level only.** No replication-level default/override layer — a deliberate
scope decision for now, not an oversight.

## Strategy output gains a `selected` flag — and this is what powers a scheduled reload

**New, 2026-08-30.** A generated segment (from any of the four authoring paths) can flag itself as
selected by default: DuckDB/source-SQL output gains a fourth column, `selected` (boolean); the C# path's
return type gains the equivalent field. This is a property of the **candidate list a strategy proposes**,
not of `BatchReloadSegment`/`RangeSegment` itself — `selected` decides which candidates get turned into
real segments, then its job is done.

**Interactively**, this is just a better default for the Backfill checklist: the strategy's flagged
segments start checked, everything else starts unchecked, and the operator can still change any of it
before queuing.

**On a schedule, this is the actual point.** A mapping whose reader is `BatchReload`, configured with a
Custom default segmenting strategy that flags "the last N months" as selected (computed relative to
*today*, recalculated every time it runs), gives exactly the capability described: "a simple relative
date based ETL... reload of the last N months... on a schedule," with no new scheduling concept needed
at all — because **the mechanism this reuses already exists and already runs on a schedule.**

This is the resolution to the "overlap with the standalone persisted segment list" question raised above,
not a competing mechanism: `RunExecutor.ResolveSegmentsAsync` already, on every scheduled pass of a
`BatchReload` reader, looks for a segment list to process (today, only a static persisted one under the
`segments` reader option). It needs one more source to check: **a mapping's configured default
segmenting strategy, executed fresh each pass, filtered to its `selected` candidates.** Same consuming
code path, same schedule (the replication's own Continuous/Periodic setting — no second schedule to
design), same "process every segment in this pass" behavior already built — the only change is where the
segment list comes from before that loop runs: a static list the operator once pasted in, or a strategy
re-evaluated fresh every time.

## Reconciled 2026-08-30: one home, not two sources

**Supersedes the "two sources feeding the same mechanism" resolution above.** Rather than the static
persisted list living on in its old spot (a raw JSON blob under the reader's `segments` option, hand-typed
into the generic KeyValueTable options editor) alongside the new mapping-level default, **the static case
moves into the same structured default-segmenting config entirely, and the old reader-option mechanism is
retired.**

Concretely: the mapping's default becomes **a list of segments**, not a single one —
`IReadOnlyList<BatchReloadSegment>`, the exact same array shape `SegmentSerializer` already
serializes/deserializes for the old `segments` option (`SerializeMany`/`DeserializeMany` are unchanged;
only *where the array lives* changes). This is what "enabling lists of segment values and lists of
segment ranges" means concretely: the default-segmenting dialog gets a real add/remove editor over that
array — add any number of `List` entries (each its own column + values) and any number of `Range` entries
(each its own column + bounds) — replacing hand-typed JSON with the same structured editor the rest of
this phase already builds for Full/Auto/Custom.

**`RunExecutor.ResolveSegmentsAsync` reads from the mapping's default-segmenting field, full stop** — no
longer from `readerOptions[SegmentsOptionKey]`. That reader option goes away as a way to configure this;
segmenting configuration lives on the mapping, not buried in a stringly-typed options bag with no
dedicated editor.

**Resolved 2026-08-30: documented breaking change, no migration.** The old `segments` reader option
simply stops being read. No automatic conversion, no compatibility shim — this gets called out plainly in
release notes/changelog as something anyone using the old mechanism needs to reconfigure by hand into the
new default-segmenting field.

## Open questions

1. **Does a custom strategy replace `Auto`'s even-bucket algorithm as an option on the same picker, or is
   it a separate concept entirely** (segmenting *mode* = Full/List/Range/Auto/Custom, with Custom pointing
   at a bound script)? Leaning toward the latter, since `BackfillForm.tsx`'s existing `Segment` dropdown
   already enumerates modes this way — Custom is a natural fifth entry, not a replacement for Auto.
2. **Where's the parameter surface for source-SQL and C# strategies?** A Year-Month strategy over a real
   table needs to know which column to segment by — phase 42's parameter system (already used for
   verification checks' column pickers) is the obvious fit. DuckDB strategies need no such parameter,
   since they touch no table at all.
3. **Whether source-SQL/C# strategy previews also run synchronously in the form**, the way DuckDB safely
   can, or need an explicit "preview" step given they *do* touch a real connection — worth being more
   careful than DuckDB's default-safe case.
4. ~~Whether the mapping-level default replaces, sits beside, or subsumes the existing standalone-reload
   persisted segment list~~ **Superseded 2026-08-30: it replaces it outright.** One home
   (`TableMappingConfig`'s default-segmenting field), not two — see "Reconciled 2026-08-30" above.
5. ~~Migration for existing configs already using the old `segments` reader option~~ **Resolved: documented
   breaking change, no migration.**
6. **Whether hand-added static entries (List/Range) can mix with Auto or Custom in the same default**, or
   whether picking Auto/Custom is exclusive of adding static entries — the underlying array type doesn't
   forbid mixing; whether the UI should allow it is a smaller implementation-level call.

**Next step**: ready for an implementation phase doc, following phase 43's four-authoring-options
structure. DuckDB is fully specified; source-SQL, target-SQL, and C# need the small remaining questions
above settled during implementation rather than blocking the whole phase.

---

# Outcome

Agreed, as `implementation/todo/phase-058-custom-segmenting-strategies.md`.
