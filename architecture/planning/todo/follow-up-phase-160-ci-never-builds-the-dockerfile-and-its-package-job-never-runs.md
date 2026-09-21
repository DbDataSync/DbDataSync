# CI never builds the Dockerfile, and the `package` job it would be checked in never runs

**Found** while verifying phase 160 (docs shipped in the package and the image). Not fixed there: it is a decision
about what CI should cost, not a defect in that phase.

## What is true (checked 2026-09-20)

- `ci.yml`'s `package` job — the only place that packs the tool, installs it, **builds the container image**, starts
  it and probes it — has `if: startsWith(github.ref, 'refs/tags/release/v')`. Its own comment records the
  trade-off: "the first time anyone finds out the Dockerfile or the tool package is broken is now the release itself".
- Since `release.yml` became a manual dispatch that tags *after* publishing, with the version as the tag name
  (`2026.9.20.517-beta`, not `release/v…`), **nothing pushes a `release/v*` tag any more**, so that job cannot run at
  all. The comment's "the release itself" is no longer true either: `release.yml` packs and installs the tool before
  publishing (and, since phase 160, checks the docs are in it), but never builds the image.
- So **no workflow builds the Dockerfile.** A change to it, or to anything it depends on, is first exercised by a person.
- The container check in that job that says "serves the web console, not the fallback" was vacuous:
  `curl -sf … | grep -q "No web assets…"` — `-f` turns a 401 into empty output, the grep matches nothing, the step
  passes. That let a real bug through (below). The text is fixed in `ci.yml`, but the job still does not run.

## What it hid

With authentication on — the default — a container (and the released tool, `2026.9.18.1918` was checked) answered
**401 with an empty body to `/`, `/invite`, `/replications/…` and every asset**. The fallback authorization policy
("anything unmarked is closed, admin-only") applied to static files because `UseStaticFiles` and the SPA `MapFallback`
sat after `UseAuthorization`, so the app could not serve its own sign-in screen. Every API test runs with
authentication off, and the Playwright suite too, so nothing saw it. Fixed in phase 160 (static files ahead of
authorization; the SPA fallback `.AllowAnonymous()`), with auth-on tests in `EmbeddedDocsServingTests`.

## Options

- **A.** Build and probe the image when `test` advances (a `workflow_run`, like `promote-test.yml`), so a green `test`
  means the image builds and serves. Costs a few minutes per promotion; catches this class before a release.
- **B.** A path-filtered job on pushes to `dev` (`Dockerfile`, `docs/**`, `src/DbDataSync.Web/**`, the Cli project) —
  cheaper, misses a change that reaches the image some other way.
- **C.** Have `release.yml` build and probe the image before publishing the package, like the package smoke test it
  already has — the last line of defence rather than an early one.
- Whichever is chosen, delete or repoint the dead `package` job so a comment stops promising a check that cannot run.

## Open questions

- Is the image published anywhere? Nothing in this repo pushes one, so the image may be only a convenience build; if
  so, C alone may be enough.
- Should the anonymous-web-assets behaviour also be probed against the packed *tool* (not just the image)? The API
  tests cover the host either way; the packed tool differs only in where `wwwroot` comes from.

## Update (2026-09-20, phase 163)

Option **C** is now taken in part: `publish-image.yml` (called by `release.yml` after a release) builds the image on native
amd64 and arm64 runners and runs `tools/docker/smoke-test.sh` on the pushed digest before anything is tagged — the checks this
doc describes as missing, including the anonymous `/` 200 that the vacuous `curl -sf | grep -q` step never asserted. It still
runs **only at release time**. Nothing builds the Dockerfile on an ordinary push, so a change that breaks it is still first
found by a release's image job (which fails after the NuGet publish, and can be re-run). Options A and B (per-promotion or
path-filtered builds) remain open, and `ci.yml`'s dead `package` job is still dead.
