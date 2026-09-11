# Phase 127 — publish `DbDataSync` to nuget.org via Trusted Publishing (OIDC)

**Status**: Done.
**Plan reference**: `architecture/planning/done/nuget-org-publishing-and-github-hosting-move.md` §2.
Depended on phase 126 (the repo needs to exist at its final `DbDataSync/DbDataSync` location, since a
Trusted Publishing policy names that exact repo).

## What this built

`release.yml` now publishes `DbDataSync.Cli` (nuget.org package id `DbDataSync`) to a real public
feed, with no long-lived secret anywhere, and creates its own release tag once that publish is
confirmed. The trigger changed mid-phase, from a pushed tag to a manual dispatch — both shapes are
described below because both were built, run for real, and one deliberately replaced the other; see
*The trigger redesign*.

### Trusted Publishing (OIDC), not a stored API key

`permissions` gained `id-token: write` alongside `contents: write` — required for the job to request
a GitHub OIDC token at all; its absence fails that request silently rather than erroring loudly, so
getting it right the first time mattered. A `NuGet login` step (`uses: NuGet/login@v1`) exchanges
that token for a one-hour, single-use nuget.org API key, requested immediately before the push that
spends it — never earlier in the job. Its `user:` input is `${{ secrets.NUGET_USER }}`, the nuget.org
profile name the Trusted Publishing policy is registered under (not sensitive in itself; stored as a
secret only because that's nuget.org's own documented pattern). `dotnet nuget push` then targets
`https://api.nuget.org/v3/index.json` with `--skip-duplicate`, using the exact resolved nupkg path
(`steps.pack.outputs.nupkg`) rather than a glob — see bug 1 for why that distinction is load-bearing,
not stylistic. Moved before "Publish the GitHub Release" so that step's notes can say "published to
nuget.org" truthfully.

**The one manual, nuget.org-side step nothing in this repo could do for itself**: signing in to
nuget.org, account menu → **Trusted Publishing** → *Add a new policy*, naming Repository Owner
`DbDataSync`, Repository `DbDataSync`, Workflow File `release.yml` (file name only, not the
`.github/workflows/` path), no Environment, package glob `DbDataSync` (exact, not a prefix). Confirmed
working the moment it was created — no "pending policy" 7-day window was observed, consistent with
the repo already being public (phase 126) by the time the policy was made.

### A `beta` input ships a real SemVer 2 prerelease

Under the workflow_dispatch redesign, `on: workflow_dispatch: inputs: beta` (boolean, default
`false`). `Compute the version` reads `${{ inputs.beta }}` and appends `-beta` to the computed
version when set, emitting a `prerelease` step output that `Publish the GitHub Release` reads to pass
`--prerelease` to `gh release create` — so the GitHub side agrees with nuget.org rather than showing
a beta build as "Latest release." nuget.org genuinely hides a `-beta` version from a plain
`dotnet tool install` (confirmed twice, empirically — see *How it was verified*); only an exact
`--version` or `--prerelease` finds it.

### The trigger redesign — from a pushed tag to a manual dispatch, 2026-09-11

The original design (phase 78) triggered on `push: tags: ["release/v*"]`, version computed from the
clock rather than the tag's text — deliberate, so the two could never disagree. What that design
didn't anticipate: **iterating on the pipeline itself**, which a brand-new Trusted Publishing setup
all but guarantees on its first real use. Getting this phase's own pipeline working took five
iterations (bugs 1–4 below), each needing `release/v1-beta` deleted and recreated by hand before a
retry could even start, because the tag was both the trigger and the thing a naive retry collided
with.

Moved to `workflow_dispatch` with the `beta` boolean input: an operator runs it from the Actions tab
(or `gh workflow run release.yml -f beta=true`), no tag involved in starting anything. The pipeline
creates and pushes the release tag *itself* — a lightweight tag, not annotated (the "who/why" an
annotation would carry already lives in the GitHub Release's own notes) — named after the version it
computed, using nothing but `actions/checkout@v4`'s default `persist-credentials` (no new
permission), and only *after* the nuget.org push is confirmed. `gh release create "$VERSION"` then
uses that already-pushed tag directly. A tag existing is now proof a version really shipped; a failed
run leaves no tag to clean up, and a retry is just running the workflow again with a freshly
computed, always-different version. `phase-078`'s own doc got a superseded-note pointing here; the
version-by-clock computation and "the trigger's own text carries no meaning for the shipped version"
reasoning it established are otherwise unchanged — only what starts a run, and what happens to the
tag, moved.

### `.claude/skills/nuget-release/` — a skill for cutting a release

Built once the pipeline itself was proven, to make running it a documented, repeatable procedure
rather than something re-derived from memory each time. `SKILL.md` explains what gets published
(this package only, never `DbDataSync.Drivers.Abstractions`), the beta/stable default (stable unless
asked otherwise), what a successful run means, and the real indexing-lag findings from bugs 5–6.
`scripts/release.sh` wraps the dispatch-find-watch sequence done by hand three times this phase: `gh
workflow run` doesn't hand back a run id, so it matches the newest `workflow_dispatch` Release run
created after the moment it dispatched, then `gh run watch --exit-status`s it. `.gitignore`'s
blanket `.claude/` exclusion (session locks, scheduled-task state) got a carve-out for
`.claude/skills/` — a skill is process knowledge worth versioning, not per-machine state.

## How it was verified

Three real runs, escalating in what they proved, plus a fourth pass to correct one wrong assumption:

1. **First real run, under the original tag-push trigger** (`release/v1-beta` → `ff6d02d`) — a
   genuinely first-ever publish through a brand-new OIDC policy to a public feed, deliberately a
   prerelease rather than risking a permanent version on the very first attempt. Five iterations to
   get green (bugs 1–4). Once green: the pack + smoke-test steps passed; `Compute the version`
   emitted `2026.9.11.425-beta` and `prerelease=true`; `NuGet/login` succeeded (the OIDC exchange and
   the policy match, for real); `dotnet nuget push` succeeded; a plain, unversioned
   `dotnet tool install --tool-path <clean dir> DbDataSync` found **nothing** (nuget.org genuinely
   treated it as hidden); the exact `--version 2026.9.11.425-beta` install succeeded and
   `dbdatasync version` reported that exact string; the GitHub Release was created `prerelease: true`
   with one asset and a working exact-version install command.
2. **The same beta shape, under the redesigned `workflow_dispatch` trigger** (`gh workflow run
   release.yml -f beta=true` → `ef64c85`) — fully green on the first attempt. Dispatched correctly
   with no tag involved; `Compute the version` emitted `2026.9.11.450-beta`; `Tag this commit with
   the released version` created and pushed the tag using nothing but `actions/checkout@v4`'s default
   credentials; the tag was confirmed present on `origin` and **absent** on `old-origin`, proving the
   in-runner `git push origin` targets the repo the workflow lives in, unrelated to any local git
   remote naming; `gh release create` used the already-existing tag directly.
3. **A real, non-beta dispatch** (`.claude/skills/nuget-release/scripts/release.sh`, no `--beta`, →
   `c0e5c65`) — fully green on the first attempt via the skill. `2026.9.11.532`, `prerelease: false`,
   shown as **Latest** on GitHub — no `-beta` suffix, no prerelease flag. A plain, unversioned,
   `--no-cache`d `dotnet tool install DbDataSync` from a clean tool-path eventually succeeded (see bug
   6 for how long that actually took, and why "eventually" needed real measurement rather than
   assumption) and `dbdatasync version` reported `2026.9.11.532+c0e5c65…`, matching the exact commit.

`ci.yml`'s per-push `package` job was unaffected throughout — still packs and artifact-uploads on
every push to `main`, never attempts a NuGet push.

## Decisions and real bugs found

Six, found across the three runs above — none guessed at, all caught by running the real thing and
reading what actually came back:

1. **The pack step's own glob matched a second, unrelated package.** `dotnet pack
   src/DbDataSync.Cli/DbDataSync.Cli.csproj -o artifacts -p:Version=…` also side-effect-packs
   `DbDataSync.Drivers.Abstractions` into the same directory — `-o`/`-p:Version` on the command line
   are global MSBuild properties applying to the whole build graph, and that project has had
   `GeneratePackageOnBuild=true` since phase 109e. `ls artifacts/DbDataSync.*.nupkg` matched both
   filenames (both package ids start with `DbDataSync.`) and picked the wrong one — the first real tag
   this repo ever pushed is what caught it; nothing before this phase had run `dotnet pack` on the Cli
   project with a global `-o`/`-p:Version` override at all. **Not cosmetic**: the same broad glob was
   also used by the `dotnet nuget push` and `gh release create` attach steps, so left unfixed the very
   next steps would have published `DbDataSync.Drivers.Abstractions` to nuget.org too — a real scope
   violation, not a wrong log line. Fixed by anchoring the glob to a digit right after the package id
   (a version always starts with one) and capturing the one resolved path as a step output every later
   step reuses, rather than each re-globbing.
2. **The smoke test couldn't see its own just-packed prerelease package.** `dotnet tool install
   --add-source artifacts DbDataSync` (no `--version`) failed on the very first beta tag with "dbdatasync
   is not found in NuGet feeds https://api.nuget.org/v3/index.json, …/artifacts" — from *both* sources
   it checked, not just nuget.org. `--add-source` only **adds** to the default source list (confirmed
   with `dotnet nuget list source` — nuget.org is already registered by default), and an unversioned
   `dotnet tool install` considers only stable versions by design, which hides a `-beta` package in a
   local folder feed exactly as it would on nuget.org itself. This is a real gap in the beta-versioning
   addition itself, not a pre-existing issue: nothing before this phase ever packed a prerelease version
   in this step. Fixed by passing `--version "$SHIPPED"` explicitly, which bypasses the
   stable-only default and is a strictly stronger assertion than before (proves *this exact* version
   installs, not merely that installing found something whose reported string happens to contain it).
3. **`dbdatasync version` structurally cannot report a prerelease label, and never has.**
   `src/DbDataSync.Cli/Program.cs`'s `Version()` read `Assembly.GetName().Version` — a strict 4-part
   numeric `System.Version` — which the SDK derives from `<Version>`'s numeric core alone, silently
   dropping any prerelease suffix. Install succeeded (`Tool 'dbdatasync' (version '2026.9.11.415-beta')
   was successfully installed`), but the *running binary* then reported plain `2026.9.11.415`, failing
   the smoke test's substring check for real — not a test bug, a genuine "the tool can't tell you which
   build you're running" gap. **Pre-existing, not introduced by this phase**: the dev-build
   `-alpha.<seconds>` suffix (landed on `main` the day before this phase, in "Move the local-tool
   version convention into MSBuild") had been silently truncated by every `dbdatasync version` since —
   nothing had ever asserted on the exact string before this run did. Fixed in `Program.cs`: read
   `AssemblyInformationalVersionAttribute` instead (falling back to `AssemblyName.Version` if somehow
   absent) — SDK-style projects stamp it with the *whole* `<Version>` string, prerelease label
   included, plus a `+<git-sha>` build-metadata suffix the SDK adds automatically from source control
   (confirmed harmless: NuGet's own package version is unaffected — metadata after `+` is excluded from
   `PackageVersion` by SemVer 2 convention — and the smoke test's existing substring match already
   tolerates the extra suffix without any further change). Verified locally before pushing: a plain dev
   build reported `2026.09.11.0418-alpha.10+<sha>`; a build with `-p:Version=2026.9.11.415-beta`
   reported `2026.9.11.415-beta+<sha>`; the full `DbDataSync.Cli.Tests` suite (104 tests) stayed green.
   **This local verification is itself why bug 4 wasn't caught until the next real run** — it used a
   hand-typed, already-normalized version string, sidestepping the exact discrepancy that only shows
   up when the version comes from `date`'s own zero-padded output.
4. **The computed version and the assembly's reported version disagreed on zero-padding, always —
   not just that day.** `date -u +%Y.%m.%d.%H%M` zero-pads every field (`2026.09.11.0421`); NuGet
   strips a leading zero from each dot-separated numeric component of a version core
   (`2026.9.11.421` — the pack step's own comment already documented this, for the nupkg side).
   `AssemblyInformationalVersionAttribute` (bug 3's fix) does not go through that normalization at
   all — it is a raw echo of whatever `-p:Version` literally received — so `$SHIPPED` (read off the
   normalized nupkg filename) and the running binary's reported version were never going to agree as
   strings once bug 3 made the binary report its full version honestly. The smoke test's substring
   check failed for real, with genuinely different numeric text (`2026.09.11.0421-beta` vs.
   `2026.9.11.421-beta`) — not a formatting nit. This is exactly the discrepancy the pack step's own
   long-standing comment was already written to route around ("Rather than reimplement those
   normalization rules here … the shipped version is read back off the filename"), except that
   workaround only ever reconciled the nupkg side; nothing made the *assembly's own* reported string
   agree until now, because nothing before bug 3 read anything past the numeric core the SDK derives
   automatically.

   Fixed at the source rather than reconciled afterward: `Compute the version` strips the leading
   zeros itself (`$((10#$part))` per dot-separated field — forcing base-10 avoids bash reading `08`/`09`
   as invalid octal literals) *before* the string ever reaches `-p:Version`, so what MSBuild receives
   is already in NuGet's normalized form. `$VERSION` and `$SHIPPED` become the identical string by
   construction, for every field, every run — not only when the clock happens to need no padding.
   Verified with a genuine end-to-end local rehearsal, not a hand-typed shortcut: pack with the
   pre-normalized version → install into a clean tool-path exactly as the workflow does → run the
   installed binary's own `version` command → confirm the substring check passes. It did, across
   several synthetic zero-padded dates chosen to exercise every field (`2026.01.05.0421`,
   `2026.12.31.2359`, `2026.10.10.1000`), not only that day's.
5. **A workflow's own "the push succeeded" is not "the package is installable" — nuget.org indexes a
   brand-new package id in two separate, independently-lagging stages.** After a fully green run
   (`Push to nuget.org` reported "Your package was pushed"), the raw flat-container blob
   (`v3-flatcontainer/dbdatasync/…/dbdatasync.….nupkg`) was serving `200` within about a minute, but a
   real `dotnet tool install --tool-path <clean dir> DbDataSync --version 2026.9.11.425-beta` from an
   uninvolved shell still failed: *"Version 2026.9.11.425-beta of package dbdatasync is not found in
   NuGet feeds https://api.nuget.org/v3/index.json."* `dotnet tool install` resolves versions through
   the **registration index** (`v3/registration5-gz-semver2/dbdatasync/index.json`), a separate
   resource from the flat container, which returned `404` for several more minutes after the blob
   itself was already reachable. Not a bug in this repo's workflow — nothing to fix here — but a real
   trap for verifying a *first-ever* publish of a package id specifically: checking the flat container
   (or the package's nuget.org web page) says nothing about whether `dotnet tool install` can actually
   find it yet. Confirmed by polling the registration index directly rather than assuming the flat
   container's availability generalized.
6. **The indexing lag isn't limited to a package id's first-ever publish, and a leaning stated
   earlier in this doc turned out wrong when actually measured.** The real, non-beta release
   (`2026.9.11.532`, dispatched via the `nuget-release` skill) was assumed to index faster than the
   two betas before it — "`DbDataSync` is a real, established package now." Measured instead: **893
   seconds (~15 minutes)** from `gh release` "published" to a plain `dotnet tool install DbDataSync`
   (no version pin) succeeding — slower than either beta, not faster. Likely explanation: both
   earlier publishes were prereleases, which nuget.org excludes from ordinary search/listing
   entirely, so `2026.9.11.532` was plausibly this package id's first-ever *listed* version — a
   different, and apparently slower, event than "first publish of any kind." Also found while
   measuring this: a `curl` check against the registration index's bare existence is not proof
   either — it reported the version's string present (likely as a page-range bound, not a resolvable
   leaf entry) well before an actual unversioned `dotnet tool install` succeeded, giving a false
   "it's ready" signal. The only check that means anything is the real install command, retried with
   `--no-cache` (that flag matters too: `dotnet` keeps its own local HTTP cache independent of
   nuget.org's server state, and a `404` cached from an attempt made before the package existed can
   make an already-indexed package look absent to that one machine). The `nuget-release` skill's own
   indexing guidance was corrected to state the measured number rather than the unverified
   assumption.

## What this phase does not build

- Publishing `DbDataSync.Drivers.Abstractions` or any other package.
- An approval/environment gate on the nuget.org push — deliberately, per the parent planning doc.
- A reserved `DbDataSync*` prefix on nuget.org.
- Anything on `ci.yml` — this phase touches only `release.yml`.
- A `README.md`/`CONFIG.md` "install from nuget.org" callout — worth doing, not done here; a small
  follow-up whenever someone is next in those files.

## Open questions — resolved

- **Whether the "pending policy" 7-day window applies to a repo that was private very recently**:
  resolved in effect, if not directly observed — the account owner created the policy outside this
  session's own view, but the very first real run (bug 1's `release/v1-beta`) exchanged its OIDC
  token and pushed successfully, which a policy stuck in a non-functional pending state could not
  have done. Whatever the UI showed at creation time, the policy was fully active by first use. The
  repo being public (phase 126) before the policy was made is consistent with nuget.org's documented
  reasoning for that window existing at all (it guards against a deleted-and-recreated repo/owner).
