# Phase 64 — A state-only Paused status, ShouldRun, an audit trail, and tabbed Overview/mapping pages

**Status**: Complete
**Plan reference**: `architecture/planning/done/pause-and-notes.md`

## What this covers

A `Paused` status living entirely in the state database; a `ShouldRun` derivation used everywhere
run-eligibility is decided; a popup-driven pause/resume flow with a full audit trail (who, when, what
note — logged but not yet surfaced in the UI); Markdown `Notes` on both `ReplicationTaskConfig` and
`TableMappingConfig`; and a restructured replication Overview page *and* table mapping editor — both
gaining a tabbed area with Notes first/default, each other tab routed except Notes.

## 1. State: `Tasks` gains current pause state; a new `PauseEvents` table is the audit trail

```sql
ALTER TABLE Tasks ADD COLUMN Paused INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Tasks ADD COLUMN PauseNote TEXT NULL;

CREATE TABLE PauseEvents (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    TaskName       TEXT NOT NULL,
    Action         TEXT NOT NULL,   -- 'Paused' | 'Resumed'
    Note           TEXT NULL,
    PerformedAtUtc TEXT NOT NULL,
    PerformedBy    TEXT NOT NULL
);
CREATE INDEX IX_PauseEvents_TaskName ON PauseEvents(TaskName);
```

`Tasks.Paused`/`PauseNote` are the fast current-state columns the scheduler reads. `PauseEvents` is the
append-only history — every pause and every resume, with whatever note was entered and who did it,
written in the same transaction as the `Tasks` update. `TaskRunStore` gains `SetPaused(taskName, paused,
note, performedBy)` (writes both), `IsPaused(taskName)`, and `GetPauseHistory(taskName, limit)`.

## 2. `ShouldRun`

A single, shared definition — `ShouldRun := Enabled && !Paused` — not re-derived at each call site:

```csharp
public static class TaskScheduling
{
    public static bool ShouldRun(bool enabled, bool paused) => enabled && !paused;
}
```

- `SchedulerService`'s gate becomes `if (!TaskScheduling.ShouldRun(task.Enabled, taskRunStore.IsPaused(task.Name)) || mappingNames.Count == 0)`.
- `ReplicationStatus` (the API DTO `GET /replications/{name}/status` already returns, per phase 46) gains
  a `ShouldRun: bool` field computed the same way, so the SPA reads the answer rather than recomputing
  `enabled && !paused` itself.
- Any other place currently checking `Enabled` alone for run-eligibility gets audited and switched to
  `ShouldRun` during implementation — not assumed to be only the scheduler.

## 3. API: pause/resume, with attribution

`PUT /replications/{name}/paused`, body `{ paused: bool, note: string? }`. Writes to `Tasks` **and**
inserts a `PauseEvents` row, both via `TaskRunStore.SetPaused`, using `currentUser` (the same construct
`ReplicationsController.SetEnabled`'s neighbors already use for git attribution) as `PerformedBy` — no new
identity concept.

A `GET` for `PauseEvents` history is **not built this phase** — logged and queryable at the data layer,
but no endpoint or UI consumes it yet. Tracked as its own follow-up:
`architecture/planning/todo/pause-history-ui.md`.

## 4. Config: `Notes` on both objects

```csharp
// ReplicationTaskConfig
public string? Notes { get; set; }

// TableMappingConfig
public string? Notes { get; set; }
```

Plain Markdown, git-tracked and diffed through the existing config-history mechanism.

## 5. SPA: pause/resume popup, tooltips, Status card

- **Paused toggle** beside the Enabled toggle in the replication header. Clicking it — to pause *or* to
  resume — opens a popup showing the current note (if any), editable or clearable, before the action
  commits. No automatic clearing, no automatic keeping — the operator decides every time. The action only
  fires on confirm, carrying whatever note text is present.
- **Tooltips** on both the Enabled and Paused toggles, clarifying the distinction (Enabled: durable,
  git-tracked; Paused: temporary, state-only).
- **Status card** shows the current pause state and its note when paused. Nothing more — the history
  itself is out of scope, per `pause-history-ui.md`.

## 6. SPA: the replication Overview becomes tabbed

Restructuring `OverviewPanel.tsx`, grounded in its current shape (`EndpointsCard`, then stacked Pipeline /
Target provisioning / `ScriptBindingsCard` cards):

- Below `EndpointsCard` (Source/Target), the three existing cards — **Pipeline**, **Target Provisioning**,
  **Custom Transforms** (`ScriptBindingsCard`) — become tabs in one tabbed area, joined by a fourth,
  **Notes**, which is **first and the default**.
- **Notes tab**: `ReplicationTaskConfig.Notes` rendered as Markdown, with a toggle button into an editable
  source view — not open-by-default.
- **Routing**: Pipeline, Target Provisioning, and Custom Transforms each get their own nested route under
  the replication's Overview route. Notes has no route segment of its own — it's the index content at the
  Overview route itself.
- **The lifted draft (phase 46) is untouched by this** — `draft`/`setDraft` stay owned by
  `ReplicationDetailPage`; the new sub-routes read/write the same object switching between them exactly
  like the existing top-level tabs already do.
- The three moved cards' own content/behavior is unchanged — only their container (card → tab) and
  addressability (implicit → routed) change.

## 7. SPA: the table mapping editor becomes tabbed too

Grounded in `TableMappingForm.tsx`'s current shape: `EndpointSidePair` (Source/Target), a Source-filter
card beneath it, then stacked `ColumnMappingEditor`, `ScriptBindingsCard`, `DefaultSegmentingCard`,
`ProvisioningCard`, plus "Preview SQL" and "Verify" as header buttons linking to their existing routes
(`/mappings/:name/preview`, `/mappings/:name/verification`).

- **Tabs, in order**: Notes (new, first/default) · Column Mapping (`ColumnMappingEditor`) · Custom
  Transforms (`ScriptBindingsCard`) · Reload Segmenting (`DefaultSegmentingCard`) · Provisioning
  (`ProvisioningCard`) · Preview SQL · Verify.
- **Correction: "source/target cards" moving into Provisioning means `ProvisioningCard`'s own Source/
  Target `PlanPanel` DDL-preview cards** (already inside `ProvisioningCard`, showing each side's plan and
  Apply button) — **not** `EndpointSidePair`. `ProvisioningCard.tsx` currently renders the `PlanPanel`
  grid *before* its settings card (the `InheritableToggle` pair); reorder so settings render first and
  the `PlanPanel` grid follows — an internal reorder within `ProvisioningCard`, nothing external moves.
- **`EndpointSidePair` and Source Filter are both untouched by this phase** — they stay exactly where they
  are today, at the top level, Source Filter directly beneath `EndpointSidePair`.
- **Preview SQL and Verify's header buttons are removed** — replaced by tabs pointing at the same
  existing routes those buttons used to link to.
- **The Provisioning tab gets a count badge** — the number of provisioning steps that need applying
  (`plans.source.steps.length + plans.target.steps.length` from `useProvisioning`, which already models
  a plan as discrete steps with their own title/command text). **No badge when the count is 0** — a
  fully-satisfied plan (the common case once provisioning has actually been applied) shows a plain tab
  label, not a "0". Since the tab bar has to show this regardless of which tab is currently active, it
  calls `useProvisioning` itself (the same query `ProvisioningCard` already uses, keyed the same way, so
  React Query dedupes it rather than double-fetching) instead of only the routed `ProvisioningCard`
  fetching it.
- **Routing**: Column Mapping, Custom Transforms, Reload Segmenting, and Provisioning each get their own
  nested route, joining Preview SQL and Verify's existing ones. Notes has no route segment — default/index
  content, same rule as the replication Overview. (Inferred from that rule, not explicitly restated for
  the mapping case — confirm during implementation.)
- **`TableMappingConfig.Notes`** renders the same way as the replication's Notes tab: Markdown, with an
  edit toggle, not open by default.
- **Source Filter becomes collapsible**, same precedent `ScriptBindingsCard` already established
  ("collapsed until something is bound... opens by itself when this level binds something"): collapsed by
  default when `source.filter` is empty, open when it isn't. Its collapsed heading carries a **"Filters
  Applied" pill** when `source.filter` is non-empty — a small, self-contained addition to the card that's
  staying in place regardless of everything else in this section.

## What this phase does not build

- Any change to `Enabled`'s existing behavior, endpoint, or config shape.
- Killing or interrupting an in-flight run when paused — pausing only prevents new scheduling.
- Any change to what Pipeline/Target Provisioning/Custom Transforms/Column Mapping/Reload Segmenting/
  Preview SQL/Verify actually do — this phase moves their container, not their content.
- A `PauseEvents` viewer of any kind — tracked separately in `pause-history-ui.md`.
- Relocating `EndpointSidePair` or Source Filter — neither moves; only `ProvisioningCard`'s internal
  ordering changes.

## How to verify when built

- Pausing writes a `PauseEvents` row and updates `Tasks.Paused`/`PauseNote`, without touching the config
  repo (`git status` clean).
- `ShouldRun` is false whenever either `Enabled` is false or `Paused` is true, true only when both allow
  it — exercised directly against `TaskScheduling.ShouldRun`, and against the API's `ReplicationStatus`.
- An in-flight run at the moment of pausing completes normally; only the next scheduled attempt is
  blocked.
- Clicking Paused (either direction) opens the popup; confirming with a note records it; confirming with
  the note cleared records an empty note; canceling the popup performs no action and writes nothing.
- `PauseEvents` accumulates one row per action, each with the correct `PerformedBy`, retrievable in order
  at the data layer (no endpoint required to verify this — a direct store-level check is enough).
- Tooltips render on both toggles with distinct text.
- The Status card shows an active pause's note and nothing else pause-related; resuming clears the
  "currently paused" state.
- The replication Overview shows four tabs, Notes first/default with no route segment; Pipeline/
  Provisioning/Transforms each reachable by a direct URL; switching between them preserves in-progress
  draft edits (regression check against phase 46).
- The table mapping editor shows seven tabs, Notes first/default with no route segment; the other six
  each reachable by a direct URL (Preview SQL and Verify keeping their existing paths); within the
  Provisioning tab, the settings render above the Source/Target `PlanPanel` DDL-preview cards;
  `EndpointSidePair` and Source Filter both remain at the top level, unmoved and unaffected.
- A mapping with pending provisioning steps shows the Provisioning tab's badge with the correct combined
  source+target step count; applying steps down to zero removes the badge entirely (no "0"); a mapping
  that was always fully satisfied never shows one.
- Source Filter starts collapsed for a mapping with no filter, and shows a "Filters Applied" pill only
  once a filter is entered; a mapping that already has a filter opens the card by default, matching
  `ScriptBindingsCard`'s established convention.
- A replication's and a table mapping's `Notes` fields round-trip through save, show in config history's
  diff view, and render as Markdown with a working edit toggle.
- Full suite green, including a Playwright flow: pause with a note via the popup, see it in the Status
  card, resume via the popup clearing the note; and flows navigating both the replication's four tabs and
  the mapping's seven tabs by URL.

## Open questions

- Confirm the mapping tabs' routing follows the same "Notes has no route, everything else does" rule
  inferred from the replication Overview.
- Exact SPA placement for the mapping's Notes tab content (presumably just the tab body, same as the
  replication's Notes tab — likely not actually open).

---

# Retrospective

The pause half arrived fully specified and was small. The tabbing half was not small, and most of
what took thought was not the tabs themselves — it was the two things that quietly stopped working
because a component that had always been mounted no longer was.

## A pause is a different kind of fact from Enabled, and the schema says so

Both gates answer "should this run", so the tempting shape is one column with three states. They are
not one question. `Enabled` is durable intent: it lives in git, it is diffed on screen, and changing
it is something somebody can be asked about a month later. A pause is an operator reacting to
something happening now, and putting that in the config repo would either fill the history with noise
or — more likely — stop anyone using it, because nobody wants their afternoon to be a commit.

So `Paused` sits on the `Tasks` row the scheduler already reads, and never touches the repo. There is
a test asserting the commit count does not move across a pause and a resume, because the day that
assertion fails is the day the feature has silently become a slower spelling of `Enabled`.

`PauseEvents` exists for a narrower reason than "audit trails are good". A pause is the only
operational act in this product that changes what it does while leaving no trace anywhere — every
config change is a commit, every run is a `TaskRuns` row, and a pause was going to be neither. Without
the table, "why did this stop replicating for three days in March" has no answer. Both writes go in
one transaction: the alternative is a replication held with nothing saying by whom, which is the exact
state the table exists to prevent.

Nothing reads the history yet, deliberately — the viewer is `planning/todo/pause-history-ui.md`. It is
still tested, because a table nothing reads is a table nothing notices is wrong, and it will be read
eventually.

## `ShouldRun` is a function for a reason that is not tidiness

`enabled && !paused` is short enough that a shared function looks like ceremony. Three places ask the
question — the scheduler's tick, the status endpoint, and the SPA — and one of them is in a different
language. The SPA gets `shouldRun` as an answer rather than the two flags, so a third gate added later
is a change in one C# file and not also in TypeScript that nobody remembers is deriving the same rule.

It ships with both inputs beside it, which is not redundancy: the UI has to be able to say *which*
gate is closed, because "resuming this will still not start it" is something an operator needs to see
before they try.

## The scheduler is the only gate, and manual triggers stay ungated

The doc asked for an audit of everywhere `Enabled` is checked for run-eligibility. There is exactly
one: `SchedulerService`. Manual triggers — Run Now, Backfill — do not check `Enabled` today and so do
not check `Paused` either. That is left alone on purpose: a manual trigger is somebody standing at the
screen asking for this specific thing, and refusing it because of a hold they can see and could lift
in two clicks would be the product arguing with a person who already knows.

## Two things the tabbing broke, and why they were worth finding

Both were the same underlying shape: behaviour that depended on a component being mounted, in a
component that is now one tab among several.

- **Column auto-suggestion.** It ran in `ColumnMappingEditor`. Save requires at least one column
  mapping, so a new mapping whose Column Mapping tab was never opened had Save permanently disabled
  with nothing on screen explaining why. It moved up to the form, which is always mounted. A
  suggestion about the mapping belongs with the mapping, not with whichever tab happens to be open.
- **A save was dropping verification checks and hooks.** `TableMappingForm` builds the saved document
  field by field, and a save replaces the whole thing — so the two fields it does not edit were being
  erased. This predates the phase, but making Verify a tab one click from Save turned a latent bug into
  a likely one, so it is fixed here rather than filed.

Neither was in the phase doc. Both were found by the Playwright suite failing in ways that looked like
test problems and were not, which is the argument for updating an end-to-end suite honestly instead of
loosening it until it goes green.

## Markdown, rendered as elements rather than HTML

Notes are typed by one operator and rendered in every other operator's session. That is the shape of a
stored XSS, and the usual answer — a Markdown library plus a sanitiser plus the discipline to keep
them correctly wired — is three things to keep right. Producing React elements instead means there is
no HTML being parsed and therefore nothing to sanitise: text becomes text nodes, and the only tags
that exist are the ones written in `Markdown.tsx`. Link hrefs are still restricted to http/https/
mailto, because a link is the one thing in a note that could act rather than inform.

The subset is the subset notes are written in. Anything outside it renders as its own literal text
rather than vanishing, which is the right failure for a field whose entire purpose is that somebody
reads what was written.

No dependency was added. That was a judgement, not a rule: about a hundred lines against a parser plus
a sanitiser, for a field with a known and small vocabulary.

## Decisions the phase doc left open

- **The mapping tabs follow the same routing rule as the replication Overview** — confirmed, per the
  doc's first open question. Notes is the index with no segment of its own; everything else has a
  route. The consequence is the reason it is right: the plain URL for a mapping or a replication opens
  what the thing is *for*, and nobody has to know a path segment to send somebody a link to a mapping.
- **The mapping's Notes tab is the whole tab body**, per the second open question — the same
  `NotesPanel` the replication uses, same props, same behaviour.
- **`NotesPanel` opens rendered, not editing.** The common visit is somebody finding out what they are
  looking at; the rare one is somebody writing it down. Defaulting to a textarea optimises for the
  rare case and makes the common one read Markdown syntax by eye.
- **Empty notes are `null`, not `""`.** The empty state has its own prompt saying what the field is
  for, and an empty string round-tripping as "a note that exists and says nothing" would make that
  distinction wrong.
- **Preview SQL and Verify keep their existing routes and render the tab bar themselves.** Nesting
  them inside the editor would have been less code and wrong: both are about the mapping *as saved*,
  which is exactly not what an editor holding unsaved changes is showing. They sit beside it, as they
  always have, and wear the same bar.
- **Both tabs are hidden entirely for an unsaved mapping**, rather than shown and dead. There is
  nothing to preview and nothing to compare, and two dead ends on a screen somebody is half-way
  through filling in is worse than five tabs.
- **`SubTabs` is visually lighter than the top-level `.tabbar`.** Two identical tab strips stacked
  would leave nobody sure which row they were on.
- **The pause popup can be dismissed with Escape or by clicking the backdrop.** A popup that can only
  be dismissed by finding its Cancel button is a popup people press Confirm on to get rid of, which
  for this control means an accidental pause.
- **"Clear note" is its own button.** Clearing is one of the three things the popup exists to allow,
  and making somebody select the text and delete it would hide one third of the feature.
- **`PerformedBy` is `currentUser.Author.Name`** — the same construct and the same "nobody is signed
  in" fallback the config history already uses for commits. No new identity concept, per the plan.
- **`SetPaused` upserts rather than updates.** A replication that has never run has no `Tasks` row,
  and holding something before its first pass is an entirely ordinary thing to want.
- **Pausing a *listed* replication that vanished mid-request is not an error.** The status composition
  swallows a `FileNotFoundException` and reports the state it has; the next poll 404s on the listing
  check anyway.

## Verification

- `PauseStateTests` (7) — a never-touched task not being paused; pausing something that has never run;
  `UpsertTask` not quietly resuming a held replication (the worker calls it on every start, so this is
  the one that would have bitten); resuming clearing the hold; a note edited, kept and cleared while
  still paused; every action recorded in order with the right `PerformedBy`; history scoped per task;
  and the limit returning the most recent first.
- `TaskSchedulingTests` (4) — all four combinations of the two gates.
- `PauseEndpointTests` (7) — a fresh replication eligible; pausing making it ineligible and carrying
  the note; resuming restoring it; disabled-and-paused reporting both gates and staying ineligible
  after a resume; **pausing never moving the commit count**; a 404 for a replication that does not
  exist; and replication notes round-tripping through save.
- `NotesYamlRoundTripTests` (4) — real Markdown (headings, emphasis, code spans, links, a blockquote)
  surviving both objects' round trip, absent notes staying null rather than becoming `""`, and notes
  being editable and clearable.
- `StateDatabaseTests` — `PauseEvents` added to the expected schema.
- Playwright 39 and 40, new — the pause round trip end to end (cancel writing nothing, pausing with a
  note, the note surviving a reload, resuming with the note cleared, the commit count unmoved, and the
  status endpoint's own `shouldRun`); and Markdown notes on both objects rendering, committing and
  reloading, plus every mapping tab's URL with Notes as the one without a segment. Existing tests were
  updated to navigate the new tabs rather than loosened.
- Full suite green: 833 unit, 153 integration, 42 Playwright.

## Open questions

- ~~**Whether the mapping tabs each get a route.**~~ They do; Notes is the index.
- ~~**Where the mapping's Notes content sits.**~~ The whole tab body.
- **`PauseEvents` has no reader.** By design, per `planning/todo/pause-history-ui.md` — but it now
  accumulates rows nothing prunes. `PruneRuns` covers `TaskRuns` and `Logs` only. A pause is a rare,
  human-initiated act so the growth is negligible, and it is named here rather than left to be
  discovered by whoever eventually writes the viewer.
- **The pause popup is the first modal in this SPA.** It brings its own `.modal`/`.modal-backdrop`
  CSS and its own Escape handling. There is no focus trap, and a second modal should probably
  generalise this rather than copying it.
- **`Markdown.tsx` supports no tables and no nested lists.** Both are plausible in a note about a
  table mapping. They render as their own literal text today, which is honest but not good, and the
  moment somebody actually wants one the calculation about a dependency changes.
