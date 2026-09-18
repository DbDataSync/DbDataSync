# Branching and releases

Three long-lived branches, in order of how far a change has had to prove itself:

- **`dev`** — where work lands. Every session/worktree pushes here — fetch, check
  `git log --oneline HEAD..origin/dev`, push — the exact fast-forward-only discipline already used
  against `main` until this doc existed, just retargeted.
- **`test`** — a validated snapshot of `dev`, never more than one CI run behind it. **Nobody pushes to
  `test` directly.** It only ever moves because `dev`'s own CI run just passed —
  `.github/workflows/promote-test.yml` fast-forwards it automatically, and refuses (rather than
  force-pushing) if it ever finds `test` holding a commit `dev` doesn't, since that would mean something
  bypassed this and landed on `test` some other way.
- **`main`** — what `release.yml`'s `release/v*` tags are cut from. Promoted from `test` **deliberately**
  — open a PR from `test` into `main` and merge it, a human or an explicitly authorized agent deciding
  the moment, never a side effect of CI going green. `main` moving is a real decision, not a promotion
  that happens to itself.

## Why three, not the one branch everyone pushed to before

Every session and worktree working on this repo pushed straight to `main` — a single branch that had to
double as both "everyone's latest, still-being-argued-with work" and "what a release is cut from," with
nothing between "just landed" and "shipped." That shared-everything shape is exactly what made phase
numbering's own collision problem (see `architecture/implementation/README.md`'s "Phase IDs" section)
costlier than it needed to be: a collision on a low-stakes integration branch is a rebase; the same
collision landing directly on the branch releases are cut from is a rebase discovered *after* something
might already have shipped from it.

Splitting `dev`/`test`/`main` doesn't remove the underlying concurrency — multiple sessions still push to
one shared `dev`, and still can still collide there the same way they used to collide on `main` — but it
moves where a collision is cheapest to have, and gives `main` a real gate instead of being wherever the
tip of everyone's combined work happened to land.

## The mechanics

- **CI** (`.github/workflows/ci.yml`) runs on every push to `dev`, `test`, and `main`, and on every PR
  targeting any of the three — the same checks as before, just triggered on all three stages instead of
  `main` alone.
- **`dev` → `test`**: automatic. `.github/workflows/promote-test.yml` reacts to CI's own completion on
  `dev` (via `workflow_run`, not a second trigger on the same push — no point running the whole suite
  twice to learn the same thing) and fast-forwards `test` to match, only on a green result.
- **`test` → `main`**: manual. Open a PR from `test` into `main`
  (`gh pr create --base main --head test --title "..."`) and merge it when whatever `test` is meant to
  carry — a longer soak, a deliberate look-over, or simply "we're about to cut a release" — is satisfied.
  Nothing automates this on purpose.
- **Releases** (`.github/workflows/release.yml`) are unchanged: a `release/v*` tag pushed on top of a
  commit already on `main`.

## What this does not change

- The fast-forward-only push discipline every session already uses — fetch, check for new commits ahead,
  push. Only the target branch name changes, from `main` to `dev`.
- How phase docs are designed, built, or moved between `todo/`/`done/` — see
  `architecture/implementation/README.md` and `architecture/planning/README.md`. Phase numbering changed
  in the same pass as this doc (see that README's "Phase IDs" section) but for an unrelated reason — the
  same underlying collision problem, at a different layer.

## Not yet decided

- **Whether `main` (and `test`) get branch protection** — no direct pushes, PR-only, enforcing the
  "deliberate gate" above by construction rather than by everyone continuing to honor a convention.
  Deliberately not turned on by this phase: doing so the moment this doc lands would immediately affect
  every session that doesn't yet know `dev` exists and is still pushing straight to `main` out of habit —
  that cutover is worth its own explicit go-ahead, not a side effect of writing the intended flow down.
  Until it's decided, `dev`/`test`/`main` are a documented convention that the tooling (CI triggers, the
  promotion workflow) supports, not one GitHub itself enforces yet.
- Whether the `test → main` PR should be opened automatically (still requiring a human/agent to merge it,
  just not to remember to open it) once `test` is green. Not built this pass — a plausible small
  follow-up, not blocking anything.
