# Implementation Docs — Planning & Tracking Convention

This folder is where every non-trivial unit of work on DbDataSync is planned *before* it's built and
documented *after* it's built — in the repo, in git history, not in a chat transcript, a temporary
plan file outside the repo, or an AI assistant's own context. If it isn't written here, it doesn't
count as planned or as done.

## Structure

- **`todo/`** — work that has been designed but not yet implemented. One file per phase.
- **`done/`** — work that has been implemented, verified, and committed. One file per phase.

Upstream of this folder is `architecture/planning/`, where a thought is captured *before* anyone knows
what to do about it; a planning doc moves to its own `done/` once we've agreed on a plan, which is
typically the moment a phase doc appears in this folder's `todo/`. See `architecture/planning/README.md`.

A phase moves from `todo/` to `done/` exactly once, at the point its implementation is verified and
committed — see "Workflow" below. Nothing is ever deleted; a phase whose plan changed materially before
implementation gets its `todo/` file edited in place (with a note on what changed and why), not
silently replaced.

## Build order

`todo/` is a set, not a queue — the filename's number records when a phase was *designed*, not when it
will be built, and renumbering files to express priority would break every reference in git history and
in the planning docs that point at them.

So the order lives here, and is the one to work through:

| | phase | why here |
| --- | --- | --- |
| 1 | **034** — PostgreSQL logical replication | |
| 2 | **035** — config history diff and revert | now also covers `dbdatasync.config.yaml`'s missing history view, carried forward from 081 |
| 3 | **038** — Postgres COPY staging, and the columnar decision | |

Updated 2026-09-09 (later than the note below): 118 is done and removed — `GET /api/drivers` now
reports a driver's bound library and kind-name capabilities, a new `GET /api/libraries` (and the two
`known-*` catalog endpoints) back a new Admin → Drivers/Libraries tab pair, all read-only. 119–120
build on it in order, same as before.

Updated 2026-09-09 (earlier): 117 is done and removed — `KnownLibraries` is a
7-entry catalog now (a stable id, a real package id, a factory type, a display name and description),
and `KnownDrivers` (new, one `mysql.generic` entry) supplies `config driver install --from`'s bodies as
embedded YAML instead of a hardcoded CLI switch. 118–120 build on it in order, same as before.

Updated 2026-09-09: 116 is done and removed — `DbDataSync.Providers` is `DbDataSync.Libraries`
throughout, and a `driver.yaml` descriptor now references its library by id (`library: <id>`) instead
of carrying its own `factoryType`/`packages` block. 117–120 build on it in order, same as before.

Updated 2026-09-09: phases **116–120** join `todo/` as one arc — drivers and libraries visible and
manageable from the web console (`architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`).
They are internally sequential (116 → 120) and must be built in order; **116** (the `provider` →
`library` rename plus the descriptor's `library:` reference) is the gate for the rest. The arc is
independent of the 034/035/038 queue above and of the phase-109g–109i dependency-removal series —
relative priority against those is an open call. **121** and **122** are follow-ons, deferred until
116–120 are in production.

Updated 2026-09-04 (latest of all): 105 is done and removed — the Overview → Provisioning tab (retitled
from "Target provisioning") now aggregates every table mapping's plan, grouped by connection and
database, deduplicated by statement, ordered database-scope before table-scope, individually selectable,
copyable per group and runnable as one batch. Nothing else in the ordering rationale below changes,
since 105 was already independent of everything above it.

Updated 2026-09-04 (later than everything below): 104 is done and removed — the run history endpoint
now takes `kind`, `mappingName`, `status` and an opaque keyset `cursor`, `watermark-times` takes the
identical four so the two never resolve to different pages, and the panel's old client-side
`all | failed | backfills` filter is gone in favor of the three server-side ones plus paging controls.
105 moves up to take 104's old spot; nothing else in the ordering rationale below it changes, since 105
was already independent of everything above it.

Updated 2026-09-04 (latest): 102 is done and removed — the Monitoring tab's rows now show and manage
every mapping's intent and hold, over phase 100's endpoints and phase 101's capability declarations.
104 moves up to take 102's old spot; nothing else in the ordering rationale below it changes, since 104
was already independent of 100–102 and only ever depended on 103 for where the panel lives.

Updated 2026-09-04 (yet later): 103 is done and removed — Runs is a Monitoring sub-tab now, Schedule
is on Overview, and the three `RefreshCountdown`s live in the header of the card or pane each one
describes rather than in the now-deleted `ShellActions` portal. 102 stays exactly where the queue jump
above put it, still pointed at the Current Status sub-tab 103 built — but 102's own doc still describes
the pre-103 tab layout, and correcting that description is 102's job when it lands, not something this
update reached into its file to fix.

Updated 2026-09-04 (even later): 101 is done and removed alongside 100, which had already landed but
was left in this table — an oversight, corrected here rather than left for 102 to notice. 101's own
scope shifted mid-implementation: `planning/done/bulk-load-pipeline-and-the-initial-load-rule.md`
retargeted its §1 partway through (see phase 101's own doc for the amendment), which is why its
implementation doesn't match its original design verbatim. 102 stays exactly where 100–101's queue jump
put it — a hold can currently only be set and cleared over the API, which 102 is what makes visible.

Updated 2026-09-04 (later still): 105 joins the list below 104. It depends on nothing above it and
nothing above it depends on it — placed here rather than higher because it eases a setup-time burden
rather than fixing anything broken, and 100–102 are still what an operator hits when a position
expires.

Updated 2026-09-04 (later): 103 goes above 100–102, and 104 below them. Not a judgement that the UX
work matters more than the intent/hold chain — 103 is small and entirely presentational, and 102 puts
new controls on the very tab 103 restructures. Building 102 first would mean designing those controls
against a layout that then moves under them, and 100 and 101 have not started, so the ordering costs
nothing. 104 is independent of all four and sits below them; it depends on 103 only for where the
panel it changes happens to live.

Updated 2026-09-04: 100–102 go to the top, in that order — they are one design split three ways and
have to land in sequence. What earns the queue jump is 101's half: a mapping whose source position
expires today fails on *every* scheduled tick, indefinitely, because there is no backoff or quarantine
anywhere in the codebase — another failed run and another notification each interval, burying every
other failure in that replication's history. The only remedy offered is a full reload, which on a large
table is hours to recover from a source that usually still holds most of what was missed.

Updated 2026-09-03: 096 is done and removed — the three daily-use defects are fixed, and both layout
ones now have assertions that were seen to fail against the broken code. It found a fourth defect on
the way out, which is *not* fixed and is not a phase yet: a target table created by the Setup card's
Apply button never gets its shape cached, so the mapping fails its first run
(`planning/todo/apply-button-does-not-cache-provisioned-columns.md`). That one is server-side and
blocks an ordinary flow, so it is likely to jump this queue once it is agreed.

Updated 2026-09-03: 095 went in at the top and is already done and removed — a mapping the bulk screen
creates now arrives with its columns cached and mapped, so it runs without anyone opening it. It jumped
the queue because it was much smaller than the three below it and it fixed a shipped screen that was
producing mappings which could not run at all. Those three engine phases are again what is left.

Set 2026-09-01; 083 done and removed — the admin-screen/config/certificate arc (079, 081, 082, 083) is
now fully shipped, end to end. What's left below 096 is the three engine phases that have been waiting
since distribution and auth moved above them — with 032 and 033 done there were three change-tracking
mechanisms and no way for anyone outside this repo to install any of them, and nothing guarding the
port. A phase moving up or down is an ordinary decision and only this table changes.

## What counts as a phase

A phase is a coherent, independently describable unit of delivered (or to-be-delivered) work — a
vertical slice of the system, a cross-cutting rework, or (as with Phase 0 of this project) a pure
design/architecture pass that produces documents rather than code. **Documentation and architecture
work is real work and gets a real phase document** — the test for "is this a phase" is "did we decide
or build something," not "did we write code."

**A backlog — a list of things deliberately *not* being done — is not a phase**, and must never be
numbered or labeled as one. `architecture/implementation-plan.md`'s "Backlog" section is the place for
that list; it intentionally has no phase number and no corresponding file in this folder, because
numbering it would create a numbered slot in this folder's sequence with nothing behind it. (This
folder's numbering had exactly that problem once — a "Phase 8" label on the backlog section with no
`phase-008-*.md` file to match, which was confusing and got fixed by dropping the number from the
backlog and renumbering the next real phase into that slot instead of leaving a gap.)

## File naming

`phase-NNN-short-title.md`, zero-padded to **three digits** (`phase-000-...` through
`phase-999-...`) so the sequence sorts correctly by filename indefinitely, regardless of how many
phases the project eventually has. Headings and prose inside a doc, and casual references elsewhere
(commit messages, code comments, conversation), can still say "Phase 8" naturally — the zero-padding
is a filename/sorting convention, not how the phase is spoken or written about.

**Add-on work — a small, discrete follow-up to a specific phase that doesn't warrant its own phase
number — gets a lowercase letter suffix directly on that phase's number**: `phase-007a-...`,
`phase-007b-...`, and so on, in the order the add-on work happened. Use this when the work is clearly
*of* a specific already-numbered phase (a rename, a small correction, a follow-up requested by the
user shortly after that phase landed) rather than new, independent scope — independent scope,
however small, gets the next full phase number instead. When unsure which it is, ask: "does this
depend on / only make sense in the context of one specific prior phase, or could it stand alone?" —
the former is a lettered add-on, the latter is its own numbered phase.

## Workflow

1. **Before implementation begins** on anything non-trivial, produce a complete design — via plan mode,
   a Plan subagent, or direct design work — and write it into `todo/` as one file per phase, in the
   same structure the `done/` docs use (see "Phase doc structure" below), adapted to be forward-looking
   (`**Status**: Planned, not started`, "What this phase will build," "How to verify when built,"
   "Open questions to resolve during implementation" instead of the past-tense equivalents). Number
   phases sequentially, continuing from the highest number in `done/` — never reuse a number, never
   leave a gap for a section (like the backlog) that isn't itself a phase.
2. **While implementing**, the `todo/` file is the plan of record — update it if the design changes
   materially during implementation (a rejected approach, a newly-discovered constraint), the same way
   any other part of the repo gets updated when reality diverges from an earlier design.
3. **Once a phase is implemented, verified (tests green, manual verification done where called for),
   and committed**, rewrite its `todo/` file as a retrospective — what was actually built, how it was
   verified, real bugs found and fixed, decisions made, what's explicitly still not built — and `git mv`
   it into `done/` in the same commit as the implementation. A phase's document and its implementation
   land together; there is no state where a phase is "done" in the repo's code but its plan still sits
   in `todo/`, or vice versa.
4. If a planned phase turns out to be bigger than expected mid-implementation, split it — write
   additional `todo/` phase files for the remaining scope (numbered after the current highest, same as
   any new phase) rather than silently absorbing unplanned scope into what was originally described as
   one phase.

## Phase doc structure

Match the existing `done/` docs' shape:

- `# Phase N — Title`
- `**Status**` (`Planned, not started` in `todo/`; `Complete` in `done/`) and `**Plan reference**`
  (pointing at `implementation-plan.md` and/or the design source, e.g. a plan-mode file's content
  transcribed here, or a prior phase doc's "Design history").
- What this phase builds (or built) — concrete enough to name real types, files, endpoints, schema.
- How it will be (or was) verified — specific tests, specific manual checks.
- Decisions made, and real bugs found (for `done/` docs — these are often the most valuable part of a
  retrospective; don't skip them for the sake of brevity).
- What's explicitly out of scope / not built, so a later phase doesn't have to rediscover the boundary.
- Open questions, for `todo/` docs where something is genuinely undecided and expected to be resolved
  during implementation rather than before it.

## Why this exists

Design work that only lives in a chat session or a plan-mode scratch file disappears the moment that
session ends — it can't be reviewed, referenced by a future phase, or picked up by anyone (human or
AI) who wasn't in that specific conversation. Treating "planned but not yet built" as a real, versioned
artifact in the repo — not a temporary file, not something reconstructed from memory — is what makes
the plan for any given piece of work as durable and inspectable as the code that eventually implements
it.
