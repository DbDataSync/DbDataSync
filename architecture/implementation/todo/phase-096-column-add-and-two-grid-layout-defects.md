# Phase 96 — adding a target column, and two grid rows that lie

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/mapping-add-column-and-two-grid-layout-defects.md`,
resolved 2026-09-03.

Three defects reported from ordinary use. Unrelated in cause, one phase because two are CSS corrections
and the third is one control, and they share a verification story: each is something a test could have
caught and none did.

## 1. The add-column control offers only columns the target already has

`ColumnMappingEditor.tsx:234-261` already renders an "Add target column…" select. It offers
`targetColumns.filter(tc => !mapped.has(tc.name))` — the **target catalog** minus what is mapped — and
the whole block is gated on that list being non-empty.

Two consequences, the second worse than the reported one:

- Remove a row whose target column is not in the catalog (the rows carrying the `MISSING` badge, which
  provisioning was going to create) and there is nothing left to add it back with.
- A source table that gains a column **cannot be mapped to a new target column at all** on an existing
  target, because the picker cannot express a name the catalog does not already have.

`AlterTargetTablePlanner.cs:79` already emits `RenderAddColumn` for precisely this case, and phase 45
wired provisioning to alter an existing target. The capability is built. This is a UI change that lets
someone ask for it.

### What changes

The select becomes a **text input with a `<datalist>`** of the unmapped catalog columns. One control:
type any name, or pick a suggestion. No mode switch between "existing" and "new", because the two are
the same act from the operator's side and the difference — whether provisioning has to add it — is
already shown by the badge on the resulting row.

**Always rendered**, not gated on `unmapped.length > 0`. A new column can always be added, so the
condition that hid the control has no remaining justification.

Validation before the row is added, all three surfaced inline rather than silently ignored:

- a name already used as a `targetColumn` in this mapping is rejected — duplicate target columns are a
  config error, and the resulting mapping would fail at staging with a message about neither row
- trimmed, and empty rejected
- unchanged otherwise: the new row takes the same-named source column as its suggestion when one exists,
  else the first source column, exactly as the existing Add button does

### The `MISSING` badge has to be re-worded

`ColumnMappingEditor.tsx:194-196` renders `MISSING` with the title *"This column is not on the target
table."* That was right when the state could only be reached by accident. Once it is reachable
deliberately, the badge is reporting an **intention**, and phrasing it as a fault would be wrong.

It becomes `WILL ADD` (matching the existing `RENAMED` badge, which already names a pending provisioning
action rather than a defect), titled to say provisioning will add the column when applied. Where no
alter plan can cover it — `AlterTargetTablePlanner` reports `unmappable` for a column with no
cross-engine type mapping — the honest badge is still a fault, so that case keeps warning wording.

## 2. A failed run's status is centred; every other status is left-aligned — and only failures open

`RunsPanel.tsx:275-287` makes the grid item a `<button>` for a failed run and a `<span>` for everything
else. Both blockify as grid items, so the cause is not inline-versus-block: the **UA stylesheet** gives
`button` `text-align: center`, while the `span` inherits `text-align: left` from `.grid-row`.

Worth stating because the plausible fixes do nothing: adding `align-items`, or making the cell a flex
row, leaves the horizontal centring exactly where it is.

**Fix: every status becomes a button, and every run gets a details popup.** Not the other levelling —
making both a `<span>` — because the asymmetry in the markup is a symptom of a real asymmetry in the
feature: a failed run has somewhere to go and a successful one does not, even though "how long did this
take, how many rows, where did the watermark land" is an ordinary question about a run that succeeded.
Levelling down would have fixed the column and left that.

### `RunErrorDialog` becomes `RunDetailsDialog`

It is already most of the way there — it renders mapping, segment, PID, started and ended, and *then* an
error block. The change is to make the error block conditional and fill in what a successful run is
actually asked about:

- rows read and rows written
- queue time and processing time — the `enqueuedAtUtc` → `startedAtUtc` → `endedAtUtc` pair the row
  already computes but only exposes as a `title` tooltip
- the watermark it moved, and from where, which today lives only in `WatermarkCell`
- the stage timing trace when the run has one, rather than making it the row's separate chevron

One adaptive dialog rather than two components: the fields are the same fields, and a failed run wants
every one of them *plus* its error. Two dialogs would mean two places to add the next field to, and they
would drift. Splitting them later is easy if the two ever genuinely diverge.

### Sameness alone does not fix the column

Both buttons would inherit the UA `text-align: center`, so the badges would line up with each other and
the whole column would still sit centred under a `Status` header that `.grid-head` renders
left-aligned. **The button also needs explicit `justify-self: start`**, or this trades a visible
misalignment for a subtler one and reads as fixed while it is not.

### Two smaller consequences

- **The status button must not look like a link.** The failed case uses `btn-link`, which is accent
  coloured; applying that to every row would turn the status column blue and bury the badge's own
  status colour, which is the thing being read. It becomes a transparent wrapper that keeps the badge's
  appearance and carries hover/focus-visible affordance only.
- **`data-testid={`run-status-${runId}`}` now exists on every row**, not just failed ones. Nothing
  depends on its absence today — there are no tests referencing it at all, which is itself worth noting:
  the dialog shipped in `4dcc135` with no coverage, and this phase adds the first.

## 3. The monitoring latency cell overflows its row and overlaps its neighbours

```css
.grid-row { height: 38px; align-items: center; }
.grid-row.tall { height: 40px; }
```

A fixed height, not a floor. `LagCell` (`MonitoringPanel.tsx:135-196`) stacks up to four items — the
figure with its `exact`/`estimated` badge, "N versions behind", and the "as of HH:MM:SS" line — which is
roughly 50px in a 40px box, spilling above and below into the adjacent rows.

The row was sized when that cell had two lines; the `as of` line phase 88 added is what pushed it over.

**Fix:** a new `.grid-row.auto` variant — `height: auto; min-height: 40px;` with vertical padding — used
by `MappingLagRow`. Not a change to `.grid-row.tall`, because `ReplicationsPage.tsx:54` uses that variant
and is not broken; and not a change to `.grid-row` itself, whose fixed height is load-bearing for the
dense tables that make up most of the app.

## How it will be verified

**Unit / component**
- adding a target column not present on the target appends a row with that name, and the row carries the
  `WILL ADD` badge
- a duplicate target column name is rejected with a message, and no row is appended
- the control renders when every catalog column is already mapped — the regression that caused the
  original report

**E2E** (`tests/DbDataSync.Web.Tests`) — both layout defects get a real assertion, because a layout bug
confirmed only by looking is one that comes back:

- **alignment:** in a run list containing at least one failed and one succeeded run, the status badge's
  `boundingBox().x` is equal for both rows **and** matches the `Status` header's. Both halves matter —
  the first alone passes with the whole column centred under a left-aligned header. This fails today.
- **the details dialog opens from a succeeded run** and shows its rows read/written and processing time;
  the same dialog on a failed run additionally shows the error. The first assertion is new function; the
  second is the first coverage the dialog has ever had.
- **overflow:** for a monitoring row whose lag cell is in its fullest state — an exact figure, a versions-
  behind line and an `as of` line — the lag cell's bounding box bottom is within the row's. This fails
  today.
- a column removed from a mapping and then re-added by name round-trips through a save

Both E2E assertions need a fixture in the *fullest* state rather than the default one — a mapping whose
lag has all three lines, and a run history with a failure in it. A test built on the default fixture
would pass against the broken code, which is how both of these survived this long.

## Out of scope

- Any other use of `.grid-row`'s fixed height. Only `MappingLagRow` moves to the new variant; auditing
  every grid in the app for cells that have quietly outgrown their row is real work and its own phase.
- Removing a target column *from the target table*. Adding one is additive and safe; dropping one is
  destructive, and `AlterTargetTablePlanner` deliberately does not do it.
- Re-designing the column mapping editor's layout. This adds one control and re-words one badge.

## Open questions to resolve during implementation

- **Should a `WILL ADD` row be blocked from saving when no alter plan can cover it?** The type is
  reported `unmappable` by the planner, so the mapping would save and then fail at provisioning. Warning
  at the row is certain; refusing the save is a stronger claim about what the operator meant, and
  probably belongs to the provisioning card rather than the editor.
