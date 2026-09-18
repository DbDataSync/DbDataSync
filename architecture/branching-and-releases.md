# Branching and releases

Three long-lived branches, in order of how far a change has had to prove itself:

- **`dev`** — where work lands. Every session/worktree pushes here — fetch, check
  `git log --oneline HEAD..origin/dev`, push — the exact fast-forward-only discipline already used
  against `main` before this doc existed, just retargeted.
- **`test`** — a validated snapshot of `dev`, never more than one CI run behind it. **Nobody pushes to
  `test` directly.** It only ever moves because `dev`'s own CI run just passed —
  `.github/workflows/promote-test.yml` fast-forwards it automatically, and refuses (rather than
  force-pushing) if it ever finds `test` holding a commit `dev` doesn't, since that would mean something
  bypassed this and landed on `test` some other way.
- **`main`** — **the last released commit, and nothing else.** Nothing is "promoted into" `main`;
  `.github/workflows/release.yml` fast-forwards it to whatever that run just shipped, as its final step.
  `main` moving and a release happening are the same event, so `main` can never sit ahead of what is
  actually published.

## Why three, not the one branch everyone pushed to before

Every session and worktree working on this repo pushed straight to `main` — a single branch that had to
double as both "everyone's latest, still-being-argued-with work" and "what a release is cut from," with
nothing between "just landed" and "shipped." That shared-everything shape is exactly what made phase
numbering's own collision problem (see `architecture/implementation/README.md`'s "Phase IDs" section)
costlier than it needed to be: a collision on a low-stakes integration branch is a rebase; the same
collision landing directly on the branch releases are cut from is a rebase discovered *after* something
might already have shipped from it.

Splitting `dev`/`test`/`main` doesn't remove the underlying concurrency — multiple sessions still push to
one shared `dev`, and can still collide there the same way they used to collide on `main` — but it moves
where a collision is cheapest to have, and leaves `main` naming one specific, checkable fact instead of
being wherever the tip of everyone's combined work happened to land.

## The mechanics

- **CI** (`.github/workflows/ci.yml`) runs on every push to `dev`, `test`, and `main`, and on every PR
  targeting any of the three.

- **`dev` → `test`**: automatic. `.github/workflows/promote-test.yml` reacts to CI's own completion on
  `dev` (via `workflow_run`, not a second trigger on the same push — no point running the whole suite
  twice to learn the same thing) and fast-forwards `test` to match, only on a green result. It pushes
  `dev:test` directly, as `github-actions[bot]`.

- **`test` → `main`**: **there is no separate promotion step.** Cutting a release is what moves `main`:

  ```
  gh workflow run release.yml --ref test -f beta=false
  ```

  Dispatching that run *is* the deliberate decision. `release.yml` then, in order:

  1. **Refuses unless dispatched against `test`.** Against anything else it would either release
     unvalidated work or move `main` to something that was never released.
  2. **Requires a green CI run for that exact SHA** — a lookup against the Actions API, deliberately not
     a re-run. The commit already has a CI result, because that is the precondition `promote-test.yml`
     enforces before `test` can point at it; re-running would spend ~15 minutes re-deriving the same
     answer and would hand a known-flaky job (`dotnet-windows`' temp-dir cleanup, see
     `architecture/planning/todo/follow-up-a-temp-dir-that-cannot-be-deleted-fails-a-job-whose-tests-all-passed.md`)
     a veto over releasing at all. What the check *does* catch is a commit that reached `test` by some
     route other than the promotion, which a re-run would not establish any better.
  3. Packs, smoke-tests the package, publishes to nuget.org, tags the commit with the shipped version,
     and publishes the GitHub Release.
  4. **Fast-forwards `main`** to that commit, last — refusing rather than forcing if `main` is not an
     ancestor, mirroring `promote-test.yml`'s discipline.

  ### Why this replaced a `test` → `main` PR

  The first version of this model promoted with a PR from `test` into `main`, merged by hand. It worked,
  but it bought nothing and cost two things. GitHub's PR merge has **no fast-forward option** — the
  three it offers are merge commit, squash and rebase, and the latter two mint new SHAs for commits that
  already exist on `dev` and `test` — so every promotion added an empty merge commit to `main` whose tree
  was byte-identical to `test`'s. And because promoting and releasing were separate acts, `main` could
  sit ahead of the last release, which is precisely the ambiguity the branch is supposed to remove.

  Folding the promotion into the release fixes both: `main` is a literal fast-forward of `test`, sharing
  its SHA, and it means exactly one thing. The gate is not weakened — it moved from "merge this PR" to
  "dispatch this workflow," which is the same human decision, made once, at the moment it actually
  matters.

  Note the version is computed from the UTC clock as `YYYY.MM.DD.HHmm` — nothing is typed or bumped —
  and the tag is pushed by the run *after* the package is confirmed published, so it records what
  shipped rather than predicting it. `beta=true` appends a `-beta` prerelease label, which nuget.org
  then hides from a plain `dotnet tool install`. That workflow was originally tag-*triggered* (phase 78)
  and stopped being so in phase 127; its own header comment explains why at length. Historical
  `release/v*` tags predate the change — `package` in `ci.yml` still keys off `refs/tags/release/v*`,
  which is why it is the one CI job that does not run on an ordinary push or PR.

## Branch protection

`main` and `test` carry the **same** protection, for the same reason: force pushes and deletions are
blocked, and nothing else is.

| setting | value |
| --- | --- |
| force pushes | blocked |
| deletions | blocked |
| pull request required | no |
| required status checks | none |
| enforced for admins | no |

**Why nothing stronger.** Both branches are advanced by a workflow pushing directly —
`promote-test.yml` pushes `dev:test`, `release.yml` pushes the released SHA to `main` — and a
pull-request requirement blocks a direct push outright. `GITHUB_TOKEN` cannot be granted an exception
under classic branch protection: adding `github-actions` to the push allowlist is accepted by the API
and then **silently dropped** (verified against this repo, 2026-09-18). Required status checks would
probably survive, since both pushes carry a SHA whose checks are already green, but "probably" is the
wrong property for the only automated steps in this flow — and `release.yml` now asserts that green run
explicitly, which is the stronger and more legible version of the same guarantee.

**What the protection that remains actually buys.** Release tags point into `main`'s history, so
rewriting it would orphan a published release; and `promote-test.yml` refuses to advance `test` if it
holds a commit `dev` doesn't, which is exactly the moment someone would be tempted to flatten `test` to
make the error go away. Blocking force pushes and deletions is precisely the guard for both, and neither
is something a PR requirement was protecting.

**What is now convention rather than enforcement:** that humans don't push to `main` or `test` by hand.
Nothing stops it. Under this model nobody has a reason to — all work goes to `dev` — but it is honest to
say the tooling no longer prevents it.

## What this does not change

- The fast-forward-only push discipline every session already uses — fetch, check for new commits ahead,
  push. Only the target branch name changes, from `main` to `dev`.
- How phase docs are designed, built, or moved between `todo/`/`done/` — see
  `architecture/implementation/README.md` and `architecture/planning/README.md`. Phase numbering changed
  in the same pass as this doc's first version (see that README's "Phase IDs" section) but for an
  unrelated reason — the same underlying collision problem, at a different layer.

## Not yet decided

- **Whether to move `main` and `test` onto repository rulesets with a bypass actor**, which would let a
  pull-request requirement come back for humans while letting the two workflows through — restoring
  enforcement of the convention named above. Not done in this pass because classic protection cannot
  express it, and rulesets' bypass support for `GITHUB_TOKEN` specifically was not confirmed. The
  alternative, a stored PAT, was rejected: phase 127 removed this repo's last long-lived credential on
  purpose, and reintroducing one to save a convention is a bad trade.
- **Whether `dev` should become the repository's default branch.** `main` is still the default, so new
  PRs and fresh clones point at the released state rather than at where work happens. That is arguably
  correct for anyone browsing the repo and arguably wrong for anyone contributing to it.
