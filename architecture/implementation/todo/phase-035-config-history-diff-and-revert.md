# Phase 35 — Config history: diff and revert (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/config-history-diff-and-revert.md`, split out of the
original `config-import-export-and-revert.md` — see that doc's own guidance that a thought covering
several problems should be split so each can be resolved independently. Import and export stay in
`planning/todo/config-import-export.md`, where the hard questions are.

## Why this half is worth doing on its own

The config store is already a git repository. Every change is already an auto-commit with an author and
a message, and the Version Control tab has shown that log since phase 6.

So the history is **real and complete**. What is missing is any way to act on it — the tab shows when,
who and what message, and nothing else. An operator who broke a mapping five minutes ago can see the
commit that did it and has no way to look at it, let alone undo it.

Both actions in this phase operate on commits that already exist, through a service
(`GitCommitService`) that already writes them. Nothing new is stored, and no new concept is introduced.

## Diff

`GET /api/replications/{name}/history/{sha}/diff` — the patch for one commit, scoped to that
replication's directory.

Rendered in the Version Control tab with Monaco's diff editor, which phase 28 already brings in and
which its own retrospective named as the natural follow-on. The content is YAML, so the existing `sql`
and `csharp` registrations are joined by `yaml` — one more lazy language chunk.

**Read-only.** Editing a diff is not a thing this phase offers.

The mockup's *Diff vs production* is a different feature and needs more than one environment to diff
against (`planning/todo/environments.md`, blocked on a product decision). Diffing against a chosen
commit is the part that is meaningful today and is most of the value.

## Revert

`POST /api/replications/{name}/history/{sha}/revert` — restore this replication's config to its state
at that commit, and **record that as a new commit**. History is never rewritten: the log shows what
happened, including the undo.

Three things to get right, all of which are the reason this is a phase rather than a one-liner:

### It is a restore, not a `git revert`

`git revert` computes an inverse patch, which conflicts if anything touched the same lines since.
Restoring the tree at a commit does not conflict, and it is what an operator means by "put it back the
way it was". The commit message should say so — `Restore replication 'orders-sync' to a1b2c3d` — so the
log reads honestly.

### A revert can leave a replication pointing at nothing

If the commit being restored to predates a table mapping that has since been created, restoring
removes it. If it postdates a connection rename, the restored config references a connection that no
longer exists.

The endpoint therefore **validates the restored config before committing it**, using the validation
that already exists — `EndpointResolution.Validate`, `ConfigRepository.ValidateHooks`, script-binding
resolution — and refuses with what would have broken. A revert that produces config the tool would
reject on save must not be reachable through a different door.

### A running replication has to be considered

The work queue may hold items for a mapping the revert deletes, and a TaskRunner may be mid-pass. The
run model already handles a mapping disappearing (a work item whose mapping cannot be loaded fails that
item, not the process), so the honest scope here is: **say what will happen**, in the confirmation, and
let the operator decide. Pausing the replication first is a thing they can already do.

## The UI

The Version Control tab gains, per commit: a **View changes** action opening the diff, and a **Restore
to here** action with a confirmation naming what will change — which mappings appear, disappear or
differ. The confirmation is built from the same diff the first action shows, so there is one source of
truth for "what does this do".

## What this phase does not build

Import and export. Those carry the genuinely hard questions — name collisions, connections referenced
by name that do not exist in the destination, and secrets, which are deliberately not in the config
store and cannot travel with it — and they stay in `planning/todo/config-import-export.md` until those
are answered.

Diff between two arbitrary commits, or against another environment.

Revert of a connection or a script. Both are git-tracked and both could work the same way; the
replication is where the mockup put it and where the risk of a broken restore is highest, so it is the
one to build the pattern on.

## How to verify when built

- `Category=Integration`: save a mapping, change it, diff the commit and get a patch containing both
  versions; restore and get the earlier one back as a **new** commit with the later one still in the
  log.
- A restore that would orphan a mapping's connection refused, naming it — and the refusal leaving the
  working tree untouched.
- A restore that deletes a mapping succeeding, with the confirmation having said so.
- Playwright: view a diff, restore, and see the log grow rather than shrink.
- Full suite green.

## Open questions

- **Scope of a restore.** This phase restores one replication's directory. A connection referenced by
  it is outside that directory and is not restored, which is why the validation above matters. Whether
  "restore everything at this commit" should exist is a separate and much larger question.
- **Diff size.** A first commit that creates forty mappings is a large patch. Monaco handles it; the
  API should probably cap it rather than stream megabytes into a browser.
- **`dbdatasync.config.yaml` at the repo root has no history view anywhere**, carried forward from phase
  81's retrospective (`architecture/implementation/done/phase-081-admin-config-screen.md`). It sits one
  level above `config/`, so it is git-tracked and diffable at the command line but invisible to
  `GitCommitService.GetHistory`'s `config/`-prefixed queries — and, unlike a replication's directory,
  `GetHistory`'s prefix match (`path.StartsWith(prefix.TrimEnd('/') + "/")`) does not match a bare
  top-level file at all: `dbdatasync.config.yaml` normalizes to `dbdatasync.config.yaml/`, which the file's
  own (no-trailing-segment) path never starts with. Widening this phase's diff/revert to also cover that
  one file — its own small history view, or folded into wherever this phase ends up putting the Version
  Control tab's per-replication one — needs that one-line fix (also match the bare path, not only a
  prefix of it) plus deciding where in the UI a repo-root file's history belongs, since it is not any one
  replication's. Phase 81 judged this out of scope for itself specifically because this phase already
  existed to take it.
