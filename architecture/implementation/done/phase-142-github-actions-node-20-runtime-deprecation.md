# Phase 142 — GitHub Actions: Node 20 runtime deprecation

**Status**: Complete. Verified against real CI runs on `main` (job list checked directly via `gh run
view --json jobs`, e.g. run 35006082487) — no Node-20-runtime deprecation annotation on any job, and
`dotnet`, `dotnet-windows`, `web`, and `playwright` all green (their checkout/setup-dotnet/setup-node/
upload-artifact steps all use the bumped majors). `dotnet-integration` fails on that same run, but on
the already-known, already-tracked `BulkLoadIntegrationTests` flake (see phase 141/143), unrelated to
this phase. `package` never ran on any of these checks — it's gated on `refs/tags/release/v*` pushes
only (`ci.yml:249`), so its own use of the bumped actions is unverified by any normal push; nothing in
this repo currently cuts a release tag on a cadence that would confirm it without deliberately doing so
just to test this.

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

## How it was verified

A real CI run on `main` (several, since this landed bundled with other pushes rather than its own
commit-triggered run — see Decisions below) shows no Node-20-runtime deprecation annotations anywhere,
and every job that actually ran and touches these actions is green: `dotnet`, `dotnet-windows`, `web`,
`playwright`. `dotnet-integration` fails, but on the pre-existing, already-tracked
`BulkLoadIntegrationTests` race (phase 141/143), confirmed unrelated by reading the actual failure
content, not assumed. `package` (checkout, setup-dotnet, setup-node, upload-artifact — the Docker build
and nupkg smoke tests, the parts most likely to be sensitive to an action major bump) never ran on any
push checked: it only triggers on `refs/tags/release/v*`, so it stays genuinely unverified until a real
release is cut. None of v6/v7 of these four actions carry a documented breaking change relevant to how
this repo uses them, so this is a real but low-probability gap, not a known issue.

## Decisions made

- No table entry in `implementation/README.md`'s "Build order" — same treatment as phases 135/136/137:
  small, self-contained, picked up directly rather than queued.
- Bundled the `node-version` unification into this phase rather than filing it separately — it's the
  same two workflow files, the same kind of change, and splitting it would be pure overhead.

## What's explicitly out of scope

- Auditing other third-party (non-`actions/*`) actions for the same runtime issue — only `NuGet/login@v1`
  exists here and was checked.
- Any workflow behavior change beyond the version bumps — no new steps, no logic changes.

