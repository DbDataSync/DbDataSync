# Mapping and overview UI polish

**Status: agreed, ready to implement — 2026-08-31 (revised).** A short list of small, independent SPA
fixes, none of which needs its own design discussion. Grouped here because they were raised together,
not because they share a mechanism; the ordering below is roughly cheapest-first, not priority.

---

# Outcome

Agreed, as `implementation/todo/phase-067-mapping-and-overview-ui-polish.md`.

## 1. Preview SQL and Verify are missing the mapping heading every other tab has

`TableMappingForm.tsx` renders, above its `SubTabs` bar: the `page-head` (mapping name + badges),
`EndpointSidePair` (Source/Target), and `SourceFilterCard` — see `TableMappingForm.tsx:166-235`. Every
tab reached through that editor's Outlet sees the same tables without having to re-establish them.

**Preview SQL** (`MappingPreview.tsx:34-42`) and **Verify** (`VerificationPanel.tsx:44-61`) are not
reached through that Outlet — per `mappingTabs.ts`'s own comment, they are separate routes the tab bar
links to (`to:`, not `path:`), specifically so they can show the mapping *as saved* rather than an
editor's unsaved draft. Both currently render only a bare `<h2>{mappingName}</h2>` plus a one-line note,
with no Source/Target/filter heading at all.

**Fix**: give both the exact same heading the editor renders — `EndpointSidePair`/`MappingSide` plus
`SourceFilterCard`, unchanged, fed from the loaded (saved) mapping. **No new read-only variant** — reuse
what's already there rather than designing a second presentation of the same information.
`VerificationPanel.tsx` already fetches the mapping (`useTableMapping`, line 29); `MappingPreview.tsx`
will need to start fetching it too.

## 2. "Setup" is stale — the tab is called Provisioning

`MappingSide.tsx:171` says a target that doesn't exist yet needs you to *"save the mapping and apply
the plan in Setup to create it"*. There is no tab called Setup — it's **Provisioning**
(`mappingTabs.ts:18`, `ProvisioningCard.tsx:172`). Fix the string. While touching this file, the same
stale name is in a code comment at `TableMappingForm.tsx:107` ("The Setup card plans against the
mapping as *saved*...") — not user-facing, but worth fixing in the same pass since it's the same
leftover rename.

## 3. Custom Transforms no longer needs to collapse

`ScriptBindingsCard` (`components/ScriptBindings.tsx:29-99`) starts collapsed unless something is
already bound, per its own doc comment (lines 22-27): *"a card reading 'no scripts bound' holding a
screen's best space is noise for everyone it does not apply to."* That was true when the card sat in a
flat stack of other cards competing for the same screen. It no longer applies — Custom Transforms is
now its own tab, both on the mapping editor (`mappingTabs.ts:16`) and on the replication Overview
(`OverviewPanel.tsx:30`, rendered by `CustomTransformsTab`, line 290) — so there's no stack to save space
in, and a tab an operator has already clicked into should just show its contents.

**Fix**: remove the `expanded`/`open` collapse state and the `▾`/`▸` toggle button (lines 41-42, 52-60);
always render the `card-body`. One component change fixes both call sites. Update or remove the doc
comment explaining the now-obsolete rationale.

## 4. Rename "Reload Segmenting" to "Backfill" — both places

Both tab labels change: the Overview's (`OverviewPanel.tsx:31`) *and* the mapping editor's
(`mappingTabs.ts:17`). Neither `SegmentingStrategiesCard` (the strategies themselves) nor the mapping's
own segmenting picker changes name or content — only these two tab labels.

Because both change together, `SegmentingStrategiesTab`'s doc comment (`OverviewPanel.tsx:314-318`) —
*"named exactly as the mapping editor's is... An operator who has seen [it] on a mapping and wants to
know where the names come from will look for the same words here"* — stays true; it just needs the
words in it updated from "Reload Segmenting" to "Backfill".

## 5. Pause/Resume: toggle-switch wording and control become pause/play

`ReplicationDetailPage.tsx:130-145`. Today this is a `.toggle` switch (the same control shape as the
Enabled toggle beside it) labelled "Paused" / "Not paused". Switching to pause/resume terminology with a
pause icon and a play icon fits better — a media-control shape describing an *action available*, not a
durable setting the way Enabled is.

**Fix**:
- Add `PauseIcon`/`PlayIcon` to `components/icons.tsx`, following that file's existing stroke-SVG
  convention (`currentColor`, `viewBox="0 0 16 16"`, `strokeWidth: 1.4` — see `CodeIcon`/`FlowIcon` for
  the pattern). `PauseIcon` is reused by item 6 below.
- Replace the `.toggle` control with an icon button: a pause glyph when not paused (clicking pauses), a
  play glyph when paused (clicking resumes) — the icon shows what clicking *does*, not the current
  state, which is the standard media-control convention.
- Labels become "Pause" / "Resume" rather than "Paused" / "Not paused" — action words for an action
  control.
- The click behavior itself is unchanged: it still opens `PauseDialog` to collect the required note
  before committing (`ReplicationDetailPage.tsx:136`, `:215-222`) — this item is only the control's
  shape and wording, not its logic.

## 6. Status card: one indicator, four states, three icons

`StatusCard.tsx:14-25`. Today the state indicator (`dot` + text) only ever reflects `status.running` —
`dot-ok`/"running" or `dot-idle`/"not running" — regardless of whether the replication is disabled or
paused. `enabled` currently only changes the *card's* own CSS class (line 18), not the state indicator;
`paused` is shown as a separate banner underneath (lines 32-39), never in the indicator itself. So
today it's possible for the indicator to say "not running" on a replication that's disabled, or on one
that's paused, or on one that's simply quiet between passes — three different situations reading
identically.

**Fix**: one indicator, evaluated in this priority order (a disabled replication can also be paused;
disabled is the more fundamental fact and should win):

1. **Disabled** — `!enabled`.
2. **Paused** — `status.paused`.
3. **Running** — `status.running`.
4. **Idle** — none of the above. Replaces today's "not running" wording.

Three icons, not four: **Running and Idle share the same icon** — only the label changes, from
"Running" to "Idle". Disabled and Paused each get their own; Paused reuses the `PauseIcon` added in
item 5, so pausing something and seeing it show as paused draw on the same glyph. Colors keep coming
from the existing tokens, applied to whichever icon is showing rather than to a plain dot:
`dot-bad`'s color for Disabled, `dot-warn`'s for Paused, `dot-ok`'s for Running, `dot-idle`'s for Idle
(`index.css:438-441` — Running and Idle differ in color and label; the icon shape is the only thing
they share).

`enabled` is already a prop this component receives (line 14) — it isn't currently read anywhere except
the outer class. The existing "Paused." banner and its note (lines 32-39) should stay; it explains *why*
in a way a one-word state label can't, and it's more visible reasoning than the indicator alone would
carry.

This fully replaces the separate "rename 'Not running' to 'Idle'" item from the first draft of this
doc — it's just the fourth branch's label here now, not a change of its own.

## 7. Give the Last 1h/24h/7d card the same top accent, and move it below Schedule

`StatusCard.tsx:18` and `ScheduleCard.tsx:26` both render `` `card ${enabled ? 'enabled' : 'disabled'}` ``,
which is what puts the 2px colored top border on them (`.card.enabled`/`.card.disabled`,
`index.css:239-240`, driven by `--accent-enabled`/`--accent-disabled`). `MetricsCard.tsx:28` renders
plain `className="card"` — no accent, and no `enabled` prop to key it from.

**Fix**:
- `MetricsCard` takes `enabled: boolean` (same as `StatusCard`/`ScheduleCard`) and applies the same
  `` `card ${enabled ? 'enabled' : 'disabled'}` `` className.
- In `ReplicationDetailPage.tsx:206-211`, reorder the rail so `MetricsCard` renders *after*
  `ScheduleCard` (currently Status → Metrics → Schedule; becomes Status → Schedule → Metrics), and pass
  it `enabled` from the same source the other two already use.
