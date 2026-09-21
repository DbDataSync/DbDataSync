# `publish-image.yml` compiles the same build stage twice per architecture, and a few other rough edges

**Found** 2026-09-21. None of this is a defect; it is what the first design chose, and nothing has run yet, so none of it is measured.

- **The shared `build` stage is compiled by both variants.** `default` and `runtime` are separate matrix jobs (they must be: each
  ends at a different Dockerfile stage), and each rebuilds `web` → `build` from scratch on a cold cache, twice per architecture. Their
  GHA caches are scoped `<variant>-<arch>`, so they do not share layers either, and each stores a full copy of the same layers in the
  repository's limited Actions cache. Scoping the cache by architecture only (both variants read and write the same scope) would let the
  second variant find the first's layers, if the two jobs are ordered; run in parallel they will simply race. Measure the real times on the
  first release before deciding it matters.
- **The digest artifacts expire in one day** (`retention-days: 1`). Re-running only the `merge` job after that fails with no digests; the
  whole workflow has to be re-run (or dispatched again with `overwrite=true`). Fine for a retry within the hour; worth saying somewhere
  a person will read it, or raising the retention to a few days (each holds one 72-byte digest).
- **No concurrency control.** Two dispatches for the same version at once race on the same tags. `concurrency: group: image-${{ inputs.version }}`
  with `cancel-in-progress: false` would serialise them.
- **`merge` runs even when one architecture failed** for the *other* variant's matrix entry (`fail-fast: false`), which is intended (an
  amd64-only outage should not stop the runtime variant's tags) — but a variant whose `build` did not complete for both architectures
  fails in `create-tags.sh`'s "no smoke-tested arm64 digest" check rather than being skipped, so the run shows red for that variant. That is
  the honest outcome; the annotation could say which architecture is missing more prominently.
- **Provenance attestations** are on (buildx default for `push-by-digest` outputs). They make every per-arch digest an index with an
  `unknown/unknown` entry; `create-tags.sh` tolerates that (tested), but a future change to how attestations are attached could change what
  `imagetools inspect --format` returns. `tools/docker/test-create-tags.sh` is the check to re-run when the action versions move.
