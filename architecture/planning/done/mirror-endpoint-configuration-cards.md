# Mirror endpoint configuration between Overview and Mapping, and stop coupling table pickers to overrides

**Status: resolved 2026-08-28, refined same day with general UX items.**

## The note as given

Source table and target table dropdowns should always be editable and are never an override — they are
exactly what a mapping exists to configure, unlike connection and database, which a mapping only
overrides from the replication's endpoints. The endpoint configuration on the replication Overview page
and on a table mapping should visually mirror each other: separate cards per side, equal height, with an
arrow between them. That visual style should apply to the Overview page too.

## Table selection is never an override — confirm and preserve, not build

`MappingSide.tsx` already gets this right functionally: the `Table` field (both the plain `<select>` and
the `allowNewTable` schema/table inputs) is rendered outside the `overriding` conditional and is disabled
only by `!database`, never by the override toggle's state. Its own doc comment already says so: "The
table picker cascades from the *resolved* endpoint either way, so choosing a table works the same
whichever side of that toggle you are on."

This note's purpose is to make that an explicit invariant rather than an incidental fact, specifically
because the card restructuring below touches these same components — the risk is a refactor
accidentally re-gating the table control while reorganizing the surrounding layout, not that it is broken
today.

## Current state: the two screens don't match

- **Mapping page** (`TableMappingForm.tsx`): Source and Target already render as two separate
  `MappingSide` cards, side by side in a `.form-grid`. But the Source column also stacks a second card
  (the source-filter editor) directly beneath the Source `MappingSide` card, so the Source and Target
  columns are different heights, and there is no arrow between the two endpoint cards.
- **Overview page** (`EndpointsCard.tsx`): Source and Target are **not** separate cards — both
  `EndpointFields` blocks live inside one shared card, arranged with a 3-column grid
  (`1fr 28px 1fr`) and the arrow (`→`) as the middle grid column. One card with two sides inside it,
  rather than two cards.

Neither matches the target shape, and they don't match each other.

## The target shape, both places

- **Two separate cards**, one per side (Source, Target) — not fields sharing one card.
- **Equal height.** On the mapping page this means the source-filter card can no longer live stacked
  under just the Source card — it needs to move to its own row, full width, beneath the Source/Target
  pair, so the two endpoint cards are directly comparable and nothing skews one taller than the other.
- **An arrow between the two cards** — not a grid column inside a shared card, but a connector rendered
  between two independent card elements, the same visual treatment in both places.
- Applies to `EndpointsCard.tsx` (Overview) and the Source/Target pair in `TableMappingForm.tsx`
  (Mapping) alike, so an operator sees the same shape for "where does this replication/mapping read from
  and write to" regardless of which screen they're on.

## What this does not change

- Table selection behavior itself — already correct, per above.
- The override toggle on `MappingSide` — still governs connection/database only.
- Anything about `EndpointFields`'s actual fields (Connection, Database) beyond their container.

## Refinements (2026-08-28)

**The "inherited by N mappings" note stays, and appears on both cards, not just one shared header.**
The first pass suggested it needed a new single home once `EndpointsCard` splits in two. It doesn't need
consolidating — Source and Target can each be overridden independently by a given mapping, so the note is
genuinely per-side information (whether *this* side is inherited or overridden varies mapping to mapping),
and belongs on both cards rather than being said once above the pair.

**This becomes a shared component**, used by both `EndpointsCard` (Overview) and the Source/Target pair
in `TableMappingForm` (Mapping) — not two implementations kept in sync by hand. This is the direct fix for
"prevent future divergence": the reason the two screens drifted apart before is that there was never a
single piece of code responsible for both.

**Source and target each get a consistent accent color**, applied as a **top border accent** on their
card, and used the same way everywhere else in the application a side is shown (not just this card pair)
— so an operator builds an association between a color and "this is the source side" / "this is the
target side" that holds regardless of which screen they're on. Picking the actual two colors is an
implementation detail, not a planning one, but they need to be chosen once, centrally (a CSS
variable/token pair), not per-component.

**Remove the duplicated horizontal navigation.** `AppShell`'s vertical icon rail already links
Replications, Connections and Scripts. `SectionTabs` — a horizontal tab bar repeating the same three
destinations — is rendered by five separate pages (`ReplicationsPage`, `ConnectionsPage`, `ScriptsPage`,
`ConnectionEditPage`, `ScriptEditPage`) via `AppShell`'s `tabs` prop. One nav already covers this; the
second is pure duplication and should go. This is unrelated to the endpoint-card work itself but was
raised alongside it as a general UX cleanup, and is small enough to fold into the same phase rather than
spinning up a separate one.

---

# Outcome

Agreed, as `implementation/todo/phase-044-mirror-endpoint-configuration-cards.md`.
