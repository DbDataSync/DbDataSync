# `publish-image.yml` never checks that the image it published can actually be pulled by anyone

**Found** 2026-09-21, writing phase 163's "First release checklist".

## The gap

A new GHCR package can be created **private** by its first push, and GitHub has no API for making it public: it is a manual step in the
package's settings, once. Until it is done, `docs/install.md`'s `docker run ghcr.io/dbdatasync/dbdatasync:latest` fails for everybody who
is not signed in to a GitHub account with access — and nothing in the pipeline would say so. `publish-image.yml` logs in with
`GITHUB_TOKEN`, so every step it takes sees the image whether or not it is public; a green run proves nothing about the outside world.
The only safeguard today is item 2 of a checklist in a phase doc.

## Proposal

A last job, `verify-public`, that runs after `merge` **without logging in**:

```sh
docker logout ghcr.io 2>/dev/null || true
docker buildx imagetools inspect "$IMAGE:$VERSION"          # an anonymous manifest fetch
```

and fails, with the settings URL in the message, when it is refused. On the first release that makes the run red until someone makes the
package public and re-runs *that job only* — a loud, one-time reminder, which is the point. Once the package is public it stays a
no-op check that would catch a later flip back to private.

## Open questions

- Red or a warning? Red is honest (the image is not usable) but marks the release workflow failed for an expected first-time reason.
  Leaning red: the alternative is a checklist people skip.
- Does an anonymous `imagetools inspect` behave the same against a private package (401/404) and a public one on GHCR? Expected, not
  tested — nothing has been published yet.
- Whether a package created by a workflow in a repository that is public defaults to public for that organisation is a GitHub setting
  I have not checked; if it does, this check is a pure safety net.
