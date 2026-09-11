# Phase 127 — publish `DbDataSync` to nuget.org via Trusted Publishing (OIDC)

**Status**: Planned, not started — **the workflow code is written, but this stays in `todo/` until
the one manual nuget.org-side step below is done and a real `release/v*` tag has exercised it
end-to-end.** Per `implementation/README.md`'s own rule, a phase moves to `done/` only once
implemented *and verified*; a policy that can't be created by any API call this repo's automation can
make is a real precondition, not a formality.
**Plan reference**: `architecture/planning/done/nuget-org-publishing-and-github-hosting-move.md` §2.
Depends on phase 126 (the repo needs to exist at its final `DbDataSync/DbDataSync` location, since a
Trusted Publishing policy names that exact repo).

## Why

`release.yml` (phase 78) already packs `DbDataSync.Cli` on a `release/v*` tag, smoke-tests it, and
attaches the `.nupkg` to a GitHub Release — but nothing installs it from anywhere but that one
release's assets. `dotnet tool install --global DbDataSync` (the command `docs/install.md` already
documents) resolves against nuget.org by default and currently finds nothing there.

## What this phase will build

### `.github/workflows/release.yml`

- **`permissions` gains `id-token: write`**, alongside the existing `contents: write` — required for
  the job to request a GitHub OIDC token at all; without it the NuGet login step fails silently
  rather than erroring loudly, per nuget.org's own documentation, so this is worth getting right the
  first time rather than debugging its absence later.
- **A `NuGet login` step**, `uses: NuGet/login@v1`, placed **after** the install/smoke-test step and
  **immediately before** the push — the exchanged key is valid for one hour and single-use, so it is
  requested right before it is spent, not any earlier in the job. Its `user:` input is
  `${{ secrets.NUGET_USER }}` — the nuget.org profile name the Trusted Publishing policy is
  registered under (see below), not an email address, and not itself a secret in the sensitive sense
  — stored as one only because that's what nuget.org's own documented example does, and there's no
  reason to diverge from a vendor's tested pattern for a low-stakes value.
- **A `dotnet nuget push` step** using `${{ steps.nuget-login.outputs.NUGET_API_KEY }}`, targeting
  `https://api.nuget.org/v3/index.json`, with `--skip-duplicate` (a re-run that somehow re-asks for an
  already-shipped version fails soft rather than failing the job over something already true).
- **Reordered**: NuGet push moves before "Publish the GitHub Release" — so the Release's own notes
  can say "published to nuget.org" truthfully rather than promising a step that hasn't run yet.
- **Release notes updated** — the primary install command becomes the plain
  `dotnet tool install --global DbDataSync --version ${VERSION}` (no `--add-source` needed once it's
  really on nuget.org); the attached-package `--add-source .` form stays, documented as the
  offline/air-gapped path.
- No change to `ci.yml`'s per-push `package` job, which never touches this workflow.

### `NUGET_USER` — a GitHub Actions secret on `DbDataSync/DbDataSync`

Set via `gh secret set NUGET_USER --repo DbDataSync/DbDataSync` (or the repo Settings UI) to the
nuget.org profile name the policy below is registered under. This repo's automation cannot set it
sight-unseen — it is the one value only the account owner has.

## The manual nuget.org step this phase is gated on

nuget.org has no API for this; it is a UI action under the signed-in account's own menu →
**Trusted Publishing** → *Add a new policy*:

| field | value |
| --- | --- |
| Policy owner | the `DbDataSync` nuget.org organization |
| Repository Owner | `DbDataSync` |
| Repository | `DbDataSync` |
| Workflow File | `release.yml` **(file name only — not `.github/workflows/release.yml`)** |
| Environment | *(leave blank — no GitHub Environment is used, see the parent planning doc's "no approval gate beyond the tag")* |
| Scope / package glob | `DbDataSync` (exact — this phase publishes exactly one package id, not a prefix) |

A policy on a repo that was private very recently may show as "pending" for up to 7 days until a real
successful publish supplies nuget.org the repo/owner IDs it needs to lock the policy permanently — this
repo is public (phase 126), so that window likely does not apply, but it's worth knowing the shape of
the message if it appears rather than treating it as an error.

## How to verify when built

- Push a real `release/v*` tag (or a disposable test one, cleaned up after) against
  `DbDataSync/DbDataSync` and confirm, in order: the pack + smoke-test steps pass unchanged; the
  `NuGet/login` step succeeds (proves the OIDC token exchange and the policy match); `dotnet nuget
  push` succeeds; `https://www.nuget.org/packages/DbDataSync` shows the new version within nuget.org's
  normal indexing delay; the GitHub Release is created with notes naming that version and a working
  plain `dotnet tool install --global DbDataSync` command.
- From a machine that has never touched this repo: `dotnet tool install --global DbDataSync` with no
  `--add-source` at all succeeds and `dbdatasync version` reports the released version — the actual
  claim this phase makes.
- Confirm `ci.yml`'s per-push `package` job is unaffected — still packs and artifact-uploads on every
  push to `main`, does not attempt a NuGet push.
- Once verified, rewrite this doc as a retrospective (what was actually confirmed, the exact tag used)
  and move it to `implementation/done/` in the same commit as any follow-up fix the first real run
  turns up.

## What this phase will not build

- Publishing `DbDataSync.Drivers.Abstractions` or any other package.
- An approval/environment gate on the nuget.org push — deliberately, per the parent planning doc.
- A reserved `DbDataSync*` prefix on nuget.org.
- Anything on `ci.yml` — this phase touches only `release.yml`.

## Open questions to resolve during implementation

- **Whether the "pending policy" 7-day window applies here at all**, given the repo went public before
  the policy is created — confirm by reading whatever nuget.org's UI actually says once the policy is
  created, rather than assuming from the general doc language.
- **Whether to also update `README.md` / `CONFIG.md`** with a "install from nuget.org" callout once
  this is live — small, but worth doing in the same pass as moving this doc to `done/` rather than
  forgetting it.
