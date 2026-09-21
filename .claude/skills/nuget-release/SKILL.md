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

The repo is **`DbDataSync/DbDataSync`**.

## Running a release

The workflow always builds whatever is on `origin/test` — not your working tree, not a local commit
that hasn't been pushed, and not `main` (`release.yml` refuses outright unless dispatched against
`test` — see `architecture/branching-and-releases.md`; there is no separate `test` → `main` PR or push,
cutting the release *is* what fast-forwards `main`). Before dispatching, make sure the repo you're
releasing from is clean and pushed to `dev`, and that `dev` has already been promoted to `test` (only
happens automatically after a green CI run — check `git log --oneline -1 origin/test`).
`scripts/release.sh` checks this itself and refuses to run otherwise, so the common case is just:

```sh
.claude/skills/nuget-release/scripts/release.sh          # a real, stable release
.claude/skills/nuget-release/scripts/release.sh --beta    # a prerelease
```

It dispatches `release.yml`, finds the run it just created (`gh workflow run` doesn't hand back a
run id, so the script matches the newest `workflow_dispatch` Release run created after the moment it
dispatched), watches it to completion with `--exit-status`, and on success prints the resulting
version and the release list. Requires `gh` authenticated with access to the repo, run from inside a
clone of it — nothing else.

If the script refuses because of uncommitted or unpushed changes, commit and push them first (or
confirm with the user that releasing an older commit is actually what they want, then run the raw
`gh` commands below instead of the script).

If the script isn't available for some other reason:

```sh
gh workflow run release.yml --repo DbDataSync/DbDataSync --ref test -f beta=false   # or beta=true
gh run list --repo DbDataSync/DbDataSync --workflow Release --limit 3               # find the new run's id
gh run watch <id> --repo DbDataSync/DbDataSync --exit-status
```

## Beta or stable — default to stable unless told otherwise

**Default to a real, stable release** (`beta=false`, the workflow's own default) unless the user
explicitly asks for a beta, a prerelease, or says they want to test the pipeline itself. A stable
release is a permanent, fully-listed public nuget.org version — don't reach for `--beta` just
because a release feels like a big or risky action; that caution belongs in *confirming with the
user before running the script*, not in silently downgrading what they asked for.

A beta run appends `-beta` to the computed version (a real SemVer 2 prerelease label) and marks the
GitHub Release as a prerelease. nuget.org hides a prerelease from a plain `dotnet tool install` —
only an exact `--version` or `--prerelease` finds it — which is the right shape for exercising the
pipeline itself, not for a release meant to be found normally. No need to go prove that hiding
behavior after every beta; it's an established property of nuget.org, not something this workflow
could get wrong per run.

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

A green workflow run is the real confirmation that the release shipped — nuget.org publishing
doesn't need to be independently re-proven every time.

## nuget.org indexing can lag behind the push by several minutes

Let the user know this rather than treating a not-yet-visible package as something wrong: nuget.org
has indexed a fresh publish anywhere from under a minute to as long as ~15 minutes after the workflow
reports success, and that's without anything having failed — see
`architecture/implementation/done/phase-127-nuget-trusted-publishing.md` (bugs 5–6) for the real
numbers this was measured against. If someone wants to confirm a specific version is live, this is
the one command that actually answers it (an unversioned `--global` install can misleadingly resolve
from a local cache instead, so this uses a scratch path and skips that cache):

```sh
dotnet tool install --tool-path /tmp/verify-release DbDataSync --version <version> --no-cache
```

If that doesn't find it yet, it's very likely just still indexing — wait a few minutes and try again
rather than assuming the release failed (the GitHub Release existing and the workflow having gone
green already established that it published).

## Reference

- `.github/workflows/release.yml` — the workflow itself, heavily commented; read it before changing
  anything about the release process.
- `architecture/implementation/done/phase-127-nuget-trusted-publishing.md` — the full design
  rationale, the nuget.org Trusted Publishing policy setup, and every real bug found building this
  (worth reading before assuming something "should just work").
- `architecture/planning/done/nuget-org-publishing-and-github-hosting-move.md` — why Trusted
  Publishing over a stored API key, and why the trigger is a manual dispatch with no extra approval
  gate.
