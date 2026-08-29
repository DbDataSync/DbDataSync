# Phase 47 — Fix grid-table row misalignment on content overflow (planned)

**Status**: Planned, not started
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
