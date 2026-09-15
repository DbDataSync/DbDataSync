# Phase 142 — GitHub Actions: Node 20 runtime deprecation

**Status**: Implemented, not committed — other in-flight work on `main` should land first (user's
instruction, 2026-09-15), so this doesn't trigger an extra CI run on its own.

**Plan reference**: No planning doc — diagnosed and fixed directly in conversation, 2026-09-15. CI
was warning that several actions still run on the Node.js 20 runtime, which GitHub is deprecating.

## What this phase builds

Two separate things were both named "Node 20" but only one was actually the warning's cause:

- The warnings are about the **GitHub Actions runtime** each action ships on, not this repo's own
  `node-version` build target. `actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/setup-node@v4`,
  and `actions/upload-artifact@v4` — the majors pinned in `.github/workflows/ci.yml` and `release.yml` —
  all still execute on the Node 20 action runtime GitHub is phasing out.
- Separately (not itself a source of the warning, but inconsistent and worth fixing at the same time):
  `node-version` for the four `setup-node` steps was split — `"24"` in `ci.yml`'s `web` and `playwright`
  jobs, `"22"` in `ci.yml`'s `package` job and in `release.yml`. Node 24 ("Krypton") is the current LTS
  as of 2026-09; there was no reason for the split.

Fix applied to both workflow files:

- `actions/checkout@v4` → `@v7` (4 sites in `ci.yml`, 1 in `release.yml`)
- `actions/setup-dotnet@v4` → `@v6` (4 sites in `ci.yml`, 1 in `release.yml`)
- `actions/setup-node@v4` → `@v7` (3 sites in `ci.yml`, 1 in `release.yml`)
- `actions/upload-artifact@v4` → `@v7` (2 sites in `ci.yml`)
- All four `node-version` inputs unified to `"24"` (`ci.yml`'s `web`, `playwright`, and `package` jobs;
  `release.yml`)

`NuGet/login@v1` (in `release.yml`) was checked and left alone — it isn't one of the standard
`actions/*` majors this warning is about.

## How to verify when built

A real CI run (push or PR) with no Node-20-runtime deprecation annotations, and every job that touches
these actions still green: `dotnet`/`dotnet-windows`/`dotnet-integration` (checkout, setup-dotnet),
`web` (checkout, setup-node), `playwright` (checkout, setup-dotnet, setup-node, upload-artifact), and
`package` (checkout, setup-dotnet, setup-node, upload-artifact — the Docker build and nupkg smoke
tests are the parts most likely to be sensitive to an action major bump, though none of v6/v7 of these
four actions carry a documented breaking change relevant to how this repo uses them).

## Decisions made

- No table entry in `implementation/README.md`'s "Build order" — same treatment as phases 135/136/137:
  small, self-contained, picked up directly rather than queued.
- Bundled the `node-version` unification into this phase rather than filing it separately — it's the
  same two workflow files, the same kind of change, and splitting it would be pure overhead.

## What's explicitly out of scope

- Auditing other third-party (non-`actions/*`) actions for the same runtime issue — only `NuGet/login@v1`
  exists here and was checked.
- Any workflow behavior change beyond the version bumps — no new steps, no logic changes.

## Handoff

Commit alongside (or immediately after) the other in-flight work already on `main`, in the same commit
as this doc. Once a real CI run confirms the warnings are gone and every affected job is still green,
rewrite this as a retrospective (fold "How to verify" into "verified how") and `git mv` it into
`implementation/done/`.
