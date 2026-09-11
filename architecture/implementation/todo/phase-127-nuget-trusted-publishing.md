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
- **A tag containing `beta` ships a real SemVer 2 prerelease**, not just a cosmetically-named tag.
  `Compute the version` lowercases `github.ref_name` and appends `-beta` to the computed
  `YYYY.MM.DD.HHmm` when it matches `*beta*` (`release/v1-beta`, `release/v2026-09-beta3`, …),
  emitting a second `prerelease` step output. nuget.org then correctly hides that version from a
  plain `dotnet tool install` (only `--prerelease`, or an exact `--version`, finds it) — the first
  real test of this pipeline against a public feed should not be indistinguishable from a real
  release. `Publish the GitHub Release` reads the same output and passes `--prerelease` to
  `gh release create` when set, so the GitHub side agrees with nuget.org rather than showing a beta
  build as "Latest release." The release notes' one prerelease-specific sentence is built as its own
  shell variable *before* the notes heredoc, not inline inside it — the heredoc is unquoted so every
  literal backtick in it is normally backslash-escaped to stop bash reading it as command
  substitution, and nesting a backtick-bearing `echo` inside a `$(...)` inline in that same heredoc
  hits exactly that trap (confirmed by dry-running it: bash tries to *execute*
  `` `dotnet tool install` `` rather than print it). A pre-computed `${prerelease_note}` variable
  splices in as inert text with no such re-scanning.
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

- **First real run is a `release/v*-beta*` tag, deliberately** — a genuinely first-ever publish
  through a brand-new OIDC policy to a public feed is exactly the case to not also make a permanent,
  fully-listed release version of on the first attempt. Push it against `DbDataSync/DbDataSync` and
  confirm, in order: the pack + smoke-test steps pass unchanged; `Compute the version` emits a
  `-beta`-suffixed version and `prerelease=true`; the `NuGet/login` step succeeds (proves the OIDC
  token exchange and the policy match); `dotnet nuget push` succeeds; `dotnet tool install --global
  DbDataSync` **without** `--version`/`--prerelease` does *not* find it (proves nuget.org actually
  treated it as a prerelease, not merely that the string "beta" appears in it); an exact
  `dotnet tool install --global DbDataSync --version <the emitted version> --prerelease` does; the
  GitHub Release is created marked "Pre-release" with a working exact-version install command in its
  notes.
- Then a real, non-beta `release/v*` tag: confirm the version has no `-beta` suffix, `prerelease` is
  `false`/absent, `dotnet tool install --global DbDataSync` with no flags at all finds it, and the
  GitHub Release is **not** marked pre-release.
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
