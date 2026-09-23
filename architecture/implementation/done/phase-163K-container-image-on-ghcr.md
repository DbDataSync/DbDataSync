# Phase 163 — the container image, published to GitHub Container Registry for amd64 and arm64

**Status: done, 2026-09-23.** Checked directly against real GitHub state via `gh`, not assumed:
`publish-image.yml` has run for real, repeatedly, across every release cut since 2026-09-21 (confirmed:
releases `35818370642` 2026-09-23T04:26, `35790476802` 2026-09-22T22:05, and others before them) —
`image / build default (arm64)` and `image / build runtime (arm64)` both succeed on a native arm64 runner
every time, not just amd64. `gh api /orgs/DbDataSync/packages/container/dbdatasync` confirms the package
exists, with 56 versions as of 2026-09-23T04:42. Everything this phase built — the Dockerfile version
stamping, the reusable workflow, the by-digest-push-then-smoke-test-then-tag pipeline, both architectures,
both variants — is real and working, not merely designed.

What's left is not this phase's own work: it's operational follow-up, each already its own doc, closeable
independently of this one —

- **The package is still private** — the one manual "First release checklist" step nobody has done yet, so
  `docker pull` fails for anyone not signed into the org. See
  `follow-up-phase-163-publish-image-does-not-check-the-package-is-publicly-pullable.md`.
- **No cleanup** — 56 versions and growing, confirmed real, not hypothetical. See
  `follow-up-phase-163-ghcr-versions-are-never-cleaned-up.md`.
- **Runs as root** — `follow-up-phase-163-container-image-runs-as-root.md`.
- **Docker Hub mirror** — deliberately deferred. `follow-up-phase-163-docker-hub-mirror.md`.
- **Possible double build of the shared stage per architecture** — now has real per-job timing data to
  check it against, not yet actually checked. `follow-up-phase-163-publish-image-builds-the-shared-stage-twice-per-architecture.md`.
- **A running container's own update advice doesn't yet name the image to pull** —
  `follow-up-phase-163-update-in-a-container-could-name-the-image-to-pull.md`.

None of these six block calling the phase itself done — each is real, bounded, already tracked in its own
doc, and can close on its own schedule.

## Why

The image is only ever built from source today (`docker compose -f docker-compose.app.yml up`, or a hand-run `docker build`).
Nothing publishes one, so "run it in a container" means "clone the repository first". A published image also has to tell the truth
about itself, and it did not: a built image reported `2026.09.20.0802-alpha.24` — the csproj's development default — because the
Dockerfile never passed a version, which would have made `dbdatasync update` compare against nonsense.

**Registry: GHCR first, Docker Hub later if wanted.** GHCR needs no secret — the workflow's own `GITHUB_TOKEN` with
`packages: write` — where Docker Hub has no OIDC trust and needs a long-lived token, the thing phase 127 avoided for NuGet. Both
can come from one build later (a second login and one more image name). Compared 2026-09-20 in conversation; the figures that
drift (Hub pull limits, free-tier quotas) are not restated here.

## Design

- **`Dockerfile`**: `ARG VERSION` → `-p:Version=$VERSION`. Empty (a local `docker build .`) leaves the csproj default; a published
  image must always be given one.
- **`.github/workflows/publish-image.yml`** — reusable (`workflow_call`) and dispatchable, taking a `version` (the tag `release.yml`
  made) and `overwrite`:
  1. `plan` validates the version (through the environment, never interpolated into a script) and names the image
     `ghcr.io/<owner lowercased>/dbdatasync`.
  2. `build`, a matrix of **variant** (`default` SDK image, `runtime`) × **arch** (`amd64` on `ubuntu-latest`, `arm64` on
     `ubuntu-24.04-arm`). **Each architecture builds on its own architecture**, not by cross-building: the Dockerfile runs the
     app it just built to bake native libraries into the image (the catalog cache holds `libduckdb.so` and `libe_sqlite3.so`), so
     an arm64 image made from an amd64 build stage would carry amd64 natives and fail only when somebody ran it. Free arm64
     runners exist because the repository is public. Each build **pushes by digest, untagged**; that exact digest is pulled and
     put through `tools/docker/smoke-test.sh`; only a passing digest is uploaded for the next job.
  3. `merge`, per variant: `tools/docker/create-tags.sh` turns the two digests into tags.
- **Tags**: `default` → `X` and, stable only, `latest`; `runtime` → `X-runtime` and, stable only, `runtime`. A prerelease gets only
  its exact tags, so `latest` never points at something a plain install would not pick.
- **`release.yml`** gains `outputs.version` on the `release` job and an `image` job (`needs: release`) that calls the workflow.
  Deliberately *after* the release, not part of it: NuGet cannot be published twice, so an image failure must not be able to
  unwind a release that is out — it fails on its own, visibly, and can be dispatched again.
- **`tools/docker/smoke-test.sh <image> [expected-version]`** — the checks that have each gone wrong in this project with no test
  noticing: the image reports the version it was built for; it serves its console (`/`, `/invite`), every doc page and every doc
  picture to someone who has not signed in (with authentication on it once answered 401 to `/`); `/api/*` is still 401; and the
  log shows no `DllNotFoundException` / `BadImageFormatException` (a wrong-architecture native shows up nowhere else).
- **`tools/docker/create-tags.sh`** — refuses unless *both* architectures have a smoke-tested digest **and** each digest really is
  that architecture (checked before anything is tagged); refuses to replace an existing version tag unless `overwrite`; verifies
  both platforms behind the finished tag.

## Verified

- **Locally, amd64, on real builds:** the default image (1.12 GB) and the `runtime` image (437 MB) both build with
  `--build-arg VERSION=2026.9.20.9999`, report exactly that version, and pass the smoke test. The smoke test **fails** on an image
  without the stamp (mutation check: the pre-existing unstamped image reports `…-alpha.24` and is rejected) and passes the same
  checks I had been running by hand.
- **`create-tags.sh`, against a throwaway local registry with real amd64 and arm64 images** (`tools/docker/test-create-tags.sh`,
  10 checks): stable and runtime tagging, a prerelease creating no moving tag, an existing version refused and then replaced with
  `overwrite`, a missing architecture refused, a wrong-architecture digest refused **before any tag exists**, an unknown variant
  refused — with inputs both as plain manifests and as provenance-style indexes (what CI pushes). Writing it found a real flaw
  in my first version: it created the tag and only then discovered the platform was missing, leaving a bad tag published. It
  also found that `imagetools inspect` prints no `Platform:` line for a plain manifest, so the platform is now read with
  `--format '{{.Image.OS}}/{{.Image.Architecture}}'`, which works for both.
- **Lint:** `actionlint` clean on `publish-image.yml` (its one note on `release.yml` is an existing step); `shellcheck` clean on
  all three scripts. The action versions pinned (`build-push-action@v7`, `setup-buildx-action@v4`, `login-action@v4`,
  `download-artifact@v8`, `upload-artifact@v7`, `checkout@v7`) are the latest majors as of today, and their inputs were read from
  each `action.yml` rather than assumed.

## Verified for real, since (2026-09-23)

Everything this section originally listed as "not verified" has since run for real, on every release cut
from 2026-09-21 onward — checked directly via `gh`, not assumed:

- **The workflow runs, repeatedly, successfully.** Not a one-off: multiple real releases, every one green.
- **arm64 builds and passes its smoke test, on a native arm64 runner, every time** — the DuckDB/`libduckdb.so`
  question this section worried about was a non-issue in practice.
- **GHCR behaviour is proven**: by-digest push, provenance attestations and `imagetools create` all compose
  correctly against the real registry — 56 real, correctly-tagged versions exist.
- **Package visibility really is the one open item this section correctly predicted** — confirmed `private`
  via `gh api`, exactly as anticipated. See the follow-up doc linked above; GitHub still offers no API to
  flip it, so it stays a manual, one-time step for whoever does it.

## First release checklist

1. Cut a release (the workflow has to reach `main`). The `image` job runs after the NuGet publish; watch it.
2. **Make the package public**: GitHub → the organisation's Packages → `dbdatasync` → Package settings → Change visibility.
   Until then the `docker run` in `docs/install.md` fails for everyone else.
3. `docker pull ghcr.io/dbdatasync/dbdatasync:<version>` from a machine that is not signed in; check `docker buildx imagetools
   inspect` shows `linux/amd64` and `linux/arm64`.
4. If any job failed after the release: `gh workflow run publish-image.yml -f version=<version>` (add `-f overwrite=true` to replace
   tags a partial run created).

**Trying arm before any release**, on an arm server, from a checkout (no GitHub needed):

```sh
docker build --build-arg VERSION=2026.9.20.9999 -t dbdatasync:arm-test .
tools/docker/smoke-test.sh dbdatasync:arm-test 2026.9.20.9999
```

Expect `architecture: aarch64` in the output and `SMOKE TEST PASSED`. A failure there is the answer to the DuckDB question.

## Open questions

- Docker Hub as a mirror: one more login and image name in `publish-image.yml`, plus `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN`.
- Snapshot images (`publish-snapshot.yml`): not built; a snapshot is a NuGet-style package for `dbdatasync update`, and nothing
  asked for an image per `test` promotion.
- Signing (cosign / GitHub artifact attestations): buildx provenance is on by default; signing belongs with the unsigned-packages
  follow-up.
- The image job runs only after a release. A per-push build of the Dockerfile is still absent; see
  `planning/todo/follow-up-phase-160-ci-never-builds-the-dockerfile-and-its-package-job-never-runs.md` (its option C is now
  effectively taken: the image is built and smoke-tested before it is tagged, though only at release time).
