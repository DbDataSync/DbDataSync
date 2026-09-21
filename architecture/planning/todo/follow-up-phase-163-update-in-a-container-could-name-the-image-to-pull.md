# In a container, `update` says "pull a newer tag" without saying which

**Found** 2026-09-21. Phase 158/159 said a container has nothing to update in place, and wrote that as generic advice before an image
existed to point at.

## What is there now

- `UpdatePlanRenderer` (CLI) prints: "This is a container image, not a dotnet tool install. Update it by pulling a newer image tag and
  recreating the container."
- `UpdateService.Capability()` (the web console) says the same in one sentence.
- Phase 163 publishes `ghcr.io/dbdatasync/dbdatasync:<version>` (and `:<version>-runtime`), with the same version strings the release
  list already shows.

## The small change

Where the release list has a chosen version and the install is a container, print the command instead of the advice:

```
docker pull ghcr.io/dbdatasync/dbdatasync:2026.9.20.2152
```

and, on the Updates page, put that string beside the Update button in place of the disabled state's reason. The variant to name comes from
the running image — the `runtime` variant is the one with no SDK, and `dbdatasync` can already tell: `SdkAvailability.HasSdk` is what
`LibraryInstaller` asks (the Dockerfile's `runtime` stage comment describes exactly this distinction).

## What makes it not entirely trivial

- **Registry and image name** would be a constant in the code that must match `publish-image.yml`; a wrong name prints a command that
  fails. A drift test (the name in `publish-image.yml`'s `plan` job equals the constant) would keep them honest.
- **Snapshots and betas:** snapshots are not published as images, and a beta has only exact tags, so `--channel snapshot` in a
  container should say so rather than print a `docker pull` that does not exist.
- **Fork or renamed organisation:** the owner lowercased is derived from `GITHUB_REPOSITORY_OWNER` in the workflow, so the constant
  would be wrong for a fork. Probably acceptable; say so in the code.

## Open questions

- Should a container detect that a *newer image exists* and show it as available, the way the release list already marks `newer`? That
  needs no new source (the versions are the same list); only the "newer than what I am" comparison, which exists.
