# Mirror the image to Docker Hub

**Deferred** 2026-09-20, by decision: phase 163 publishes to GHCR first (no secret needed); Docker Hub was the alternative discussed.

## Why someone might still want it

Docker Hub is where `docker pull dbdatasync/...` is expected, it has search and a listing page, and many corporate networks allow
`docker.io` by default and are less consistent about `ghcr.io`. Against that: it needs a long-lived access token — Docker Hub has no
OIDC trust like NuGet's Trusted Publishing (phase 127) — and anonymous pulls are rate-limited (the numbers change often; check current
ones before deciding).

## What it would take

- Decide the namespace and create the repository on Docker Hub; create an access token (read/write, not delete) and store it as
  `DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN`, preferably in a protected environment.
- In `publish-image.yml`: a second `docker/login-action` and the second image name in the `build` job's `outputs` and in the `merge`
  job's `create-tags.sh` (the script takes one `IMAGE`; it would need to loop over several, or be called once per registry).
- Build once, push to both: `push-by-digest` can target several names, so no second build.
- Decide whether the smoke test runs against both (a Hub-only failure would be a registry problem, not an image problem — one is enough).
- Sync the repository description from a file, e.g. the README (`peter-evans/dockerhub-description`), and check that action's
  current major before pinning.

## Open questions

- Is a second registry worth a second place that can fall out of step? The tags are created from the same digests, so they cannot
  differ in content; they *can* differ in existence if one push fails. Making the merge job all-or-nothing across registries needs
  thought.
- Which registry do the docs lead with? Two "run it" lines is one too many.
