---
name: nuget-release
description: >-
  Publish a new DbDataSync release: dispatches the release.yml GitHub Actions workflow (manual
  trigger, Trusted Publishing to nuget.org — no stored API key), watches it to completion, and
  confirms the tag, the GitHub Release, and the published package. Use this whenever the user asks
  to cut, ship, publish, or release a new version of DbDataSync — including a beta or prerelease —
  or wants the dbdatasync CLI tool updated on nuget.org. Trigger on phrases like "let's release",
  "publish a new version", "ship a beta", "cut a release", "push a new nuget package", "release
  what's on main" — even if the user doesn't name the workflow, nuget.org, or GitHub Actions
  explicitly.
---

# Releasing DbDataSync to nuget.org

One package, one workflow, no stored secret: `.github/workflows/release.yml` packs
`DbDataSync.Cli` (the `dbdatasync` global tool, nuget.org package id `DbDataSync`), publishes it via
Trusted Publishing (a short-lived OIDC-exchanged API key, nothing long-lived stored anywhere), tags
the released commit itself once the publish is confirmed, and creates the GitHub Release. It never
publishes `DbDataSync.Drivers.Abstractions` — that package exists for a future third-party compiled
driver plugin and is deliberately out of scope here.

The repo is **`DbDataSync/DbDataSync`** (the `origin` remote) — not the old `danshryock/DataSync`,
which nothing publishes to any more.

## Running a release

Use `scripts/release.sh`, which wraps the whole dispatch-find-watch sequence this doc used to ask a
human to do by hand three separate `gh` calls:

```sh
.claude/skills/nuget-release/scripts/release.sh          # a real, stable release
.claude/skills/nuget-release/scripts/release.sh --beta    # a prerelease
```

It dispatches `release.yml`, finds the run it just created (`gh workflow run` doesn't hand back a
run id, so the script matches the newest `workflow_dispatch` Release run created after the moment it
dispatched), watches it to completion with `--exit-status`, and on success prints the resulting
version and the release list. Requires `gh` authenticated with access to the repo — nothing else.

If the script isn't available or you need to do this by hand:

```sh
gh workflow run release.yml --repo DbDataSync/DbDataSync -f beta=false   # or beta=true
gh run list --repo DbDataSync/DbDataSync --workflow Release --limit 3    # find the new run's id
gh run watch <id> --repo DbDataSync/DbDataSync --exit-status
```

## Beta or stable — default to stable unless told otherwise

**Default to a real, stable release** (`beta=false`, the workflow's own default) unless the user
explicitly asks for a beta, a prerelease, or says they want to test the pipeline itself. A stable
release is a permanent, fully-listed public nuget.org version — don't reach for `--beta` just
because a release feels like a big or risky action; that caution belongs in *confirming with the
user before running the script*, not in silently downgrading what they asked for.

A beta run appends `-beta` to the computed version (a real SemVer 2 prerelease label, not a
cosmetic one) and marks the GitHub Release as a prerelease. nuget.org then hides it from a plain
`dotnet tool install DbDataSync` — only an exact `--version` or `--prerelease` finds it. That's the
right choice for exercising the pipeline itself, not for a release meant to be found normally.

## What a successful run means

- The version is computed from the UTC clock at run time (`YYYY.M.D.Hmm`, leading zeros already
  stripped so it matches exactly what NuGet and the assembly's own reported version agree on) —
  never typed by a human, never read from a tag.
- The run itself creates and pushes a lightweight git tag named after that exact version, **only
  after** the nuget.org push is confirmed — so a tag existing is proof the version really shipped,
  and a failed run leaves no tag to clean up. Retrying a failed release is just running the script
  again; the next run computes a fresh, always-different version.
- The GitHub Release (`gh release view <version> --repo DbDataSync/DbDataSync`) is created from that
  tag, titled and named after the version, carrying the `.nupkg` as its one asset.
- The version is on nuget.org: `https://www.nuget.org/packages/DbDataSync/<version>`.

## If the package doesn't show up on nuget.org right away

A **brand-new package id's first-ever publish** goes through nuget.org's validation pipeline before
it's fully indexed, and can lag the workflow's own "Your package was pushed" by several minutes —
this bit us hard getting Trusted Publishing working the first time (see
`architecture/implementation/todo/phase-127-nuget-trusted-publishing.md`, bug 5, for the full story).
Specifically, nuget.org has two independently-lagging stages: the raw flat-container blob
(`api.nuget.org/v3-flatcontainer/...`) tends to go live first, but `dotnet tool install` resolves
versions through the **registration index**
(`api.nuget.org/v3/registration5-gz-semver2/dbdatasync/index.json`), which can return `404` for a
while after the blob itself is already reachable. Checking the flat container or the nuget.org web
page is not proof `dotnet tool install` can find it yet.

**This only matters for a first-ever publish of a package id.** `DbDataSync` is a real, established
package now — an ordinary new *version* of it should index much faster. If a fresh release genuinely
doesn't show up after a few minutes, poll the registration index directly rather than assuming
something in the workflow is broken:

```sh
until curl -s -o /dev/null -w "%{http_code}" \
  "https://api.nuget.org/v3/registration5-gz-semver2/dbdatasync/index.json" | grep -q "^200$"; do
  sleep 20
done
```

## Verifying an install for real

Don't trust `--global` alone to prove a release is installable — it can silently resolve from your
own machine's local NuGet cache. Use a scratch tool-path with no local source, exactly what the
workflow's own smoke test does:

```sh
dotnet tool install --tool-path /tmp/verify-release DbDataSync --version <version>
/tmp/verify-release/dbdatasync version
```

For a beta, also confirm a plain unversioned install correctly finds *nothing* — proof nuget.org is
genuinely treating it as hidden, not merely that the string "beta" is in the version:

```sh
dotnet tool install --tool-path /tmp/verify-release-2 DbDataSync   # should fail to find anything
```

## Reference

- `.github/workflows/release.yml` — the workflow itself, heavily commented; read it before changing
  anything about the release process.
- `architecture/implementation/todo/phase-127-nuget-trusted-publishing.md` — the full design
  rationale, the nuget.org Trusted Publishing policy setup, and every real bug found building this
  (worth reading before assuming something "should just work").
- `architecture/planning/done/nuget-org-publishing-and-github-hosting-move.md` — why Trusted
  Publishing over a stored API key, and why the trigger is a manual dispatch with no extra approval
  gate.
