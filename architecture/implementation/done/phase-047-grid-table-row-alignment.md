# Phase 47 — Fix grid-table row misalignment on content overflow

**Status**: Done
**Plan reference**: `architecture/planning/done/grid-table-row-alignment.md`

## The bug

`.grid-head`/`.grid-row` (`index.css`) is the shared "table" pattern used everywhere in the SPA — each
row is its own independent `display: grid` sharing the same `gridTemplateColumns` string, not one grid or
a real `<table>`. CSS grid items default to `min-width: auto`, so a cell whose content is longer than its
column's `fr` share grows that row's track past the intended width — and because every row is a separate
grid formatting context, only *that* row's columns shift, breaking alignment with every other row.
Reported against `RunsPanel`'s run history table, but the same shared CSS affects every grid-table in the
app.

## The fix

In `index.css`, constrain grid (and nested flex) cells so they can shrink below their content's natural
width, with ellipsis truncation instead of a silently-grown track:

```css
.grid-head > *, .grid-row > * {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
```

Audit cells that compose sub-elements in their own flex row (e.g. `RunsPanel`'s `RunKindBadge` cell,
`MappingSide`'s `Field`-wrapped rows) for the same `min-width: 0` need on that inner flex container —
`overflow: hidden`/`ellipsis` on the outer grid cell alone won't stop an inner flex child from refusing to
shrink first.

Check for any cell that legitimately wants to wrap rather than truncate (rare in this app's tables) and
give it an explicit opt-out class rather than let it inherit truncation by accident.

## What this phase does not build

- A move to a real `<table>` element or a single shared grid — the fix stays within the existing
  `.grid-head`/`.grid-row` pattern.
- Column-width customization/resizing — out of scope, this is strictly the alignment bug.

## How to verify when built

- A run history row with a long mapping name or error summary truncates with an ellipsis instead of
  widening its own row's columns; every row's column boundaries still line up with the header.
- Spot-check at least one other grid-table screen (e.g. the column mapping editor, the mappings sidebar)
  with a deliberately long value in one row, confirming the same fix applies there too.
- Full suite green, including updated Playwright screenshots for any table screenshot that had long
  content in a row.

## Open questions

- None — the fix is small, general, and the root cause is confirmed in the existing CSS.

---

# Retrospective

The plan's diagnosis was right and the fix is the four lines it wrote out. Three things worth recording.

## The test measures the tracks, not the cells

The first version of the Playwright check compared each cell's left edge against the header's, and
failed on a table that was perfectly aligned: the Remove button is `justify-self: end`, so its box sits
wherever its own width puts it inside its track. What the bug widened is the **track**, so that is what
the test reads — `getComputedStyle(row).gridTemplateColumns`, compared across the header and every row.

## The fix was reverted to prove the test would have caught it

A regression test that passes before the fix is a test of nothing. The CSS rule was temporarily removed
and the suite re-run: test 32 fails, everything else passes. Then it went back.

## Truncation is opt-out, not opt-in

`overflow: hidden` on the cell alone does not stop an inner flex child refusing to shrink, so
`.row` and `.editable-value` inside a grid row get `min-width: 0` too — the column mapping editor's
target cell is exactly that shape and is where a long value shows up first.

Wrapping is available as `.wrap` on a cell rather than as something a cell falls into by accident. A
cell that wraps is a row that is taller than its neighbours, which is the same alignment problem
arriving from the other direction.

The focus ring was the one thing worth checking before adding `overflow: hidden` to every cell: inputs
here use `outline-offset: -1px`, so the ring is drawn inside the border box and nothing clips it.

## Verification

- Playwright 32 — a 65-character target column name truncated with an ellipsis inside its cell, every
  row's resolved grid tracks identical to the header's in the column mapping editor, and the same check
  on the run history table this was reported against.
- Confirmed failing without the fix, as above.
- Full .NET suite green: 711 tests. Playwright: 34 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions

- None, as the plan said.
