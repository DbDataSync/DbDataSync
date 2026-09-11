# Moving to github.com/DbDataSync, and automating a nuget.org publish

**Resolved 2026-09-10 — the GitHub move is done; the NuGet automation is designed and gated on one
manual nuget.org step only the account owner can do.**

Two related but separable moves, raised together because the second only makes sense once the first
exists: an owning GitHub org for public issues/PRs/releases, and an owning nuget.org identity for the
package itself.

## 1 — GitHub hosting: `danshryock/DataSync` → `github.com/DbDataSync/DbDataSync`

Phase 92 (the `DataSync` → `DbDataSync` code rename) explicitly deferred this: *"The GitHub repository
and its remote URL — explicitly out of scope; renaming it is a separate operational decision for
whoever owns that account."* That decision is made now: a `DbDataSync` GitHub org exists (created
externally, confirmed via `gh api user/orgs`), and the project moves into it under its own,
already-renamed name.

**Method: create + push, not transfer.** The old repo (`danshryock/DataSync`) is private, has zero
tags, releases, PRs or forks, and one star (the owner's own) — nothing a transfer's redirect/history
preservation would meaningfully protect that a fresh push doesn't already carry (git history is
identical either way). `danshryock/DataSync` is left exactly as it is; no archive, no redirect notice.
That is a call the owner can revisit once the new org repo has proven itself (CI green, a real release
cut) — not a decision this doc forces.

**Visibility: public.** A NuGet-published tool with its own org reads as an open project, and
nuget.org packages are public regardless of source visibility anyway — private source would only have
hidden the code, not the package.

**A concurrent in-flight commit.** A dev-build-versioning change (`ae7e301`, "Move the local-tool
version convention into MSBuild, alpha instead of local" — dev-loop packs only, `release.yml`
untouched) landed on `origin` (the old repo) from a separate session while this move was in progress.
Confirmed by `git fetch` before pushing, fast-forwarded into local `main`, and is present in the
pushed history — the new repo does not start one commit behind.

**Both remotes kept, deliberately, during the transition.** The local checkout is shared with other
concurrent sessions that may still be pushing to `origin` (the old repo) under that name — repointing
`origin`'s URL mid-session could silently redirect someone else's next push. The new repo was added as
a second remote (`neworigin`) instead, pushed to explicitly, and both remotes confirmed to point at the
identical commit. Promoting `neworigin` to `origin` (and the old remote out of the way) is left for
once nothing else is still targeting the old name.

## 2 — Automated nuget.org publishing

**Scope: the CLI tool only.** `DbDataSync.Cli` is what `release.yml` already packs and is what an
operator actually installs. `DbDataSync.Drivers.Abstractions` (packable since phase 109e, for a
third-party compiled `IDriver` plugin) stays unpublished — publishing it is a separate decision for
whenever a real external plugin author needs it, not implied by this move.

**Auth: Trusted Publishing (OIDC), not a stored API key.** nuget.org's Trusted Publishing
(https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) lets a GitHub Actions job
request a short-lived, cryptographically signed OIDC token identifying its exact repo + workflow file,
which nuget.org exchanges for a one-hour, single-use API key — checked against a policy configured
once, on nuget.org's side, naming this repo and workflow. No `NUGET_API_KEY` secret exists anywhere:
nothing to rotate, nothing that leaks if a secret ever does. The one thing a stored-key approach avoids
— a manual nuget.org-side setup step — is unavoidable either way (an API key still has to be generated
there by hand), so Trusted Publishing is strictly the better trade.

**No approval gate beyond the tag.** `release.yml`'s own existing comment already states the project's
convention: *"Pushing a tag under release/v* is the gate — an operator deciding 'cut a release now' —
and nothing else in the repo cuts one."* The GitHub Release publish already works this way with no
second confirmation; the nuget.org push follows the same rule rather than getting a bespoke
`environment:`-gated approval step that nothing else in this workflow has.

**Push before the GitHub Release, not after.** So the Release's own notes can truthfully say "published
to nuget.org" rather than promising something the next step might still fail to do.

### The one step only the account owner can do

nuget.org has no API for creating a Trusted Publishing policy — it is a UI action, and it is what
gates `phase-127` staying in `implementation/todo/` rather than `done/` until it actually happens and a
real tag exercises it end to end. See `phase-127-nuget-trusted-publishing.md` for the exact fields.

## What this does not do

- Publish `DbDataSync.Drivers.Abstractions`, or any other package.
- Add an approval/environment gate to the release workflow.
- Touch `ci.yml`'s per-push `package` job — unaffected, as it always has been by `release.yml` changes.
- Archive, rename, or otherwise touch `danshryock/DataSync`.
- Reserve a `DbDataSync*` package-ID prefix on nuget.org — a separate, later decision if more packages
  are ever published under the org.

## Outcome

| what | where |
| --- | --- |
| GitHub hosting move | `implementation/done/phase-126-github-hosting-moves-to-the-dbdatasync-org.md` |
| nuget.org Trusted Publishing | `implementation/todo/phase-127-nuget-trusted-publishing.md` — code written, held in `todo/` pending the nuget.org-side policy setup and a real verifying release |
