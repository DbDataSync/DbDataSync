# Grid tables misalign when one row's content overflows its column

**Status: resolved 2026-08-28 — root cause found, fix is small and general.**

## The note as given

In the run history table, rows aren't consistent — if one row's content in a single column overflows the
column's default sizing, the layout goes jagged and stops looking like a table.

## Root cause

Every "table" in this app (`RunsPanel`'s run history included) is a `.grid-head` row plus a stack of
`.grid-row` `<div>`s, each independently `display: grid` with the same `gridTemplateColumns` string — not
one real `<table>`, and not one shared grid. CSS grid items default to `min-width: auto`, which lets a
track grow past its `fr` share to fit its content's intrinsic width when nothing constrains it. Because
each `.grid-row` is its own separate grid formatting context (a sibling `<div>`, not a row inside one
grid), a single row whose content is longer than its column's fr share grows *that row's* track — while
every other row, with shorter content, keeps the intended proportional width. The columns stop lining up
between rows, which is exactly "jagged and doesn't look like a table."

`index.css`'s `.grid-head`/`.grid-row` rules have no `min-width: 0` (or any overflow handling at all) on
the cell elements — the CSS grid default is doing exactly what it always does; nothing here was ever
constraining it.

## Why this is bigger than the run history table

`.grid-head`/`.grid-row` is the shared pattern behind every grid-table in the app — Run history is just
the one where it was noticed. The fix belongs in the shared CSS rule, once, rather than patched per
screen.

## The fix

Add `min-width: 0` to grid cell children so a track can shrink below its content's natural width, plus
sensible truncation so long content doesn't just get cut off invisibly:

```css
.grid-head > *, .grid-row > * {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
```

Some existing cells already compose multiple pieces in a `<span className="row">` (e.g. Runs' Kind
column, `MappingSide`'s `Field` — flex rows with a gap) — those need `min-width: 0` on the flex container
too, for the same reason a flex item defaults to not shrinking below its content size either. Elements
that intentionally want to wrap (rare in this app's tables) should get an explicit opt-out rather than
inheriting the truncation by accident.

---

# Outcome

Agreed, as `implementation/todo/phase-047-grid-table-row-alignment.md`.
