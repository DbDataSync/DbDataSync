# Planning Docs — Thinking Before There's a Plan

This folder is where a thought gets written down *before* anyone knows what to do about it. It is
upstream of `architecture/implementation/`, and the two are deliberately different things:

- **`architecture/planning/`** (here) — a problem, an idea, a hypothesis, an observed bug with no
  diagnosis yet. Authored by a human, in whatever state the thought is in. A doc leaves this folder
  once we've *agreed what to do*, not once it's built.
- **`architecture/implementation/`** — numbered phase docs. A phase doc is a design that is ready to
  build, and it moves to that folder's `done/` when the code exists and is verified.

So a single piece of work usually travels: `planning/todo/` → (discussion) → `planning/done/` **and**
a new `implementation/todo/phase-NNN-*.md` → (build) → `implementation/done/`.

## Structure

- **`todo/`** — raw thoughts, not yet resolved. Anyone can drop a file here at any time; it does not
  need to be complete, correct, or well-formed. A wrong hypothesis stated plainly is more useful than
  a vague one hedged.
- **`done/`** — thoughts that have been resolved. "Resolved" means we agreed on an implementation plan
  or a solution — **not** that the work shipped.

## Workflow

1. **You write a file into `todo/`.** Anything from a paragraph to a full design. No format required
   and no numbering — a descriptive kebab-case filename is enough
   (`task-run-errors-during-high-volume-workload.md`).
2. **I read it** and we discuss it. That may mean investigating the codebase, reproducing a bug,
   benchmarking an idea, or arguing about whether it's worth doing at all. Where a stated theory turns
   out to be wrong, say so in the doc rather than quietly dropping it — a ruled-out explanation is a
   real result and stops it being re-proposed later.
3. **Once we've agreed on an implementation plan or a solution, the file moves to `done/`** — edited
   first to record the outcome: what we concluded, and where the work now lives (usually a link to the
   phase doc in `implementation/todo/` that carries the actual design). A one-line "see
   `phase-012-...`" is enough; the design itself belongs in the phase doc, not here.
4. **A thought we decide *not* to act on still moves to `done/`**, with a note on what was decided and
   why. Nothing is deleted. "We considered this and chose not to" is exactly the thing that gets
   forgotten and re-litigated six months later.
5. **If a thought turns out to be several separate problems**, split it — one file per problem, so
   each can be resolved (and moved) independently rather than the whole file being held hostage by its
   least-understood part.

## What's already in `done/`

`architecture.md`, `overview.md` and `tech-stack.md` are the project's original design set — the
thinking that produced everything in `implementation/done/phase-000` through `phase-002`. They predate
this convention and are here because they are exactly what it describes: thinking that has been acted
on.

## Why this exists

The same reason `implementation/README.md` gives, one step earlier in the process. A half-formed
observation — "several task runs failed under load and I *think* it's the change-tracking join" — is
worth capturing precisely because it isn't a plan yet. If it only ever lives in a chat message it
disappears when that session ends, along with the fact that anyone ever noticed. Written down, it can
be picked up, argued with, disproved, or turned into a phase.
