# A state-only Paused status, and Markdown notes on replications and mappings

**Status: resolved 2026-08-31 — arrived fully specified.**

## The ask

- Keep the existing `Enabled`/disabled setting exactly as it is — git-tracked config.
- Add a new **Paused** status, living in the state database, so a replication can be temporarily held
  without a config change or a commit.
- Add a **Notes** markdown field to both the replication and the table mapping (git-tracked, alongside
  everything else about them).
- Add a separate **notes** field to the pause/resume action itself, in the state database — why *this*
  pause happened, distinct from the replication's own persistent notes.

## Grounding in the current code

- **`Enabled` is git config, and already has exactly the autosave shape this should follow.**
  `ReplicationTaskConfig.Enabled` (`[DefaultValue(true)]`, so `false` actually gets written — a real past
  bug fixed there) is set via its own endpoint, `PUT /replications/{name}/enabled`
  (`ReplicationsController.SetEnabled`), which commits straight to the config repo. Its own doc comment
  explains why it's a separate endpoint: routing it through the full mapping/pipeline draft save would
  mean toggling it from the Runs tab silently committing whatever half-finished edit was sitting in the
  Overview draft. **Paused needs the same isolation, for the same reason**, but written to state instead
  of committed to git.
- **The state store already has a `Tasks` table** (`Name`, `Enabled`, `UpdatedAtUtc`) — a state-side
  mirror of config's `Enabled`, kept in sync via `TaskRunStore.UpsertTask(taskName, enabled)`. This is
  the natural table to add `Paused` (and its note) to — no new table needed.
- **`SchedulerService` checks `task.Enabled` directly off the loaded config** (`if (!task.Enabled || ...)`
  before enqueueing) — not off the state mirror. The Paused check needs to land at the same point, reading
  from state instead of config, so both gates apply before anything gets scheduled: `if (!task.Enabled ||
  IsPaused(task.Name) || ...)`.
- **`ReplicationTaskConfig`/`TableMappingConfig` have no notes field today** — straightforward addition to
  both, git-tracked like everything else on those objects.

## Design

### Paused: state-only, alongside Enabled, both gate scheduling

- **`Tasks` table gains `Paused INTEGER NOT NULL DEFAULT 0` and `PauseNote TEXT NULL`.** State only —
  never touches the config repo, never a commit, and (being in the durable state database, not in-memory)
  survives an API restart, which is what makes it a real "temporarily held" rather than just a session
  flag.
- **Both gates apply independently**: a replication runs only when `Enabled` (config) is true **and**
  `Paused` (state) is false. Pausing an enabled replication doesn't touch `Enabled`; re-enabling a
  disabled one doesn't clear a pause. They're orthogonal, on purpose — "durable intent" and "temporary
  hold" are different questions with different owners (an operator editing config vs. an operator
  reacting to something right now).
- **Pausing stops new work from being scheduled, the same way `Enabled = false` already does — it does
  not kill an in-flight run.** Consistent with `Enabled`'s existing semantics; a pause is "don't start
  anything new," not "stop what's running."
- **New endpoints**, `SetEnabled`-shaped: `PUT /replications/{name}/paused` with `{ paused: bool, note:
  string? }`, writing directly to `Tasks` (no config load/save at all — this is the one thing today's
  `SetEnabled` does that a pause endpoint should not, since there's no config involved).

### `ShouldRun`, one place, used everywhere the question is asked — new, 2026-08-31

**`ShouldRun := Enabled && !Paused`.** Every place currently deciding "should this replication actually
run" (today, just `SchedulerService`'s `if (!task.Enabled || ...)`) computes this the same way, from one
definition, rather than each call site re-deriving `enabled && !paused` by hand. Concretely: a small pure
function taking both flags (`TaskScheduling.ShouldRun(enabled, paused)` or similar), used by
`SchedulerService`'s gate and exposed on the replication status API (`ReplicationStatus.ShouldRun: bool`)
so the SPA can show/derive the same answer without recomputing the logic itself.

### Pause/resume is a popup, and every action is recorded — new, 2026-08-31, supersedes the open question
above

**No automatic decision about clearing the note on resume.** Instead: clicking the Paused toggle — to
pause *or* to resume — opens a popup showing the current note (if any), editable or clearable, before the
action commits. The operator decides each time whether to add, change, or clear it; nothing happens
automatically. This replaces the "does resuming clear the note" open question entirely — there's no single
automatic answer because it's not automatic.

**Every pause and resume is recorded — a real audit trail, not just a current-state flag.** A new state
table:

```sql
CREATE TABLE PauseEvents (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    TaskName      TEXT NOT NULL,
    Action        TEXT NOT NULL,   -- 'Paused' | 'Resumed'
    Note          TEXT NULL,
    PerformedAtUtc TEXT NOT NULL,
    PerformedBy   TEXT NOT NULL
);
CREATE INDEX IX_PauseEvents_TaskName ON PauseEvents(TaskName);
```

`Tasks.Paused`/`Tasks.PauseNote` stay as the fast current-state columns the scheduler reads; `PauseEvents`
is the append-only history, one row per action. `PerformedBy` reuses the same signed-in-user construct
`ReplicationsController` already uses for git attribution (`currentUser`, per phase 52) — no new identity
concept, the same "who did this" answer the config-history tab already gives for commits.

**Visible in the replication Status card**: the current pause state and its note. **`PauseEvents`
history itself stays out of the UI entirely for this phase** — resolved 2026-08-31 — as long as it's
logged (which it is, per the table above), a viewer for it is a deliberate, separate follow-up, tracked
in its own minimal doc: `planning/todo/pause-history-ui.md`.

### Tooltips

Both the Enabled toggle and the Paused toggle, in the replication header, get a tooltip clarifying the
distinction — Enabled is durable, git-tracked intent; Paused is a temporary, state-only hold. Prevents the
two controls, sitting side by side, from reading as redundant.

### Notes: Markdown, git-tracked, on both objects

- `ReplicationTaskConfig.Notes: string?` and `TableMappingConfig.Notes: string?` — plain Markdown text,
  committed and diffed through the existing config-history machinery like every other field on these
  objects. Rendered (not just edited as raw text) wherever it's shown — a notes field nobody can read
  without mentally parsing Markdown syntax isn't much better than a plain string.

### Where the controls live

- **Paused toggle**: beside the Enabled toggle in the replication header (phase 46's home for Enabled —
  same chrome, same reasoning: visible on every tab, independent of the batched settings draft).
- **Mapping's Notes**: on the mapping's own editor — exact card placement an implementation detail.
- **Replication's Notes**: see the tabbed interface below — settled, not an implementation detail anymore.

### The replication Overview becomes tabbed, below Source/Target — new, 2026-08-31

**Grounded in `OverviewPanel.tsx`'s current structure**: below `EndpointsCard` (the Source/Target pair),
three cards are stacked today — **Pipeline**, **Target provisioning**, and `ScriptBindingsCard` (the
"Custom Transforms" the note refers to — scripts *are* the custom transform logic). These three become
**tabs** in one tabbed area directly below Source/Target, and **Notes joins as a fourth tab — first, and
the default.**

- **Tab order**: Notes (default), Pipeline, Target Provisioning, Custom Transforms.
- **Notes tab**: renders `ReplicationTaskConfig.Notes` as rendered Markdown, with a button to toggle into
  an editable source view — not always-editable, since a notes field an operator is usually *reading* the
  history/context of shouldn't default to an open textarea.
- **Routing**: Pipeline, Target Provisioning, and Custom Transforms each get their **own route** (nested
  under the replication's Overview route) — a link to "the pipeline tab" is a real URL. **Notes does not**
  — it's the index/default content at the Overview route itself, no separate path segment.
- **The lifted draft (phase 46) is unaffected.** `draft`/`setDraft` already live on `ReplicationDetailPage`
  and get passed down; nested routing under Overview doesn't change that — switching from the Pipeline
  route to the Provisioning route keeps editing the same draft object, the same way switching top-level
  tabs (Overview/Mappings/Runs/History) already preserves it.

### The table mapping editor becomes tabbed too — new, 2026-08-31

**Grounded in `TableMappingForm.tsx`'s current structure**: `EndpointSidePair` (Source/Target), a Source
filter card beneath it, then stacked `ColumnMappingEditor`, `ScriptBindingsCard`, `DefaultSegmentingCard`,
`ProvisioningCard` — plus "Preview SQL" and "Verify" as header buttons linking to their own existing
routes (`/mappings/:name/preview`, `/mappings/:name/verification`).

Same treatment as the replication Overview, applied here:

- **Tabs, in order**: Notes (new, first/default), Column Mapping (`ColumnMappingEditor`), Custom
  Transforms (`ScriptBindingsCard`), Reload Segmenting (`DefaultSegmentingCard`), Provisioning
  (`ProvisioningCard`), Preview SQL (today's `/preview` route's content), Verify (today's `/verification`
  route's content).
- **Correction, 2026-08-31**: "the source/target cards" moving into the Provisioning tab, positioned
  below the provisioning settings, means `ProvisioningCard`'s own **Source/Target `PlanPanel` DDL-preview
  cards** — the two already inside `ProvisioningCard` today, showing each side's plan and an Apply button
  — **not** `EndpointSidePair` (the mapping-definition Source/Target pickers). `ProvisioningCard`
  currently renders its `PlanPanel` grid *before* the settings card; this reorders it so the settings
  (`InheritableToggle` pair) render first and the `PlanPanel` grid follows, both still inside the one
  Provisioning tab. Purely an internal reorder of `ProvisioningCard`'s own content.
- **`EndpointSidePair` and Source Filter are both unaffected by this phase — neither moves.** They stay
  exactly where they are today, at the top level, Source Filter directly beneath `EndpointSidePair` as
  it already sits. (This also means the "Source Filter floats with nothing tying it to Source/Target"
  concern the first pass raised no longer applies — `EndpointSidePair` never left.)
- **Preview SQL and Verify's header buttons go away** — they're tabs now, not separate links; the routes
  they already point at are the natural fit for those tabs' own addressability.
- **The Provisioning tab carries a count badge** — the number of provisioning steps needing to be applied
  (source plus target), from the same `useProvisioning` data `ProvisioningCard` already fetches. **No
  badge at zero** — a satisfied plan is a plain tab, not a "0."
- **Source Filter becomes collapsible**, same convention `ScriptBindingsCard` already established:
  collapsed by default when empty, open when a filter is already set. Its collapsed heading shows a
  **"Filters Applied" pill** when `source.filter` is non-empty.
- **Routing**, by inference from the replication Overview's rule rather than restated explicitly: Notes
  has no route segment (default/index); Column Mapping, Custom Transforms, Reload Segmenting, and
  Provisioning each get their own nested route, joining Preview SQL and Verify, which already have theirs.
  Worth confirming this inference before implementation, since it wasn't stated as plainly for the mapping
  case as it was for the replication one.

## Open questions

1. Confirm the routing inference above for the mapping tabs (each non-Notes tab gets its own route,
   matching the replication Overview's rule) — likely right, not explicitly restated.
2. Exact card placement for the mapping's Notes field within its own (now default) tab — presumably just
   the tab's whole content, same as the replication's Notes tab.

---

# Outcome

Agreed, as `implementation/todo/phase-064-pause-and-notes.md`.
