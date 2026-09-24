# Phase 193S — Query-source preview redesign: max-rows choice, capped reads, and the stale-metadata guard

**Status**: Built. See Retrospective.
**Plan reference**: `phase-190S-source-table-spec-query-and-allow-subquery.md` (decision 6: metadata for a
query-shaped source comes only from the existing preview flow). This phase extends that flow rather than
replacing it.

**Scope note (added after 191S landed)**: this phase's own scope now also absorbs two items originally
assigned to 191S and deliberately deferred here instead — retiring `RawQueryReader`/`DuckDbQueryReader`/
`QuerySegmentTokens` and their `"Query"`/`"DuckDbQuery"` Kind registrations, and relocating the frontend's
query-text field from a reader option to `SourceTableSpec.Query`. Both touch the same `QuerySourcePanel.tsx`
surface this phase's max-rows/retry/stale-guard work already has to touch, so they land in one pass rather
than two. See 191S's own Retrospective for why.

## Why

190S deliberately keeps the existing preview mechanism (`ScriptTestService.PreviewQueryAsync` →
`queryColumns` → `TableMappingForm.tsx`'s `captureFor`/save logic) as the *only* way a query-shaped source's
metadata gets captured — no new server-side describe mechanism. That mechanism has three gaps once it's the
sole source of truth for something save-time validation and reads now depend on: it can read through a
large source query's entire result just to show a sample or capture shape, it has no graceful recovery when
a query can't be wrapped in a subquery, and it has no guard against an operator saving a mapping whose query
text has silently drifted from whatever was last actually previewed.

## Decisions made (asked, not guessed)

1. **A fixed max-rows choice — `{0, 10, 50}`** — replaces `ScriptTestService`'s current `MaxLiveRows =
   20`/`Math.Clamp(request.SampleRows, 1, MaxLiveRows)` shape. `0` means "shape only, no data" — this
   directly serves metadata capture, with no separate describe mechanism needed.
2. **When `AllowSubquery` is in effect, apply the cap via a real SQL clause** on the outer wrap
   (`TOP(n)`/`LIMIT n`, or `WHERE 1=0` for `n=0`). **Regardless of wrapping**, the reader always stops
   consuming from the `DbDataReader` after `n` rows and disposes the reader/command/connection — the only
   real safety net when `AllowSubquery` is off and no SQL-level clause is possible.
3. **No auto-retry on a wrapped-preview failure.** Show the engine error plus a hint, plus an explicit
   one-click **"Retry without subqueries"** button that flips the draft's `AllowSubquery` to `false` and
   reruns the preview unwrapped, in one click — never automatic.
4. **A stale-metadata guard at save time.** Track whether the draft's current query text matches the text as
   of the last successful preview. If it doesn't, and the operator tries to save, force a blocking
   confirmation dialog — *"I understand that my changes haven't been validated, and this will be operating
   on previously captured metadata"* — offering both a "run preview instead" path and an explicit "save
   anyway" acknowledgment. Never a silent save on stale metadata.
5. **This guard is specific to query-shaped sources**, which have no other way to know their own shape. A
   relationship's or a table's own metadata staleness continues to use the existing Cached Metadata card's
   "last captured" signal — no equivalent hard gate is added there by this phase.

## What this phase builds

- `ScriptTestService.PreviewQueryAsync`'s max-rows parameter (the `{0, 10, 50}` choice, replacing
  `MaxLiveRows`/`Math.Clamp`), and its wrapping-aware limiting clause construction.
- The reader-side stop-after-`n`-rows-and-dispose behavior, applied unconditionally (both wrapped and
  unwrapped).
- `QuerySourcePanel.tsx`/`QueryEditorDialog`'s failure-state UI: the engine error, a hint that
  `AllowSubquery` may need to change, and the one-click "Retry without subqueries" button (flips the draft's
  `AllowSubquery` and reruns unwrapped).
- `TableMappingForm.tsx`'s stale-check — comparing the draft's current query text against the text as of
  the last successful preview — and the save-time blocking confirmation dialog with its two exits.

## What this phase does not build

- Any change to how relationship metadata staleness is signaled (unchanged — the existing Cached Metadata
  card).
- Any change to the underlying capture mechanism itself (`queryColumns`/`captureFor`) — this phase only adds
  the max-rows choice, the failure UI, and the staleness guard around the existing flow.

## Open questions

1. **Exact state shape for tracking "query text as of the last successful preview."** Most likely a new
   `TableMappingForm` state field (a string snapshot compared against the current draft query), but not
   traced against the actual component this session.
2. **Whether the "Retry without subqueries" affordance should also appear inline in the stale-metadata
   confirmation dialog**, for the case where the reason a query stopped matching its last preview was itself
   an edit made to work around a subquery-compatibility failure — not decided.

## How to verify

- Playwright coverage for: a fresh preview at each of the three max-rows choices; a wrapped preview that
  fails, the retry button flipping `AllowSubquery` and succeeding unwrapped; a save attempt on a mapping
  whose query was edited after its last successful preview, being blocked, with both exits (preview-instead,
  save-anyway) verified to actually work.
- A unit test for the reader-side stop-after-`n`-rows-and-dispose behavior specifically with
  `AllowSubquery = false` (no SQL-level cap available, so this is the only thing bounding the read).

## Retrospective

Built in full, including both items absorbed from 191S. The backend half (max-rows/wrap/retire) landed as
its own commit before this one; see that commit's message for `SqlDialect.RenderRowLimit`, the `limit+1`
wrapping fix, and the retirement of `RawQueryReader`/`DuckDbQueryReader`/`QuerySegmentTokens`.

**Frontend, concretely**: `SourceTableSpec` (TS) gained `query`/`allowSubquery`. `querySource.ts` is deleted
outright rather than adapted — once "is this a query source" became a plain field on the source itself
(`source.query !== null`, an explicit null check, not truthiness, since an empty-but-chosen query must
still count as query-shaped), the whole reader-Kind-lookup module it contained had nothing left to do.
`MappingSide.tsx` gained a real, direct switch — "Custom query, not a table" — where none existed before:
today's design no longer has a reader-Kind picker driving this, so the operator needs a first-class way to
declare a source query-shaped at all, not just edit one that already is. `QuerySourcePanel`'s open/closed
state moved from local to controlled (`open`/`onOpenChange`), so the new stale-metadata dialog's "run
preview instead" exit can reopen the same popup rather than only being able to say so in words.
`QuerySourcePanel`/`QueryEditorDialog` gained the max-rows `<select>` (0/10/50), the `AllowSubquery` toggle,
and the one-click retry button — which reuses `PreviewGrid`'s own existing `${testId}-error` rendering for
the message itself rather than duplicating it (a real duplicate-banner mistake caught before it shipped,
not after).

**A real robustness gap found and fixed while wiring this up, unprompted by any test failure**: a mapping
saved before this phase has neither field in its persisted JSON at all, despite the TypeScript type now
claiming both are always present. `TableMappingForm`'s initial `source` state normalizes defensively
(`query ?? null`, `allowSubquery ?? true`) exactly once at load, so every setter downstream can trust a
real value instead of `undefined` masquerading as a boolean.

**The stale-metadata guard** (`StaleQueryConfirmDialog`, a local component in `TableMappingForm.tsx`,
modeled on `MappingReadStateDialog`'s `DataLossConfirm`): tracks `lastPreviewedQuery` state, seeded from the
saved mapping's own query text on load (an existing save is assumed to have *some* captured metadata behind
it, whether from a real preview or an earlier confirmed stale save — the best available signal without new
server-side tracking). `handleSubmit` blocks on `source.query !== lastPreviewedQuery` and shows the exact
wording agreed earlier in this design's own conversation, with both exits wired for real: "run preview
instead" reopens the query dialog via the same controlled `open` state; "save anyway" proceeds.

Open question 2 is resolved in the design's own favor from the conversation that specified it: the retry
button stays inside the query editor only, not duplicated into the stale-metadata dialog — the two guard
different problems (a query that can't be wrapped, vs. one that hasn't been re-validated) and conflating
them would blur which risk is actually being acknowledged.

**Verified for real, not just type-checked**: `tsc -b` and `oxlint` clean (no new warnings in any touched
file). `vitest run` 86/86 (all pre-existing). `duckdb-query-source.spec.ts` rewritten for the new fixture
shape and extended with four new tests (toggling into/out of query mode, the retry-without-subqueries
button actually flipping `AllowSubquery` and succeeding, the max-rows choice reaching the request, and the
full stale-guard flow including both exits) — all 8 tests run against a real Chromium browser and a real
`dotnet`-hosted API server, not mocked at the component level. The three sibling mapping-editor specs
(`mapping-relationships`, `mapping-column-add`, `mapping-metadata-cache`) and the full 46-test
`golden-path.spec.ts` all re-run green after these changes, confirming nothing else in the mapping editor
regressed.
