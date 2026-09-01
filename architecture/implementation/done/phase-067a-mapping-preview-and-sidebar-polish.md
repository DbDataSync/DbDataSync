# Phase 67a — Preview/Verify's header keeps diverging, and the mappings sidebar's overflow (planned)

**Status**: Planned, not started
**Plan reference**: none — a small, discrete follow-up to phase 67, requested shortly after it landed.
Two independent UI fixes on the screens that phase touched; not new scope, no design discussion needed.

## 1. Preview SQL and Verify's header keeps diverging from the editor's

Phase 67 reconciled *part* of the header — `SavedMappingHeading.tsx` now renders the literal same
`EndpointSidePair`/`MappingSide`/`SourceFilterCard` components the editor does, disabled rather than
reimplemented, exactly as asked. What it didn't reconcile is the `page-head` above that and the
`SubTabs` bar's own data — both still computed three separate times, once per file, and two of those
three are wrong.

### The provisioning badge is the concrete bug

`mappingTabs.ts`'s Provisioning tab carries a `badge: pendingSteps` (`mappingTabs.ts:18`). In
`TableMappingForm.tsx`, `pendingSteps` is a live count — `useProvisioning(replicationName, existing?.name)`,
summed across both sides' plans (`TableMappingForm.tsx:140-141`) — and the file says exactly why, in its
own comment: *"The tab bar shows the badge whichever tab is open, so it asks for the plans itself rather
than only the Provisioning tab's card doing so"* (`TableMappingForm.tsx:137-139`).

`MappingPreview.tsx:54` and `VerificationPanel.tsx:69` both call `mappingTabs(base, mappingName, 0)` —
a **hardcoded 0**. Neither fetches `useProvisioning` at all. So a mapping with pending provisioning
steps shows the badge correctly from every tab reached through the editor's `Outlet`, and shows no
badge at all — silently wrong, not just inconsistent — the moment you're on Preview SQL or Verify. The
file comment's own stated intent ("whichever tab is open") is violated on exactly the two tabs that
don't go through that `Outlet`.

### The fix: move the computation into the tabs, not into a third and fourth copy of it

Turn `mappingTabs` from a plain function three call sites each feed data into, into a hook that computes
its own data — `useMappingTabs(replicationName, base, mappingName)` — fetching `useProvisioning`
internally and returning the finished `SubTab[]`. Every caller becomes `const tabs = useMappingTabs(...)`,
with no `pendingSteps` variable of its own to keep in sync. This is what "the same as the other tabs"
has to mean structurally: not three implementations that are supposed to agree, but one implementation
three screens call.

### The MAPPED badge is the other divergence, found by the same comparison

`TableMappingForm.tsx:168` shows a `<span className="badge badge-accent">MAPPED</span>` beside the name
whenever `existing` is set. Preview SQL and Verify's own `page-head`s (`MappingPreview.tsx:38`,
`VerificationPanel.tsx:48`) never show it — and every mapping either of those two screens can possibly
be open for **is** an existing, saved one (both require a `mappingName`), so the badge is always true
there and never shown. Add it to both, for the same reason the provisioning badge has to work
identically: an operator shouldn't see a different set of facts about the same mapping depending on
which tab happened to be open.

### What's legitimately different, and stays that way

The `right`-side actions are not a header inconsistency — they're different because the screens do
different things, and pretending otherwise would be the wrong kind of consistency:
- The editor's Cancel/Delete/Save buttons make no sense on a read-only screen.
- Verify's "Run checks" button is specific to Verify.
- The `page-note` line under the name (each screen's own one-liner — "what a pass would run," "source
  and target compared on demand") is deliberately screen-specific text, not a fact about the mapping.

Phase 67a's original "Back to the mapping" removal (see below) still applies as part of this: it's the
one `right`-side element that *was* pure duplication rather than a legitimate difference, since the
`SubTabs` bar already provides the same navigation.

### Remove "Back to the mapping" from Preview SQL and Verify

`MappingPreview.tsx:41` and `VerificationPanel.tsx:51` each have a `page-head` `<Link>` back to the
editor. Phase 67 gave both routes the same `SubTabs` bar the editor itself uses, and Notes is the
editor's index route — so clicking any tab other than Preview/Verify already navigates back into the
editor. The button is redundant now that the tab bar does its job. Delete the `<Link>` in both files;
nothing else in either `page-head`'s `right` div needs to move.

## 2. The mappings sidebar: name overflow, and the column count as a badge

`TableMappingsPanel.tsx`'s `MappingSidebarItem` (lines 68-80) renders:

```tsx
<NavLink to={to} className={...} data-testid={...}>
  {name}
  <span className="meta">{data ? `${data.columnMappings.length} cols` : '…'}</span>
</NavLink>
```

`.sidebar-item` (`index.css:212-218`) is a flex row with no `overflow`/`min-width` handling on its
children, so a long mapping name doesn't wrap or truncate — it pushes the row wider than the sidebar's
fixed 212px (`index.css:196`), and `.sidebar`'s `overflow: auto` (both axes) turns that into a
horizontal scrollbar on the whole sidebar rather than a contained overflow on one row.

**Fix**:
- Wrap `{name}` in its own `<span>` with `overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  min-width: 0;` — the `min-width: 0` is the part that's easy to skip and the reason the ellipsis
  wouldn't otherwise take effect inside a flex row (a flex item's default `min-width: auto` refuses to
  shrink below its content's natural width). Put the full name in a `title` attribute on that span, for
  the native browser tooltip.
- Change the column-count `<span className="meta">` to reuse the existing `.badge` class
  (`index.css:423-431` — already exactly a small bordered pill), keeping the right-alignment
  `.sidebar-item .meta` currently provides via `margin-left: auto` and adding `flex-shrink: 0` so the
  badge itself is never what gives way when the row is tight — only the name should ever truncate.
  Content becomes just the number (`{data.columnMappings.length}`, no "cols" suffix — a badge reads as
  a count on its own, the way `RunKindBadge` and friends already do elsewhere in this app).

## Out of scope

`ReplicationsPage.tsx`'s own `.sidebar-item` (its replications list, `ReplicationsPage.tsx:89-91`) has
the same unhandled-overflow shape — a bare `{n}` with no wrapping span — but wasn't part of what was
asked here and has no column-count badge to add either. Worth the same name-truncation treatment if
it's ever raised, not bundled into this one.

## How to verify when built

- A mapping with pending provisioning steps: the badge on the Provisioning tab shows the same non-zero
  count whether reached from the editor, Preview SQL, or Verify — not just present-vs-absent, the exact
  same number.
- The MAPPED badge shows next to the name on Preview SQL and Verify, same as the editor.
- Preview SQL and Verify no longer show a "Back to the mapping" control; every other tab still
  navigates back into the editor exactly as before.
- A mapping with a very long name: the sidebar shows an ellipsis, not a wider row or a scrollbar; hover
  reveals the full name; the sidebar's own width and the badge's position are unaffected.
- The column-count badge stays visible and un-truncated regardless of name length; it still reads
  correctly when a mapping has zero column mappings.
