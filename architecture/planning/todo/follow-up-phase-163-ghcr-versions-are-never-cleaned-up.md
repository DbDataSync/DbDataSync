# GHCR versions are never cleaned up: untagged digests from failed runs, and every old release, stay forever

**Found** 2026-09-21, designing phase 163's "push by digest, smoke-test, then tag".

## What accumulates

- **Untagged digests.** Every `build` job pushes its image by digest *before* the smoke test, so a failed test — or a run where only
  one architecture passed — leaves untagged package versions nobody can name and nothing removes. That is the design working (an
  unnamed image cannot be pulled by tag), but it is also storage that never goes away.
- **Old releases.** Every release adds `X` and `X-runtime` (each an index over two architectures plus attestation manifests). Neither
  variant is small: the default image is ~1.1 GB uncompressed. GHCR does not expire anything on its own.
- **The build cache** is `type=gha`, capped by GitHub's per-repository Actions cache limit, with the cache for both variants of an
  architecture stored separately (see the tuning follow-up).

Public images are free to store on GHCR, so this is not urgent; it becomes a real question when the package list is long enough that
finding a version is hard, or if the repository ever goes private (then storage counts against the plan).

## Options

- **A.** A scheduled workflow using the packages API (`actions/delete-package-versions` or `gh api`) that deletes **untagged** versions
  older than N days. Safe by construction: it can never remove a version a tag names.
- **B.** As A, plus: keep the newest N stable tags per variant and delete older ones, mirroring `publish-snapshot.yml`'s "newest N"
  retention. This *does* break `docker pull …:<old version>` for someone pinned to it; decide whether pins are a promise.
- **C.** Do nothing until it hurts.

## Open questions

- Should a release tag ever be deleted? `nuget.org` never lets one go; an image someone pinned in a compose file should arguably behave
  the same way. That argues for A only.
- Deleting a version that is a *member* of a still-tagged index would break that index: any cleanup has to treat an index's children as
  referenced, not untagged. The API does not obviously do that for you — verify against a scratch package before running on the real one.

## Confirmed real, 2026-09-23

`gh api /orgs/DbDataSync/packages/container/dbdatasync` shows **56 versions** already, accumulated since the
package's creation on 2026-09-21 — every release and its multi-arch/multi-variant builds add several. This
is no longer a theoretical future problem; it's actively growing today with no cleanup at all. Worth
prioritizing accordingly once the package is public (see the sibling visibility follow-up) — a private
package accumulating cruft costs nothing anyone can see yet, but a public one will.
