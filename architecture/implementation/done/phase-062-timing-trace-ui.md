# Phase 62 — Surfacing the timing trace in the UI

**Status**: Complete
**Plan reference**: `architecture/planning/done/timing-trace-ui.md`

## What this covers

The frontend half of phase 59's opt-in timing trace: a toggle for `TraceTiming` on the mapping editor, and
an expandable per-run detail view in run history showing the seven timing columns phase 59 already writes.

## 1. `TraceTiming` toggle

`TableMappingForm.tsx` gains a plain on/off control for `TraceTiming`, alongside the mapping's other
settings (provisioning, default segmenting). No `INHERITED` badge — confirmed mapping-level only, no
replication layer, per phase 59.

## 2. `TaskRunRecord` type and run history detail

- `api/types.ts`'s `TaskRunRecord` gains the seven optional fields (`readerKind`, `readerTimeToFirstRowMs`,
  `readerLifetimeMs`, `stagingKind`, `stagingDurationMs`, `writerKind`, `writerDurationMs`) — confirm
  against the actual `RunsController` response shape (likely already serializing them; this may be a
  types-only change with no backend edit needed).
- `RunsPanel.tsx`: a run whose record carries timing data gets a small indicator; clicking/expanding that
  row reveals Reader/Staging/Writer Kind and duration inline. A run with no timing data (the common,
  untraced case) shows nothing extra — no empty columns added to the base table, avoiding phase 47's
  row-alignment problem for the common case.

## What this phase does not build

- Any backend change to phase 59's decorator, columns, or opt-in semantics.
- An aggregate/percentile dashboard — still out of scope, this is single-run detail only.
- A replication-level default/override layer for `TraceTiming`.

## How to verify when built

- Toggling `TraceTiming` on for a mapping, running it, and opening that run in history shows all seven
  values; an untraced run shows the plain row with no expansion affordance.
- `ReaderTimeToFirstRowMs <= ReaderLifetimeMs` is visibly true in the rendered detail (sanity-checking the
  display against phase 59's own invariant, not just the raw data).
- Toggling `TraceTiming` off stops new runs from carrying timing data, and old traced runs still display
  correctly (nothing retroactively breaks).
- Full suite green, including a Playwright flow: enable tracing, run, expand the row, see the numbers.

## Open questions

- None — scope is fully determined by what phase 59 already shipped.

---

# Retrospective

The smallest of the four, and it needed no backend change at all — which the doc suspected and left as
something to confirm. Confirmed: the API was already serializing every one of the seven values.

## The shape was nested, not flat

The doc lists seven optional fields to add to the TypeScript `TaskRunRecord`. The actual response
carries them as one nested `timing` object, because `TaskRunRecord.Timing` is a `RunTiming?` record —
and `TaskRunStore.ReadTiming` deliberately returns null when *every* column is null rather than an
all-null record, so that "was this run traced" is a yes or a no rather than something a caller has to
interrogate field by field.

The TypeScript follows the wire, so `timing: RunTiming | null`. That turned out to be the better shape
for the UI too: "does this run have an expander" is `r.timing`, one check, rather than seven fields
that could in principle disagree.

## Where the toggle went

Phase 64 landed first and gave the mapping editor tabs, so "near the other mapping-level settings" no
longer names a place. It gets **its own tab, Diagnostics**, after Provisioning and before Preview SQL.

The reason is what the other tabs are. Column Mapping, Custom Transforms, Reload Segmenting and
Provisioning all describe what the mapping *is* and what it will do — change any of them and the
replication behaves differently. Tracing changes nothing: the same rows move the same way, and the
only difference is that the run carries numbers about it afterwards. Filing a switch like that under
"Reload Segmenting" would put it under a heading that has nothing to do with it, and an operator
looking for it would find it by exhaustion rather than by reasoning.

Sitting beside Preview SQL and Verify is the right neighbourhood: those two are the other answers to
"what is this mapping actually doing", and both are also observation rather than definition. It is
also where the aggregate view phase 59 deliberately deferred would go, which is the argument against
the one real objection — that a tab holding a single toggle is thin.

## The detail expands; it does not become columns

The run history grid already has eight columns and the plan doc had already reasoned this through:
seven more would clutter every row of every replication to serve the rare mapping that opted in, and
would reintroduce the row-alignment pressure phase 47 fixed. So the timings open beneath the row, in a
panel that spans the full width and deliberately does not participate in the grid's column tracks —
which is what keeps an untraced row identical to the pixel.

Only a traced run gets a chevron at all. A disabled one on every line would be the whole table
advertising a feature it is not using, which is a worse cost than the inconsistency of some rows
having an affordance and others not — the rows genuinely differ.

## What the test found

The Playwright flow turned up a case the phase doc's verification list does not cover: **a pass that
reads no rows has a reader lifetime and no time-to-first-row.** The doc says to check
`ReaderTimeToFirstRowMs <= ReaderLifetimeMs` in the rendered detail, which presumes there is always a
number.

Null there means *there was no first row*, not "nobody measured" — and it is exactly the distinction
phase 59 kept the columns nullable for. A zero would have been a lie claiming the first row arrived
instantly. The panel renders it as an em dash and omits the share line, and the test asserts that
branch rather than assuming a number is always present.

## Decisions the phase doc left open

The doc says "None — scope is fully determined". These came up anyway:

- **One expanded run at a time.** This is read to answer a question about one pass, and several open
  at once would push the rest of the history off the screen.
- **Time to first row is also shown as a percentage of the reader's lifetime.** The two numbers exist
  to be compared — a slow first row is a source planning or queueing, and a fast first row with a long
  lifetime is volume or a slow consumer — and a reader who has to do that division by eye every time
  is being handed the raw data rather than the answer.
- **Staging shows how much of its duration came after the source was exhausted**, when that is more
  than about a tick. A provider that writes straight through as rows arrive tracks the reader's
  lifetime; one that does real work afterwards — uploading what it staged — does not, and that
  difference is the whole reason phase 59 records both numbers.
- **Durations format like the rest of the app** (`ms`, then `s`, then `m`), and null renders as an em
  dash rather than a zero.
- **The tab is not hidden for an untraced mapping.** It is where tracing is turned *on*, so hiding it
  until it was already on would be a control reachable only by hand-editing config, which is the
  situation this phase exists to end.

## Verification

- Playwright 42, new — the whole loop: the toggle off by default, turned on, saved and read back from
  the API; a pass run with tracing on; the traced run located by asking the API which run carries
  timing rather than assuming the newest row is it (this replication runs continuously and other
  mappings' passes land in the same table); the detail absent until expanded and then naming each
  stage and the component that produced its number; the zero-rows branch above; and tracing turned off
  again with the already-traced run keeping its numbers.
- No backend change, so no new backend tests. Full suite green: 839 unit, 153 integration, 44
  Playwright.

## Open questions

- ~~**Whether the API already serializes the seven values.**~~ It does, as a nested `timing` object.
- **Still no aggregate view.** Deliberate, and unchanged from phase 59: this is single-run detail. The
  question tracing is usually turned on to answer — "is this mapping slower than it was last week" —
  is comparative, and answering it needs the percentile view neither phase built. The Diagnostics tab
  is where it would go.
- **Nothing warns that tracing was left on.** It costs little, but "turn it on for the one table
  behaving oddly and turn it off afterwards" is advice in a hint rather than anything the product
  helps with.
- **A run's timing is not shown on the live run panel**, only in history — so watching a traced pass
  happen still means waiting for it to finish and then expanding its row.
