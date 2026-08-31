# Phase 67 — Mapping and overview UI polish (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/mapping-and-overview-ui-polish.md`

## What this covers

Seven small, independent SPA fixes to the tabbed mapping editor and replication Overview phase 64 built,
surfaced after using the real thing. None needs its own design discussion — the plan doc already carries
exact file:line references and the fix for each. Order below matches the plan doc (cheapest first, not
priority) and each item is independently shippable/committable.

1. **Preview SQL and Verify get the same mapping heading every other tab has** — `EndpointSidePair`/
   `MappingSide` plus `SourceFilterCard`, fed from the loaded (saved) mapping, reusing the existing
   components rather than a new read-only variant. `VerificationPanel.tsx` already fetches the mapping;
   `MappingPreview.tsx` needs to start.
2. **Fix the stale "Setup" name** — `MappingSide.tsx:171`'s user-facing string and
   `TableMappingForm.tsx:107`'s comment both say "Setup"; the tab is "Provisioning."
3. **`ScriptBindingsCard` stops collapsing** — remove the `expanded`/`open` state and toggle button; it's
   its own tab now (both on the mapping editor and the Overview), so the "save space in a stack" reason
   for collapsing no longer applies. Update/remove the now-obsolete doc comment.
4. **Rename "Reload Segmenting" → "Backfill"** — both tab labels (Overview and mapping editor). No
   content or component changes, and `SegmentingStrategiesTab`'s doc comment needs the word swap to stay
   accurate.
5. **Pause/Resume becomes pause/play iconography** — new `PauseIcon`/`PlayIcon` in `components/icons.tsx`
   (matching the existing stroke-SVG convention), replacing the `.toggle` switch with an icon button
   (pause glyph when active → clicking pauses; play glyph when paused → clicking resumes), labels become
   "Pause"/"Resume". The click behavior (opening `PauseDialog` for the note) is unchanged — control shape
   and wording only.
6. **`StatusCard` gets one indicator with four states**, priority order Disabled → Paused → Running →
   Idle (disabled wins over paused, since it's the more fundamental fact). Running and Idle share one
   icon (label-only difference); Disabled and Paused each get their own, Paused reusing item 5's
   `PauseIcon`. Colors from the existing `dot-*` tokens, applied to whichever icon shows. The existing
   "Paused." banner/note stays — it explains *why* in a way the indicator alone can't.
7. **`MetricsCard` gets the same enabled/disabled top accent as `StatusCard`/`ScheduleCard`**, and moves
   below `ScheduleCard` in the rail (Status → Schedule → Metrics, was Status → Metrics → Schedule).
   Needs an `enabled: boolean` prop, passed from the same source the other two cards already use.

## What this phase does not build

- Any change to what these components actually configure or compute — labels, icons, collapse behavior,
  card ordering, and accent styling only.
- Any change to `PauseDialog`'s logic or the pause/resume API — item 5 is control shape/wording only.

## How to verify when built

- Preview SQL and Verify show the same Source/Target/filter heading as every other mapping tab, reading
  the saved mapping (not an unsaved draft).
- No user-facing or comment reference to "Setup" remains where "Provisioning" is meant.
- Custom Transforms (both places) renders its content immediately, no collapse/expand toggle.
- Both "Reload Segmenting" labels read "Backfill"; the underlying strategies/picker are unchanged.
- The Pause/Resume control shows a pause icon when active and a play icon when paused, labeled
  accordingly, and still opens the note dialog on click.
- `StatusCard`'s indicator correctly resolves all four states in priority order — a disabled-and-paused
  replication shows Disabled, not Paused.
- `MetricsCard` shows the same top accent as its neighbors and renders after `ScheduleCard` in the rail.
- Full suite green, including updated Playwright screenshots for the replication chrome and both tabbed
  screens.

## Open questions

None — the plan doc is fully specified with file:line references for every item.

---

# Outcome

All seven items built, in three commits (labels/collapse, then the two controls plus the card order,
then the shared heading). Three judgment calls the plan doc left open:

1. **The heading on Preview SQL and Verify is disabled, not merely reused.** The plan asked for the
   editor's own `EndpointSidePair`/`MappingSide`/`SourceFilterCard` and explicitly no read-only
   variant — but those components are pickers, and rendering live pickers whose edits go nowhere on a
   screen about the mapping *as saved* invites somebody to change a connection and believe they did.
   They are rendered unchanged inside a disabled `<fieldset>` in the new `SavedMappingHeading`: same
   components, no second presentation to keep true, and visibly not an editor. `SourceFilterCard` moved
   out of `TableMappingForm.tsx` into its own file so both callers can reach it.
2. **The status indicator's third and fourth icons are `DisabledIcon` (circle-slash) and `PulseIcon`
   (shared by Running and Idle).** The plan settled the states, the priority order and the colours but
   named only `PauseIcon`; these two follow the same stroke-SVG convention.
3. **The pause control keeps `data-testid="paused-toggle"` and `aria-pressed`** even though it is a
   button now rather than a switch — it is still a toggle button in the ARIA sense, and the name is
   what the Playwright suite reaches for.

Two pieces of now-dead scenery went with the changes: `.toggle.held` in `index.css` (nothing wears it
since the pause switch became a button) and `script-bindings-toggle`, whose Playwright assertions were
rewritten to expect the slots rendered immediately rather than after a click.
