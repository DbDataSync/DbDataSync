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
- **`main`** — what releases are cut from. Promoted from `test` **deliberately** — open a PR from `test`
  into `main` and merge it, a human or an explicitly authorized agent deciding the moment, never a side
  effect of CI going green. `main` moving is a real decision, not a promotion that happens to itself.

## Why three, not the one branch everyone pushed to before

Every session and worktree working on this repo pushed straight to `main` — a single branch that had to
double as both "everyone's latest, still-being-argued-with work" and "what a release is cut from," with
nothing between "just landed" and "shipped." That shared-everything shape is exactly what made phase
numbering's own collision problem (see `architecture/implementation/README.md`'s "Phase IDs" section)
costlier than it needed to be: a collision on a low-stakes integration branch is a rebase; the same
collision landing directly on the branch releases are cut from is a rebase discovered *after* something
might already have shipped from it.

Splitting `dev`/`test`/`main` doesn't remove the underlying concurrency — multiple sessions still push to
one shared `dev`, and still can collide there the same way they used to collide on `main` — but it moves
where a collision is cheapest to have, and gives `main` a real gate instead of being wherever the tip of
everyone's combined work happened to land.

## The mechanics

- **CI** (`.github/workflows/ci.yml`) runs on every push to `dev`, `test`, and `main`, and on every PR
  targeting any of the three — the same checks as before, just triggered on all three stages instead of
  `main` alone.

- **`dev` → `test`**: automatic. `.github/workflows/promote-test.yml` reacts to CI's own completion on
  `dev` (via `workflow_run`, not a second trigger on the same push — no point running the whole suite
  twice to learn the same thing) and fast-forwards `test` to match, only on a green result. It pushes
  `dev:test` directly, as `github-actions[bot]`; that it is a *direct push* is why `test`'s branch
  protection is deliberately lighter than `main`'s (see below).

- **`test` → `main`**: manual, via PR:

  ```
  gh pr create --base main --head test --title "Promote test into main for release"
  gh pr merge <n> --merge
  ```

  Merge it when whatever `test` is meant to carry — a longer soak, a deliberate look-over, or simply
  "we're about to cut a release" — is satisfied. Nothing automates this on purpose.

  **This lands as a merge commit, not a fast-forward, and that is expected.** Even when `main` is a
  strict ancestor of `test` and a fast-forward would be possible, GitHub's PR merge has no
  fast-forward option — the three it offers are merge commit, squash, and rebase, and the latter two
  mint new SHAs for commits that already exist on `dev` and `test`, which would fork the history three
  ways and turn the next promotion into a conflict. So merge commit is the only non-rewriting choice
  through a PR, and it is what `--merge` above does.

  The cost is worth naming so nobody re-litigates it later: each promotion adds one commit to `main`
  whose tree is byte-identical to `test`'s, so `main` and `test` agree on content but never on SHA.
  A literal fast-forward is only reachable by pushing directly (`git push origin test:main`), which
  `main`'s branch protection now refuses — deliberately. The PR is the gate; the extra commit is its
  receipt.

- **Releases** (`.github/workflows/release.yml`): **manually dispatched, not triggered by a tag.**

  ```
  gh workflow run release.yml --ref main -f beta=false
  ```

  Run it against `main`. Nothing is typed or bumped: the version is computed from the UTC clock as
  `YYYY.MM.DD.HHmm`, and the run itself creates and pushes the version tag once the package it built is
  confirmed live — so the tag is a record of what shipped, not an advance guess at what might. `beta=true`
  appends a `-beta` prerelease label, which nuget.org then hides from a plain `dotnet tool install`.

  That workflow was originally tag-triggered (phase 78) and stopped being so in phase 127; its own header
  comment explains why, at length. Historical `release/v*` tags predate the change — `package` in
  `ci.yml` still keys off `refs/tags/release/v*`, which is why it is the one CI job that does not run on
  an ordinary push or PR.

## Branch protection

Turned on deliberately, after the flow above had been exercised end to end. `main` and `test` are
protected differently on purpose, because what threatens each is different.

**`main`** — the gate, enforced by GitHub rather than by convention:

| setting | value | why |
| --- | --- | --- |
| pull request required | yes, **0 approving reviews** | Enforces "no direct pushes to `main`" by construction. Zero approvals because this repo is worked solo — requiring one would make every promotion unmergeable, which is a lock, not a gate. |
| required status checks | `dotnet`, `dotnet-windows`, `dotnet-integration`, `web`, `playwright` | The five that run on every push and PR. **`package` is deliberately excluded** — it only runs on `refs/tags/release/v*`, so requiring it would leave every PR pending forever. |
| strict (up-to-date before merge) | no | `main` only ever receives promotions from `test`; forcing a rebase dance for a branch nothing else pushes to buys nothing. |
| force pushes / deletions | blocked | The release tag points at a commit on `main`; rewriting it would orphan a published release. |
| enforce for admins | **no** | Leaves a deliberate escape hatch. `dotnet-windows` has a known flaky temp-dir cleanup failure (`architecture/planning/todo/follow-up-a-temp-dir-that-cannot-be-deleted-fails-a-job-whose-tests-all-passed.md`) that reds a job whose tests all passed; an admin can merge past it rather than burning a 14-minute re-run. Tighten this once that flake is fixed. |

**`test`** — force pushes and deletions blocked, and **nothing else**:

No PR requirement and no required status checks, because `promote-test.yml` advances `test` by pushing
`dev:test` directly. A PR requirement would break that outright. Required status checks would probably
survive it — the promotion is a fast-forward to the very same SHA whose checks just went green on `dev` —
but "probably" is the wrong property for the one automated step in this flow, and the checks would be
re-asserting a result that is already the workflow's own trigger condition.

What protection still buys here is the case the workflow itself warns about in its own comment: it
refuses to fast-forward if `test` ever holds a commit `dev` doesn't. Blocking force pushes and deletions
means nobody can resolve that refusal by flattening `test` — which is exactly the moment someone would
be tempted to, and exactly the moment a human should look instead.

## What this does not change

- The fast-forward-only push discipline every session already uses — fetch, check for new commits ahead,
  push. Only the target branch name changes, from `main` to `dev`.
- How phase docs are designed, built, or moved between `todo/`/`done/` — see
  `architecture/implementation/README.md` and `architecture/planning/README.md`. Phase numbering changed
  in the same pass as this doc (see that README's "Phase IDs" section) but for an unrelated reason — the
  same underlying collision problem, at a different layer.

## Not yet decided

- Whether the `test → main` PR should be opened automatically (still requiring a human/agent to merge it,
  just not to remember to open it) once `test` is green. Not built — a plausible small follow-up, not
  blocking anything.
- Whether `enforce_admins` on `main` should be turned on, closing the escape hatch described above. It
  should be, once `dotnet-windows` is reliable enough that merging past a red check is never the
  reasonable move.
