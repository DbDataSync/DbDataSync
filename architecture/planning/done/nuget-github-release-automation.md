# Automatic GitHub Release on a release tag, versioned by date/time

**Status: resolved — ready for an implementation phase doc.**

## What's there today, confirmed by reading the code

- `DataSync.Cli` is already the only packable project — `PackAsTool=true`, `ToolCommandName=datasync`,
  `PackageId=DataSync`, hardcoded `Version=0.1.0` (`DataSync.Cli.csproj:10-13`). It's a framework-
  dependent global tool referencing `DataSync.Api`, `DataSync.State`, `DataSync.TaskRunner`.
- `.github/workflows/ci.yml`'s `package` job (lines 103-138) already runs
  `dotnet pack src/DataSync.Cli/DataSync.Cli.csproj -c Release -o artifacts` on every push to `main`,
  installs it as a tool, smoke-tests it, builds/health-checks the Docker image, and uploads the `.nupkg`
  via `actions/upload-artifact@v4` — a CI-run-scoped artifact, not a GitHub Release. No tag trigger exists
  anywhere in this workflow.
- No git tags exist in the repo yet, no `gh release create`/`actions/create-release`-family step
  anywhere, no `CHANGELOG`. This is genuinely greenfield — no existing convention to preserve or migrate.
- `DataSync.Cli`'s `version` command (`Program.cs:24`) reflects `typeof(Help).Assembly.GetName().Version`
  — whatever `<Version>` the csproj carried at build time. No separate version source to reconcile.

## Design

**Trigger**: pushing a tag matching `release/v*` — resolved via question, not every push to `main`
(which the date-time versioning scheme would otherwise make trivially frequent) and not a schedule.
A new `on: push: tags: ['release/v*']` block, either as a new job in the existing `ci.yml` or a
dedicated `release.yml` — implementation's call, but a dedicated workflow is probably cleaner given this
one has a genuinely different trigger and purpose than the build-and-test workflow.

**Version**: computed at build time from the current UTC date/time, not parsed out of the tag's own
text. The tag is a human-chosen release *gate* (an operator decides "cut a release now" by pushing a tag
named however they like within the `release/v*` pattern — e.g. incrementing `release/v1`,
`release/v2`, or embedding their own guess at the date), but the actual package `<Version>` DataSync
ships is independently computed as `YYYY.MM.DD.HHmm` (UTC) at the moment the workflow runs, via
`dotnet pack -p:Version=$(date -u +%Y.%m.%d.%H%M) ...` or equivalent. This avoids the tag text and the
shipped version ever disagreeing, and matches "version number by date and time" literally rather than
through a tag-naming convention a human has to get right by hand. Flag this interpretation plainly in
the retrospective — if the intent was instead "the *tag itself* carries the real version and CI just
reads it," that's a smaller, different change, and worth confirming didn't get lost in translation.

**Publish**: after `dotnet pack` with the computed version, `gh release create` (already-authenticated
`GITHUB_TOKEN` in Actions covers this, no new secret needed) creating a GitHub Release named after the
computed version, uploading the `.nupkg` as a release asset. Reasonable to also attach whatever the
existing `package` job already produces (the Docker image reference, if it publishes one — check what
that job does with the image today before deciding whether it belongs in the release too, or stays
CI-only).

## What this phase should not do

- Touch the existing per-push `package` job's behavior — it keeps building and artifact-uploading on
  every push, unrelated to and unaffected by this new tag-triggered path.
- Publish to nuget.org or any other feed — "upload to GitHub Releases" is what was asked; a public feed
  publish is a separate, bigger decision (API key management, irreversible-once-published semantics) not
  implied by this request.
- Retroactively tag/release anything for the `0.1.0` history already in the repo.

## How to verify

- A test tag push (`release/v-test-...` or similar, cleaned up after) actually produces a GitHub Release
  with a `.nupkg` asset and a `YYYY.MM.DD.HHmm`-shaped version.
- Confirm `datasync version` (or equivalent) reports the computed version once installed from that
  release's asset, not the old hardcoded `0.1.0`.
- Confirm the existing `package` job's per-push behavior is unaffected (still runs, still artifact-
  uploads, doesn't also try to cut a release).

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-078-nuget-github-release-automation.md`.
