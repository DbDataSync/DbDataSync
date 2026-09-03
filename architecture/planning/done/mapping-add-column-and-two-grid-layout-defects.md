# Three defects found in use: adding a column back, and two grid rows that lie

**Status: resolved 2026-09-03 — see Outcome at the end.**

Three things reported together from ordinary use. They are unrelated in cause and this doc would
normally be three files per this folder's own splitting rule — kept as one because all three were
diagnosed and agreed in the same sitting and land in the same phase, so there is no least-understood
part holding the others hostage, which is what that rule exists to prevent.

## 1. After removing a column from the mapping, there is no way to add it back

The reported symptom is real, but the cause is not the obvious one — **the control already exists**.
`ColumnMappingEditor.tsx:234-261` renders an "Add target column…" select. What it offers is the
problem:

```js
const mapped = new Set(mappings.map((m) => m.targetColumn))
const unmapped = targetColumns.filter((tc) => !mapped.has(tc.name))
…
{unmapped.length > 0 && ( …the whole control… )}
```

`targetColumns` is the **target table's catalog**. So the picker can only ever offer a column that is
already on the target, and the entire control disappears when none are left. Remove a row whose target
column is *not* in the catalog — one provisioning was going to create, the rows that carry the `MISSING`
badge — and there is nothing left to add it back with.

**The wider gap it exposes is the more valuable finding.** A source table that gains a column cannot be
mapped to a new target column on an existing target *at all* through this editor, because the picker
has no way to express a name the catalog does not already have.

And the server can already do it. `AlterTargetTablePlanner.cs:79` emits `RenderAddColumn` for exactly
this case — a mapped column absent from the target — and phase 45 wired provisioning to alter an
existing target. **The capability is built; the editor is the only thing preventing anyone from asking
for it.** That makes this a small UI change rather than new function, which is not what the report
looked like at first.

## 2. A failed run's status does not line up with the others in its column

`RunsPanel.tsx:275-287` renders the status cell two different ways:

```jsx
{hasError ? (
  <button type="button" className="btn-link" …><StatusBadge status={r.status} /></button>
) : (
  <span><StatusBadge status={r.status} /></span>
)}
```

Both are grid items and both get blockified, so the usual suspect — inline vs block — is not it. The
difference is the **UA stylesheet**: `button` carries `text-align: center`, while the `span` inherits
`text-align: left` from `.grid-row`. The failed badge is centred in its cell; every other badge is left
aligned.

It is worth naming precisely because "make it a flex row" or "set align-items" would not fix it, and a
plausible-looking fix that changes nothing is how this kind of defect gets closed twice.

## 3. The monitoring screen's latency cell overlaps its neighbours

`MappingLagRow` is `className="grid-row tall"`, and the CSS is a **fixed** height, not a floor:

```css
.grid-row { height: 38px; align-items: center; }
.grid-row.tall { height: 40px; }
```

`LagCell` (`MonitoringPanel.tsx:135-196`) is a column flex stacking up to four things: the figure with
its `exact`/`estimated` badge, "N versions behind", and the "as of HH:MM:SS" line. That is roughly 50px
of content in a 40px box, and with `align-items: center` it spills above *and* below into the rows on
either side.

The row was sized when the cell had two lines. The `as of` line that phase 88 added is what pushed it
over — which is the ordinary way a fixed-height row becomes wrong: not when it is written, but when
something is added to it later and nothing complains.

`.grid-row.tall` cannot simply become `min-height`: `ReplicationsPage.tsx:54` uses the same variant and
is fine as it is.

---

# Outcome — resolved 2026-09-03

All three go to **`implementation/todo/phase-096-column-add-and-two-grid-layout-defects.md`**, as one
phase. Three separate phases for two CSS corrections and one control would be numbering ceremony, and
they share a single verification story: these are defects a test could have caught and did not.

Decisions taken:

1. **The add control offers new columns, not only catalog ones.** A free-text entry with the unmapped
   catalog columns as suggestions, always rendered rather than gated on there being unmapped columns
   left. This fixes the removal case as a side effect and closes the real gap — mapping a newly-arrived
   source column to a new target column — using provisioning machinery that already exists.
2. **The `MISSING` badge's wording has to change with it.** It currently reads as a warning ("This
   column is not on the target table"), which was right when that state could only be reached by
   accident. Once an operator can deliberately name a column that provisioning will add, the badge is
   reporting an intention, and phrasing it as a fault would be wrong.
3. **The status column levels *up*, not down.** Every status becomes a button opening a run-details
   popup, rather than every status becoming a plain span. The markup asymmetry was a symptom of a
   feature asymmetry — a failed run has somewhere to go and a successful one does not, though "how long,
   how many rows, where did the watermark land" is an ordinary question about a run that worked.
   `RunErrorDialog` becomes an adaptive `RunDetailsDialog` whose error block is conditional. Note that
   sameness alone does not fix the column: both buttons inherit the UA `text-align: center`, so the
   badges would agree with each other and still sit centred under a left-aligned header.
4. **A new row-height variant rather than a change to `.grid-row.tall`**, because that variant has a
   second user which is not broken.
5. **Both layout defects get a real assertion**, not a visual check — see the phase doc. A layout bug
   that is only ever confirmed by looking is a layout bug that comes back.
