# Phase 78 — Automatic GitHub Release on a release tag, versioned by date/time

**Status**: Not started.
**Plan reference**: `architecture/planning/done/nuget-github-release-automation.md`

## The gap

`DataSync.Cli` already packs to a `.nupkg` on every push to `main` (`ci.yml`'s `package` job, lines
103-138) but only as a CI-run artifact (`actions/upload-artifact@v4`) — never a GitHub Release. Version
is hardcoded `0.1.0` in `DataSync.Cli.csproj:13`. No tag trigger, no `gh release create` anywhere, no
prior release has ever been cut.

## What to build

### Trigger

A new workflow (or job) triggered on `push: tags: ['release/v*']` — not every push to `main` (resolved:
would create a new public release several times a day given the date-time versioning scheme), not a
schedule. An operator pushing a tag matching that pattern is what cuts a release.

### Version

Computed at build time from the current UTC date/time — `YYYY.MM.DD.HHmm` — independent of the tag's own
text. The tag is a human-chosen gate; the shipped version is `dotnet pack -p:Version=$(date -u
+%Y.%m.%d.%H%M) ...` (or equivalent), so the tag text and the shipped version can never disagree with
each other. If this interpretation is wrong — if the intent was instead for the tag's own text to *be*
the version, parsed out at build time — that's a smaller, different change; flag this plainly in the
retrospective rather than silently building one and hoping.

### Publish

`gh release create` (the workflow's built-in `GITHUB_TOKEN` covers this, no new secret) naming the
release after the computed version, uploading the `.nupkg` as a release asset. Check what the existing
`package` job does with its Docker image before deciding whether that belongs in the release too, or
stays CI-only as it is today.

## What this phase should not do

- Change the existing per-push `package` job's behavior at all — it keeps running on every push,
  unaffected by and unrelated to this new tag-triggered path.
- Publish to nuget.org or any other public feed.
- Retroactively tag/release the `0.1.0` history.

## How to verify

- A test tag push (cleaned up afterward) produces a GitHub Release with a `.nupkg` asset and a
  `YYYY.MM.DD.HHmm`-shaped version, not `0.1.0`.
- Installing the tool from that release's asset and running its version command reports the computed
  version.
- The existing `package` job's per-push behavior is unaffected — still runs, still artifact-uploads,
  never also tries to cut a release on an ordinary push.
