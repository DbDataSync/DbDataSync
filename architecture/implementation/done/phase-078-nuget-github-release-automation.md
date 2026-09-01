# Phase 78 — Automatic GitHub Release on a release tag, versioned by date/time

**Status**: Done.
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

---

# Outcome

A new `.github/workflows/release.yml`, and a comment on the one line of `DataSync.Cli.csproj` this
changes the meaning of. `ci.yml` is byte-for-byte untouched.

### The two calls the doc left open

**A dedicated workflow, not a job in `ci.yml`.** The doc leaned this way and nothing argued against it
once written. A job in `ci.yml` would have had to carry an `if:` guarding every step against the
tag trigger, and `ci.yml`'s own `on:` block would have had to grow tags it does not otherwise care
about — which is precisely the coupling the phase was told not to introduce, since the requirement
that the per-push `package` job be *unaffected* is easiest to guarantee by not editing its file.

**The release is nupkg-only; the Docker image stays out.** Checked before deciding, as instructed:
`ci.yml`'s `package` job builds the image as `datasync:ci` and runs a health check against it, but
never pushes it to any registry — no `docker push`, no `ghcr.io` login, no registry anywhere in the
repo. So there is no published image *reference* a release could point at. Attaching one would mean
first deciding to publish images somewhere, with a registry and a retention policy behind it. That is
a real decision and not this phase's to make.

### The interpretation the plan asked to have flagged

Built as the plan read it: **the tag is a gate, the clock is the version.** `release/v*` says "cut one
now" and its text is never parsed; `-p:Version=$(date -u +%Y.%m.%d.%H%M)` decides what ships. Flagging
it plainly as both documents asked: if the intent was that the *tag* carries the version and CI reads
it out, that is a smaller change than this one — delete the version step and substitute
`${GITHUB_REF_NAME#release/v}` — and nothing else in the workflow would move.

### What local verification actually caught

Worth recording because it would have broken the first real release on any single-digit month or day,
and CI would have been the place it surfaced.

NuGet **normalizes** the version it is handed: leading zeros are stripped. A `-p:Version=2026.09.01.1805`
ships as `2026.9.1.1805`, and that shorter string is what the `.nupkg` filename, the package metadata
and `datasync version` all report. The first draft of the smoke test asserted the tool reported the
string that was *asked for*, which packing locally showed to be false — `datasync version` printed
`2026.9.1.1805` against a requested `2026.09.01.1805`.

The fix is not to reimplement NuGet's normalization rules in shell, which would be a second source of
truth free to drift. The workflow reads the shipped version back off the filename NuGet chose, and the
title, the notes and the assertion all use that. The requested and shipped strings are both echoed to
the log so a future divergence is visible rather than silent.

The date format itself is unchanged from the spec — `YYYY.MM.DD.HHmm` is still what is requested, and
normalization is a display and comparison concern, not an ordering one: NuGet compares those parts
numerically, so releases still sort in the order they were cut.

### The smoke test is deliberately duplicated

The install-and-run check is the same one `ci.yml`'s `package` job already does. Repeating it is not an
oversight. A GitHub Release is public and awkward to withdraw, and the version-reporting assertion is
load-bearing in a way the CI copy's is not: it is what would catch `-p:Version` being silently ignored,
which would otherwise ship a release quietly labelled `0.1.0`.

### How it was verified

No real release was cut — creating a public GitHub Release is a visible, hard-to-reverse action and
needs a human's sign-off, so this was verified in pieces instead:

- `release.yml` parses as YAML; trigger, permissions and step list all read back as intended.
- `date -u +%Y.%m.%d.%H%M` produces `2026.09.01.1805` — the shape the spec asks for.
- The version step's `>> "$GITHUB_OUTPUT"` write was run standalone against a real file.
- `dotnet pack -p:Version=...` was run locally: the command-line property does override the csproj's
  `<Version>`, producing `DataSync.2026.9.1.1805.nupkg`.
- That package was installed into a clean tool root and `datasync version` reported `2026.9.1.1805` —
  which is both the proof the override reaches the assembly and the source of the normalization finding
  above. The filename-readback and the assertion were then run against those real artifacts and pass.
- The release-notes heredoc was rendered standalone: it comes out at column 0 with its backticks and
  code fence intact, with `gh release create` stubbed out.
- `dotnet build -c Release` clean, and `dotnet test --filter "Category!=Integration"` **894 passed, 0
  failed** — the same total phase 75 recorded, so the csproj comment broke nothing.

What is *not* verified is the one thing only a real run can show: that `gh release create` succeeds with
the default `GITHUB_TOKEN` under `contents: write`. That is the documented behaviour and needs no new
secret, but it is untested here.

### To test it live

Push a throwaway tag and delete what it makes:

```
git tag release/v0.0.0-test && git push origin release/v0.0.0-test
```

Watch the Release workflow. It should produce a release titled `DataSync <today's UTC date/time>` with
one `.nupkg` asset whose version is date-shaped rather than `0.1.0`. Then clean up both the release and
the tag — the release does not disappear with the tag:

```
gh release delete release/v0.0.0-test --yes
git push --delete origin release/v0.0.0-test && git tag -d release/v0.0.0-test
```

Confirm at the same time that an ordinary push to `main` still runs `package` and still only
artifact-uploads, cutting no release.

### Note

The tag pattern is `release/v*`, so `release/v0.0.0-test` matches and so does `release/v1`. A tag
without the `v` — `release/1` — silently does nothing at all. That is the intended shape of the gate
but it is a quiet failure mode, and the likeliest way a future operator's release "doesn't run".
