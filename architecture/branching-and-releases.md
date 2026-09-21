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

- **`test` → snapshots** (phase 158): every time `test` advances to a commit whose CI passed,
  `.github/workflows/publish-snapshot.yml` publishes it as a GitHub *prerelease* tagged
  `snapshot-<version>` with the tool's nupkg and its `.sha512` attached, and prunes all but the newest 20.
  Nothing goes to nuget.org, and nothing needs a feed or a token to read: `dbdatasync update` lists these,
  downloads the one chosen and prints the commands to install it. The version is
  `YYYY.M.D.HHmm-snapshot.g<shortsha>`, the clock taken from the *commit* so a re-run is idempotent.
  It is triggered by the promotion because CI does not run on `test` at all (see the two constraints
  below), and it refuses to publish a commit without a green CI run of its own.

  Two constraints came out of building it, both about `promote-test.yml`. **A push made with
  `GITHUB_TOKEN` triggers no other workflow**, so `test` never gets a CI run of its own (`dev` and `test`
  sat at the same commit with a green run on `dev` and none on `test`) — anything reacting to "`test`
  moved" has to react to the promotion. And **`workflow_run` workflows are read from the default branch**
  (`main`), which only moves at a release cut: a new workflow of that kind does nothing until a release has
  put it there.

## Branch protection

`main` and `test` are **writable only by the pipelines**. Everything else — a direct push, a merged pull
request, from anyone including an organization owner — is refused.

That is a repository **ruleset** (`Pipelines only: main and test`), not classic branch protection:

| rule | effect |
| --- | --- |
| `update` | no ref update to `main`/`test` at all, which covers direct pushes **and** PR merges |
| `deletion` | neither branch can be deleted |
| `non_fast_forward` | no force pushes |
| bypass | the **DbDataSync Pipelines** GitHub App (id 5026881), and nothing else |

`dev` carries no rules. It is where everything lands, and the whole point of the other two being
unwritable is that `dev` stays the only branch anyone pushes to.

### Why an app, and not `GITHUB_TOKEN`

Both branches are advanced by a workflow pushing directly — `promote-test.yml` pushes `dev:test`,
`release.yml` pushes the released SHA to `main` — so whatever guards them has to let those two through
and nothing else. `GITHUB_TOKEN` cannot be that exception, by three separate routes:

- **Classic branch protection** accepts `github-actions` in a push allowlist and then **silently drops
  it** (verified against this repo, 2026-09-18). This is why the protection here used to be force-push
  and deletion only: with no way to exempt the pipelines, anything stronger would have blocked them.
- **A repository ruleset** refuses it outright: *"Actor GitHub Actions integration must be part of the
  ruleset source or owner organization"*. Actions is a first-party integration, not an app installed on
  this organization, so it cannot be named as a bypass actor here.
- **An organization ruleset**, where it might be accepted, requires a **GitHub Team** plan. This
  organization is on `free`.

What a repository ruleset here *will* accept as a bypass actor, probed directly: `DeployKey`,
`RepositoryRole` and `OrganizationAdmin`. The latter two are no help — `GITHUB_TOKEN` is neither. That
leaves an identity we own, and a **GitHub App beats a deploy key**: its tokens are short-lived, and the
bypass names that one app rather than admitting any write deploy key that exists on the repo.

So both workflows mint a token from the app (`actions/create-github-app-token`) and hand it to
`actions/checkout`, whose `persist-credentials` leaves it in the local git config for the plain
`git push` that follows. The app holds **Contents: read and write** and is installed on this repository
only.

Deliberately unchanged: `gh api` / `gh release create` take `GH_TOKEN` explicitly and keep using
`GITHUB_TOKEN`, as does `id-token: write` for NuGet Trusted Publishing. In `release.yml` only the `main`
fast-forward is actually gated — the tag push is `refs/tags`, which the ruleset does not target — but the
token is wired in at checkout so the job speaks as one identity rather than two.

### The ordering trap — already written down above, and walked into anyway

"The mechanics" already states it: **`workflow_run` workflows are read from the default branch**, which
only moves at a release cut. It was not read before this change was made, and the consequence was
exactly the one predicted there: while the app-token change sat on `dev` and `test`, every promote still
ran `main`'s older copy and pushed as `GITHUB_TOKEN`. A promote succeeding proved nothing about the app,
and was briefly mistaken for proof that it did work.

`release.yml` is the opposite: it is `workflow_dispatch` and refuses unless dispatched against `test`, so
it runs **`test`'s** copy and picked the change up immediately.

That asymmetry dictates the order any future change to this machinery has to follow:

1. Land the change on `dev`; it reaches `test` by the ordinary promote.
2. **Lock `main` first, not `test`.** `release.yml` already runs the new copy, so the release exercises
   the new mechanism, while `promote-test` keeps working on the old one.
3. Run a release. Its `main` fast-forward is the **last** step, after the package is published — so a
   broken bypass leaves a release that genuinely happened with `main` merely lagging, not a broken
   release.
4. Only once `main` carries the new `promote-test.yml` — i.e. after that release — extend the lock to
   `test`.

Done in that order on 2026-09-21: release `2026.9.21.2347` pushed `main` through the active ruleset,
which is what proved the bypass, and `test` was locked afterwards.


### A side effect: `test` and `main` now get their own CI runs

"The mechanics" records, as a constraint, that **a push made with `GITHUB_TOKEN` triggers no other
workflow** — which is why `test` never got a CI run of its own despite `ci.yml` listing it under
`push: branches: [dev, test, main]`.

**A push made with a GitHub App token does trigger workflows.** So moving the pipelines onto the app
incidentally removed that constraint: the promotion's push to `test` and the release's push to `main`
now each start a CI run, which is what `ci.yml` was already asking for ("All three need this suite to
actually mean anything at each stage, not just the last one"). Observed immediately — release
`2026.9.21.2347`'s push to `main` started a CI run on `main`, where the previous release's did not.

That is the configured intent finally happening, but it is not free: a commit travelling `dev` →
`test` → `main` now runs the full suite **three times** instead of once, and this suite is not cheap
(`dotnet-windows` alone is ~30 minutes). Nothing loops — `promote-test.yml` reacts to CI on `dev` only,
and `publish-snapshot.yml` still reacts to the promotion rather than to CI — so the cost is the whole of
the effect.

Worth a deliberate decision rather than leaving it as an accident of the token change: either accept
three runs as the price of each stage meaning something, or narrow `ci.yml`'s `push` branches and let
the promotion's own green-CI requirement carry the guarantee it already carries.

### Recovering if the bypass ever breaks

Delete the ruleset. Note that the fallback `release.yml`'s own comment suggests — `git push origin
<sha>:main` — is exactly what the ruleset blocks, so it is not available while the ruleset is active;
removing the ruleset is the first step, not the second. Nothing here can be repaired with a force push
either, and that is deliberate: release tags point into `main`'s history, so rewriting it would orphan a
published release.

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
  correct for anyone browsing the repo and arguably wrong for anyone contributing to it. It would also change when a new `workflow_run` workflow goes live: read from the default branch, it would
  take effect on a push to `dev` instead of waiting for the next release cut.
- **Whether `promote-test.yml` should check out the SHA CI passed on** rather than `dev`'s tip. It reacts
  to a CI run finishing but does `ref: dev`, so a push landing in between would advance `test` to a commit
  no CI run covered. `publish-snapshot.yml` guards its own output against this (no green run for that
  exact SHA, no snapshot); the promotion itself is unchanged.
