# Phase 193S — Query-source preview redesign: max-rows choice, capped reads, and the stale-metadata guard

**Status**: Not built.
**Plan reference**: `phase-190S-source-table-spec-query-and-allow-subquery.md` (decision 6: metadata for a
query-shaped source comes only from the existing preview flow). This phase extends that flow rather than
replacing it.

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
