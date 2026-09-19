# Snapshot packages on GitHub Packages, for every passing `test` build

**Status: proposal, not agreed. Investigated 2026-09-19 against the workflows and the live repo; nothing built.
Retention decided 2026-09-19: newest N. The PAT problem (finding 4) now has a proposed way around it — see
"Getting around the PAT" below, which may replace GitHub Packages altogether.**

## The idea

Every commit that reaches `test` has passed CI. Pack it, give it a snapshot version, push it to the
repo's GitHub Packages NuGet feed, and prune old snapshots automatically — so anyone can install
"whatever is on `test` right now" (or any recent test build) without cutting a release to nuget.org.

## What exists today (checked, not assumed)

- **Packing only happens at release time.** `ci.yml`'s `package` job runs only on a `release/v*` tag push —
  restricted there on purpose after per-push nupkg artifacts filled the storage quota (96 artifacts,
  14.7GB). Its own comment names the price: the first time anyone learns the tool package or the
  container is broken is now the release. Per-test-build packing would buy that check back.
- **`release.yml`** computes `YYYY.M.D.HHmm[-beta]` from the clock, packs `DbDataSync.Cli.csproj`, smoke
  tests it, and publishes to nuget.org via Trusted Publishing. It also (unintentionally, via
  `GeneratePackageOnBuild`) packs `DbDataSync.Drivers.Abstractions` into the same `-o` directory; the
  release step anchors its glob to avoid picking that one.
- **The nupkg is ~46MB now** (phase 146), not the "~150MB" `ci.yml`'s upload step still says.
- **The repo is public** (`gh repo view`: `PUBLIC`), which matters for GitHub Packages billing (below).
- **`Cli.csproj` already sets `RepositoryUrl`**, which is what links a pushed package to the repo.

## Findings that shape the design

1. **CI does not run on `test`.** `promote-test.yml` fast-forwards `test` with `git push origin dev:test`
   under the default `GITHUB_TOKEN`, and pushes made with that token do not trigger other workflows.
   Confirmed against real runs: `test` and `dev` both sit at `65615e7`, `dev` has a green CI run for it,
   and `test` has no CI run for it at all (its only push-triggered CI runs are old, from before the
   promotion existed). So "a passing `test` build" cannot mean "CI on `test` went green" — the trigger
   has to be the promotion itself (a job in `promote-test.yml` after the fast-forward, or a
   `workflow_run` on that workflow). This also means `release.yml`'s comment that a commit "legitimately
   gets more than one" CI run (dev push, then test push) is not what actually happens today.
2. **Aside — `promote-test.yml` checks out `dev`'s tip, not the SHA CI passed on.** It reacts to a CI run
   finishing but does `ref: dev`, so a push that lands between CI finishing and the promote job starting
   would advance `test` to a commit no CI run covered. Small window, but anything that publishes "the
   tested build" must build `github.event.workflow_run.head_sha`, and the promotion arguably should too.
3. **Publishing needs no stored secret**: `GITHUB_TOKEN` with `packages: write` pushes to
   `https://nuget.pkg.github.com/DbDataSync/index.json`. `dotnet nuget push --skip-duplicate` makes a
   re-run of the same commit a no-op rather than a failure (GitHub Packages will not overwrite a version).
4. **Consuming is the awkward half.** As I understand GitHub's NuGet registry, even a public package's
   feed requires authentication to *read* — a personal access token with `read:packages` in a
   `nuget.config`/`dotnet nuget add source`. That is a real cost against nuget.org's anonymous
   `dotnet tool install`, and it decides who this is for (the team and CI, not the public). Worth
   confirming against current GitHub docs before building anything.
5. **Storage cost is probably zero here, but retention is still wanted.** As I understand it, storage and
   transfer for packages in a public repo are free; confirm. Even so, ~46MB (plus the small Abstractions
   package) per promotion, and `test` tracks essentially every green `dev` push, adds up to clutter fast.
6. **GitHub Packages has no retention policy of its own** (unlike artifacts' `retention-days`), so
   cleanup is a workflow's job.

## Sketch

- **Trigger**: on each promotion, i.e. each time `test` advances. Its own workflow (`workflow_run` on
  "Promote dev to test", success only, never PRs or forks) keeps `packages: write` out of
  `promote-test.yml`, which holds `contents: write`.
- **Build the promoted SHA**, not a branch tip. Reuse the release pipeline's proven steps: build the SPA,
  `dotnet pack`, assert the nupkg's `wwwroot` is non-empty, install into a clean tool root and check
  `dbdatasync version`. Skip the Docker half of `ci.yml`'s package job. That turns the "only found out at
  release" gap into a per-promotion check.
- **Version**: the same clock-derived core as a release plus a snapshot label, e.g.
  `2026.9.19.1432-snapshot.65615e7`. Derive the clock part from the *commit's* timestamp rather than the
  run's, so re-running the same SHA yields the same version and `--skip-duplicate` makes it idempotent.
  Normalise leading zeros exactly as `release.yml` does. A prerelease label sorts before the same core's
  stable version, and a plain `dotnet tool install` ignores prereleases, so nothing here can be picked up
  by accident as a release.
- **Cleanup**: after each publish, prune snapshot versions for both package ids. Candidates:
  `actions/delete-package-versions` (count-based: keep the N newest, with a prerelease-only guard so
  nothing that isn't a snapshot can ever be deleted — verify its inputs when building), or a scheduled
  workflow that lists versions with `gh api` and deletes by `created_at` for true age-based retention
  ("over time"). Snapshots are the only thing on this feed, since releases go to nuget.org.

## Getting around the PAT (tested 2026-09-19)

Tried against the real `2026.9.18.1918` release, with `--configfile` clearing every source but the one
under test (a plain `--add-source` *adds* to nuget.org, which had this version and made a first attempt
look like it worked when it hadn't):

- **A raw release-asset URL as a NuGet source: does not work.** NuGet treats any URL as a feed root and
  appends `FindPackagesById()`; GitHub answers `618 (jwt:jwt-not-provided)`. There is no
  "install a tool straight from a .nupkg URL".
- **Downloading the asset, then installing from that folder: works, anonymously.** `curl -L` of the
  public asset returned 200 with no token, and `dotnet tool install --add-source <folder> DbDataSync
  --version X` installed it with nothing else configured. A framework-dependent tool nupkg carries its own
  dependencies, so it needs no other feed.
- `release.yml` already attaches the nupkg to every GitHub Release, so anonymous assets already exist.

So the PAT is avoided by **not using a feed for snapshots at all**: publish each promoted build as a
GitHub *prerelease* (`gh release create --prerelease --latest=false`, nupkg attached), and consume it by
download-then-install-from-folder. Newest-N retention becomes `gh release delete <tag> --cleanup-tag`
on everything past the N newest snapshot releases (list is anonymous too, for a client discovering them).
Stable and beta releases are already anonymous on nuget.org, so only snapshots need this path.
Trade-offs against GitHub Packages: no NuGet-native `dotnet tool update` for snapshots (a client does the
download step itself), a tag per snapshot to prune (use a distinct prefix like `snapshot/` so they can never
be confused with `release` versions), and no repository signature (see the self-update doc's note on
verification). The consuming side is the interesting work, and is written up in
`architecture/planning/done/self-update-and-release-channels.md`.

## Open questions

- **GitHub Packages, or snapshot prereleases?** The section above is the case for prereleases: same
  automation, anonymous, and it composes with a self-update. GitHub Packages is only better if
  NuGet-native tooling matters more than avoiding the PAT.
- **Every promotion, or thinned?** Publish each one and prune hard, or publish only the newest since
  the last one finished.
- **Both packages, or just `DbDataSync`?** The Abstractions package is for third-party driver authors and
  its own version is a fixed `0.1.0` in the csproj; a snapshot of it needs the same `-p:Version` override
  the release path already applies to the whole build graph.
- **Retention**: decided as newest N (the value of N is still open). Applies to whichever place snapshots
  end up.
- **Fix promotion to use the tested SHA** (finding 2) as part of this, or separately.
- Whether to also correct `ci.yml`'s stale "~150MB" comment while touching packaging.

---

# Outcome — resolved 2026-09-19

Agreed, as `architecture/implementation/todo/phase-158K-snapshot-releases-and-cli-update-staging.md`
(Part A). **GitHub Packages was not chosen**: snapshots are GitHub *prereleases* with the nupkg attached,
consumed by download-then-install-from-folder, which needs no token. Retention is newest N (default 20).
Where this doc and the phase disagree, the phase wins — notably the tag prefix is `snapshot-` (no slash),
and the version is `YYYY.M.D.HHmm-snapshot.g<shortsha>` (the `g` keeps an all-digit abbreviated SHA from
being an invalid SemVer 2 numeric identifier). Only the tool package is a snapshot, not
`DbDataSync.Drivers.Abstractions`.
