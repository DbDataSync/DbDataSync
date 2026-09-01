# Implementation Docs — Planning & Tracking Convention

This folder is where every non-trivial unit of work on DataSync is planned *before* it's built and
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
| 1 | **079** — a standardized datasync.config.yaml, and command-shared config resolution | small, and fixes a real bug along the way — `datasync invite` cannot reach a non-SQLite state store today |
| 2 | **034** — PostgreSQL logical replication | |
| 3 | **035** — config history diff and revert | |
| 4 | **038** — Postgres COPY staging, and the columnar decision | |

Set 2026-09-01; 067a and 068 done and removed since the previous note; 079 added at the top.
What is left below it is the three engine phases that have been waiting since distribution and auth
moved above them — with 032 and 033 done there were three change-tracking mechanisms and no way for
anyone outside this repo to install
any of them, and nothing guarding the port. A phase moving
up or down is an ordinary decision and only this table changes.

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
