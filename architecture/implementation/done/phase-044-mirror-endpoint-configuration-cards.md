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

### A shared component: an endpoint-side card pair

One component — used by both `EndpointsCard` (Overview) and the Source/Target pair in
`TableMappingForm` (Mapping) — that renders two cards (Source, Target) with an arrow connector between
them, equal height. This is the direct answer to "prevent future divergence": the two screens drifted
apart because nothing made them share code before. Something like:

```tsx
<EndpointSidePair
  source={<MappingSide .../> /* or EndpointFields, per caller */}
  target={<MappingSide .../>}
/>
```

with the pair component owning the card shells, the arrow, the equal-height layout, and the accent
top-border color — callers supply only what goes inside each card.

### `EndpointsCard.tsx`

Adopts the shared pair component. The "inherited by N mappings" note **stays, and appears on both
cards** rather than consolidating into one shared header — Source and Target can be overridden
independently per mapping, so whether *this* side is inherited or overridden is genuinely per-side
information.

### `TableMappingForm.tsx`

Adopts the same shared pair component for its Source/Target `MappingSide` cards. The source-filter card
moves out of the Source column so it no longer stacks under just one side — it becomes its own full-width
row beneath the Source/Target pair, keeping the two endpoint cards directly comparable in height.

### Source/target accent colors

Two colors, chosen once as CSS variables/tokens (e.g. `--accent-source`, `--accent-target`), applied as a
top-border accent on each side's card via the shared pair component — and reused anywhere else in the
app a source/target side is shown, not just this card pair, so the color-to-side association holds
consistently. Picking the actual hex values is an implementation detail; the requirement is that they're
defined centrally once, not per-component.

### Table selection stays exactly as it is

No code change to `MappingSide`'s table controls. This phase is a container/layout change around
`MappingSide`, not a change to what's inside it. Verification (below) exists specifically to catch a
refactor that accidentally re-gates the table picker behind the override toggle while reorganizing the
surrounding cards.

### Remove the duplicated horizontal navigation

`AppShell`'s vertical icon rail (`nav.rail`) already links Replications, Connections, and Scripts.
`SectionTabs` — a `tabbar` rendered via `AppShell`'s `tabs` prop — repeats the same three destinations
horizontally, and is used by `ReplicationsPage`, `ConnectionsPage`, `ScriptsPage`, `ConnectionEditPage`,
and `ScriptEditPage`. Remove `SectionTabs` and its usages; `AppShell`'s `tabs` prop and the `.tabbar`
rendering become dead once nothing passes it, and can go too unless something else ends up using the
slot. Unrelated to the card work but small enough to fold into the same phase rather than spinning up a
separate one.

## What this phase does not build

- Any change to override semantics, connection/database fields, or table-picker behavior.
- Any change to what `EndpointFields` or `MappingSide` actually configure — purely the card
  structure/layout around them.
- A broader design-token pass beyond the two new source/target accent colors.

## How to verify when built

- Overview's Source and Target render as two visually separate cards with an arrow between them, each
  showing its own "inherited/overridden" note.
- Mapping's Source and Target cards are equal height, with the source-filter card relocated below both
  rather than stacked under Source alone.
- Both screens render the pair via the same shared component — not two hand-synced implementations.
- Source and target cards each show their accent-colored top border, consistently between the two
  screens and anywhere else a side is rendered.
- The table picker on both Source and Target remains active/enabled independent of the override toggle's
  state — regression check against the invariant this phase must not disturb.
- The horizontal `SectionTabs` bar is gone from all five pages that had it; the vertical rail remains the
  only way to navigate between Replications/Connections/Scripts.
- Playwright screenshot comparison for both screens (existing golden-path screenshots already cover
  table-mapping-form and would need updating, along with any screenshot that shows the now-removed tab
  bar).
- Full suite green.

## Open questions

- The actual accent color values for source and target.
- Whether `AppShell`'s `tabs` prop is removed outright or just left unused if any future screen might
  want a secondary (non-section) tab bar — small enough to decide during implementation.

---

# Retrospective

Built as planned, and small. The interesting part was deciding what the shared component owns, because
that decision is the thing that stops the two screens drifting apart again.

## The pair owns the shell, the callers own the contents

`EndpointSidePair` renders the two card shells, the arrow between them, the equal-height grid and the
side accent. It does **not** render what goes inside, because that is genuinely different: the
Overview configures an endpoint (connection, database), the mapping editor configures a whole side
(endpoint, override toggle, schema, table).

Trying to unify the contents as well would have meant a component with a mode switch, which is two
components with extra steps. Sharing only the part that was drifting is the part worth sharing.

`EndpointSideCard` is separate from the pair for the same reason: the Overview needs a per-side
"inherited by N mappings" note in the head, the mapping editor needs an override toggle there, and
neither belongs to the other. The head is a slot.

## The note is per side because the override is

The plan called this and it is worth restating: consolidating "inherited by the 1 mapping unless
overridden" into a shared header would read as a fact about the pair. It is not — a mapping overrides
source and target independently, so which side is currently the inherited one is per-side information
and belongs on the side.

## The filter was making the cards incomparable

The source filter card sat stacked under Source, so the two columns were different heights — which
defeats the reason for putting them next to each other. It is its own full-width row beneath the pair
now. It still belongs to the source and says so; it just no longer hangs off one card.

## Two colours, defined once

`--accent-source` and `--accent-target`, as a 2px top border rather than a tinted card background: the
cards sit side by side and a wash of colour over a whole card competes with the controls inside it.
Deliberately not the accent green, which already means "this is the active thing".

They are applied through the shared card, and reused on the Setup card's two provisioning panels —
where the *colour* carries over but the arrow does not, because "enable change tracking on the source"
and "create the target table" are two setup steps rather than a flow from one into the other. An arrow
there would say something untrue.

Stacked under 900px the arrow turns from → to ↓ rather than disappearing, since it is the thing that
says which way the data moves.

## SectionTabs is gone; the tabs slot is not

The open question was whether `AppShell`'s `tabs` prop goes with it. It stays, for two reasons that
were only visible once the section links were removed: the replication detail screen passes its own
tabs (Overview / Table Mappings / Runs / Version Control), which are real sub-navigation rather than
duplicated section nav; and the connection and script editors use the same slot for their action
buttons. What was duplicated was the three section links, and only those are gone.

## Verification

- Playwright 22 — both screens rendering `.side-pair` with `data-side` source and target cards and an
  arrow, which only the shared component produces; the per-side inherited note on both cards; the two
  accents differing and the mapping editor's matching the Overview's; the two mapping cards within 2px
  of each other in height; the filter present on the page but not inside either card; the provisioning
  panels carrying the same colours; and the section tab bar absent from all four screens that had it
  while the rail remains.
- The regression check the plan asked for specifically: the source table picker is enabled both while
  the side is inheriting and while it is overriding. This is a layout change around `MappingSide`, and
  the way to get it wrong is to re-gate the picker behind the override toggle while moving cards.
- Playwright 04b updated: it asserted the Overview's first card title was the literal word "Endpoints",
  which the pair replaced with Source and Target. It now asserts the first card in the form *is* the
  source side, which is the ordering it was always about.
- Full .NET suite green: 565 tests. Playwright: 24 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions, both answered

- ~~**The accent colour values.**~~ `#2a6f97` and `#8a5a2b` — a blue and a brown, distinguishable from
  each other and from the accent green, and defined once in `index.css`.
- ~~**Whether `AppShell`'s `tabs` prop is removed.**~~ Kept; two things still use it, above.
