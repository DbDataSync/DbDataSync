# Phase 127 — publish `DbDataSync` to nuget.org via Trusted Publishing (OIDC)

**Status**: In progress — the nuget.org Trusted Publishing policy is created, `NUGET_USER` is set, and
`release/v1-beta` (`ff6d02d`) ran the whole workflow green end to end: `NuGet/login@v1` exchanged its
OIDC token for a real API key ("Successfully exchanged OIDC token for NuGet API key"),
`dotnet nuget push` succeeded ("Your package was pushed"), and the GitHub Release came out correctly
marked `prerelease: true` with exactly one asset. **Still short of `done/`**: nuget.org indexes a
brand-new package id in two separate stages, well behind the push itself — the raw flat-container blob
(`v3-flatcontainer/…`) went live first, but `dotnet tool install` doesn't read that directly; it
queries the **registration index** (`v3/registration5-gz-semver2/dbdatasync/index.json`), which lagged
further and returned `404` for several more minutes after the flat container was already serving the
file. A real install attempt from a genuinely clean tool-path failed on that gap alone ("Version …
is not found in NuGet feeds …") — worth naming explicitly since "the file exists" and "the package is
installable" are two different claims on nuget.org for a package id's very first publish, and only the
second one is what an operator actually needs. The non-beta (stable, unlisted-as-prerelease) path is
also still unexercised — see *How to verify when built*. Five real things were found and fixed or
learned getting here (below); none were guessed at, all were caught by running the real thing and
reading what actually came back.
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

## Real bugs found so far, exercising a `release/v1-beta` tag against `DbDataSync/DbDataSync`

Four in the workflow/CLI, plus one operational finding about nuget.org itself — worth recording now
rather than losing them once the run finally goes green:

1. **The pack step's own glob matched a second, unrelated package.** `dotnet pack
   src/DbDataSync.Cli/DbDataSync.Cli.csproj -o artifacts -p:Version=…` also side-effect-packs
   `DbDataSync.Drivers.Abstractions` into the same directory — `-o`/`-p:Version` on the command line
   are global MSBuild properties applying to the whole build graph, and that project has had
   `GeneratePackageOnBuild=true` since phase 109e. `ls artifacts/DbDataSync.*.nupkg` matched both
   filenames (both package ids start with `DbDataSync.`) and picked the wrong one — the first real tag
   this repo ever pushed is what caught it; nothing before today had run `dotnet pack` on the Cli
   project with a global `-o`/`-p:Version` override at all. **Not cosmetic**: the same broad glob was
   also used by the `dotnet nuget push` and `gh release create` attach steps, so left unfixed the very
   next steps would have published `DbDataSync.Drivers.Abstractions` to nuget.org too — a real scope
   violation, not a wrong log line. Fixed by anchoring the glob to a digit right after the package id
   (a version always starts with one) and capturing the one resolved path as a step output every later
   step reuses, rather than each re-globbing.
2. **The smoke test couldn't see its own just-packed prerelease package.** `dotnet tool install
   --add-source artifacts DbDataSync` (no `--version`) failed on the very first beta tag with "dbdatasync
   is not found in NuGet feeds https://api.nuget.org/v3/index.json, …/artifacts" — from *both* sources
   it checked, not just nuget.org. `--add-source` only **adds** to the default source list (confirmed
   with `dotnet nuget list source` — nuget.org is already registered by default), and an unversioned
   `dotnet tool install` considers only stable versions by design, which hides a `-beta` package in a
   local folder feed exactly as it would on nuget.org itself. This is a real gap in the beta-versioning
   addition itself, not a pre-existing issue: nothing before this phase ever packed a prerelease version
   in this step. Fixed by passing `--version "$SHIPPED"` explicitly, which bypasses the
   stable-only default and is a strictly stronger assertion than before (proves *this exact* version
   installs, not merely that installing found something whose reported string happens to contain it).
3. **`dbdatasync version` structurally cannot report a prerelease label, and never has.**
   `src/DbDataSync.Cli/Program.cs`'s `Version()` read `Assembly.GetName().Version` — a strict 4-part
   numeric `System.Version` — which the SDK derives from `<Version>`'s numeric core alone, silently
   dropping any prerelease suffix. Install succeeded (`Tool 'dbdatasync' (version '2026.9.11.415-beta')
   was successfully installed`), but the *running binary* then reported plain `2026.9.11.415`, failing
   the smoke test's substring check for real — not a test bug, a genuine "the tool can't tell you which
   build you're running" gap. **Pre-existing, not introduced by this phase**: the dev-build
   `-alpha.<seconds>` suffix (landed on `main` the day before this phase, in "Move the local-tool
   version convention into MSBuild") has been silently truncated by every `dbdatasync version` since —
   nothing had ever asserted on the exact string before this run did. Fixed in `Program.cs`: read
   `AssemblyInformationalVersionAttribute` instead (falling back to `AssemblyName.Version` if somehow
   absent) — SDK-style projects stamp it with the *whole* `<Version>` string, prerelease label
   included, plus a `+<git-sha>` build-metadata suffix the SDK adds automatically from source control
   (confirmed harmless: NuGet's own package version is unaffected — metadata after `+` is excluded from
   `PackageVersion` by SemVer 2 convention — and the smoke test's existing substring match already
   tolerates the extra suffix without any further change). Verified locally before pushing: a plain dev
   build now reports `2026.09.11.0418-alpha.10+<sha>`; a build with `-p:Version=2026.9.11.415-beta`
   reports `2026.9.11.415-beta+<sha>`; the full `DbDataSync.Cli.Tests` suite (104 tests) stayed green.
   **This local verification is itself why bug 4 below wasn't caught until the next real run** — it
   used a hand-typed, already-normalized version string, sidestepping the exact discrepancy that
   only shows up when the version comes from `date`'s own zero-padded output.
4. **The computed version and the assembly's reported version disagreed on zero-padding, always —
   not just today.** `date -u +%Y.%m.%d.%H%M` zero-pads every field (`2026.09.11.0421`); NuGet strips
   a leading zero from each dot-separated numeric component of a version core
   (`2026.9.11.421` — the pack step's own comment already documented this, for the nupkg side).
   `AssemblyInformationalVersionAttribute` (bug 3's fix) does not go through that normalization at
   all — it is a raw echo of whatever `-p:Version` literally received — so `$SHIPPED` (read off the
   normalized nupkg filename) and the running binary's reported version were never going to agree as
   strings once bug 3 made the binary report its full version honestly. The smoke test's substring
   check failed for real, with genuinely different numeric text (`2026.09.11.0421-beta` vs.
   `2026.9.11.421-beta`) — not a formatting nit. **This is exactly the discrepancy the pack step's own
   long-standing comment was already written to route around** ("Rather than reimplement those
   normalization rules here … the shipped version is read back off the filename"), except that
   workaround only ever reconciled the nupkg side; nothing made the *assembly's own* reported string
   agree until now, because nothing before bug 3 read anything past the numeric core the SDK derives
   automatically.

   Fixed at the source rather than reconciled afterward: `Compute the version` now strips the leading
   zeros itself (`$((10#$part))` per dot-separated field — forcing base-10 avoids bash reading `08`/`09`
   as invalid octal literals) *before* the string ever reaches `-p:Version`, so what MSBuild receives
   is already in NuGet's normalized form. `$VERSION` and `$SHIPPED` become the identical string by
   construction, for every field, every run — not only when the clock happens to need no padding.
   Verified with a genuine end-to-end local rehearsal this time, not a hand-typed shortcut: pack with
   the pre-normalized version → install into a clean tool-path exactly as the workflow does → run the
   installed binary's own `version` command → confirm the substring check passes. It did, across
   several synthetic zero-padded dates chosen to exercise every field (`2026.01.05.0421`,
   `2026.12.31.2359`, `2026.10.10.1000`), not only today's.
5. **A workflow's own "the push succeeded" is not "the package is installable" — nuget.org indexes a
   brand-new package id in two separate, independently-lagging stages.** After a fully green run
   (`Push to nuget.org` reported "Your package was pushed"), the raw flat-container blob
   (`v3-flatcontainer/dbdatasync/…/dbdatasync.….nupkg`) was serving `200` within about a minute, but a
   real `dotnet tool install --tool-path <clean dir> DbDataSync --version 2026.9.11.425-beta` from an
   uninvolved shell still failed: *"Version 2026.9.11.425-beta of package dbdatasync is not found in
   NuGet feeds https://api.nuget.org/v3/index.json."* `dotnet tool install` resolves versions through
   the **registration index** (`v3/registration5-gz-semver2/dbdatasync/index.json`), a separate
   resource from the flat container, which returned `404` for several more minutes after the blob
   itself was already reachable. Not a bug in this repo's workflow — nothing to fix here — but a real
   trap for verifying a *first-ever* publish of a package id specifically: checking the flat container
   (or the package's nuget.org web page) says nothing about whether `dotnet tool install` can actually
   find it yet. Confirmed by polling the registration index directly rather than assuming the flat
   container's availability generalized.

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
