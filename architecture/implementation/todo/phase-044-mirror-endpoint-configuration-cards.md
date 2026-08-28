# Phase 44 — Mirror endpoint configuration between Overview and Mapping (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/mirror-endpoint-configuration-cards.md`

## The gap

Two screens both configure "where does this replication read from and write to," and they look
unrelated to each other:

- `TableMappingForm.tsx` renders `MappingSide` (Source) and `MappingSide` (Target) as two separate cards
  side by side in a `.form-grid` — but the Source column also stacks the source-filter card directly
  beneath the Source `MappingSide` card, so the two columns are unequal height, and there's no connector
  between the Source and Target cards.
- `EndpointsCard.tsx` (Overview) puts both sides' fields inside **one shared card**, laid out as a
  3-column grid (`1fr 28px 1fr`) with the arrow as the middle column — not two cards at all.

## The fix

### `EndpointsCard.tsx`

Split into two independent cards (`Source`, `Target`), each holding its own `EndpointFields`, with an
arrow rendered between them as a connector — not a grid column inside a shared card. The card-level
"inherited by N mappings" note (currently on the single wrapping card) needs a new home: likely a small
header above the pair, since it describes the pair as a whole rather than either side individually.

### `TableMappingForm.tsx`

Move the source-filter card out of the Source column so it no longer stacks under just one of the two
endpoint cards. It becomes its own full-width row beneath the Source/Target pair. Add the same arrow
connector between the `MappingSide` (Source) and `MappingSide` (Target) cards that `EndpointsCard` gets.

### Shared visual treatment

Both screens should use the same card-pair-plus-arrow pattern — likely worth factoring into one small
shared component or a shared CSS class (e.g. `.endpoint-pair`) rather than reimplementing the equal-height
+ arrow layout twice, given the whole point is that they look identical.

### Table selection stays exactly as it is

No code change to `MappingSide`'s table controls. This phase is a container/layout change around
`MappingSide`, not a change to what's inside it. Verification (below) exists specifically to catch a
refactor that accidentally re-gates the table picker behind the override toggle while reorganizing the
surrounding cards.

## What this phase does not build

- Any change to override semantics, connection/database fields, or table-picker behavior.
- Any change to what `EndpointFields` or `MappingSide` actually configure — purely the card
  structure/layout around them.

## How to verify when built

- Overview's Source and Target render as two visually separate cards with an arrow between them.
- Mapping's Source and Target cards are equal height, with the source-filter card relocated below both
  rather than stacked under Source alone.
- An arrow connector appears between the Source and Target cards on both screens, visually consistent.
- The table picker on both Source and Target remains active/enabled independent of the override toggle's
  state — regression check against the invariant this phase must not disturb.
- Playwright screenshot comparison for both screens (existing golden-path screenshots already cover
  table-mapping-form and would need updating).
- Full suite green.

## Open questions

- Exact home for `EndpointsCard`'s "inherited by N mappings" note once the card splits in two.
- Whether the shared layout becomes a reusable component now or stays two close-but-separate
  implementations until a third consumer shows up — small enough to decide during implementation.
