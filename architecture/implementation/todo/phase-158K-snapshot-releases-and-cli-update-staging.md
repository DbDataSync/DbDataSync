# Phase 158 — snapshot releases for every promoted `test` build, and `dbdatasync update` to list, choose, stage and print the install commands (planned)

**Status**: **Implemented; not yet done.** Everything that can be verified before a release exists is
built and verified (see "Progress" at the end). It stays in `todo/` — the same way phase 127 did — until the one
thing it cannot prove without GitHub does happen: `publish-snapshot.yml` running for real after a release has
put it on `main`. **First of two ordered phases** — phase 159
(`phase-159K-automated-update-from-cli-and-web-console.md`) builds on the library and the plan model
introduced here.
**Plan reference**: `architecture/planning/done/snapshot-packages-on-github-packages.md` and
`architecture/planning/done/self-update-and-release-channels.md`.

## Why

Stable and beta builds are already anonymous on nuget.org (`release.yml`, `beta` checkbox). Builds that
have only reached `test` have no distribution at all: nothing packs them (`ci.yml`'s `package` job runs only
on a release tag), and GitHub Packages' NuGet feed would need a personal access token to *read*, even for a
public repo. Nothing here needs a feed: tested 2026-09-19 against the real `2026.9.18.1918` release, with
nuget.org excluded via `--configfile` so the source under test was the only one —

- a raw release-asset URL as a NuGet source **fails** (NuGet appends `FindPackagesById()`; GitHub answers
  `618 jwt-not-provided`);
- an anonymously downloaded nupkg in a **plain folder installs cleanly** as the only source.

So a snapshot is a GitHub *prerelease* with the nupkg attached, and a client downloads it and installs from
the folder. This phase builds both ends: the workflow that publishes them, and the CLI that finds them.

This phase deliberately **never replaces running code**. `dbdatasync update` downloads at most one file
into a staging folder and prints the commands; the operator runs them. That leaves phase 159's hard
questions — a process replacing itself, trust, privilege — out of this phase entirely, and it is useful on
its own: today there is no way to try a `test` build without building it.

## Part A — publish a snapshot for every promoted `test` build

New workflow `.github/workflows/publish-snapshot.yml`.

**Trigger.** `workflow_run` on "Promote dev to test", `completed`, success only — never a PR, never a fork.
Two things checked against the real repo shape this:

- CI does **not** run on `test`: `promote-test.yml` pushes `dev:test` under `GITHUB_TOKEN`, which does not
  trigger other workflows (`test` and `dev` both sit at `65615e7`; `dev` has a green CI run for it, `test`
  has none). So the trigger is the promotion, not CI-on-`test`.
- The promote run's own `workflow_run.head_sha` is **not** the promoted commit (its runs report the
  default branch's SHA, `3462587`, while moving `test` to `65615e7`). So the workflow checks out `test`,
  reads `git rev-parse HEAD`, and treats *that* as the commit to build.

**Gate.** Require a successful CI run for that exact SHA, the same Actions-API lookup `release.yml` uses.
`promote-test.yml` checks out `dev`'s tip rather than the SHA CI passed on (a push landing in between would
advance `test` to an untested commit), so this gate is also what stops a snapshot being cut from one. No
green run: exit **success** with a notice and publish nothing — the next promotion covers it.

**Serialisation.** `concurrency: { group: publish-snapshot, cancel-in-progress: false }`. Re-running for a
SHA that already has its snapshot is a no-op (tag exists → skip), not a failure.

**Build.** Checkout `test` (`fetch-depth: 0`), setup dotnet/node, build the SPA (phase 137: `dotnet pack` of
the Cli never triggers the SPA build), `dotnet pack src/DbDataSync.Cli/DbDataSync.Cli.csproj -c Release -o
artifacts -p:Version=$VERSION`. Only the tool package is a snapshot: the anchored glob
`artifacts/DbDataSync.[0-9]*.nupkg` (the same trap `release.yml` documents — packing the Cli also packs
`DbDataSync.Drivers.Abstractions`) picks the right file, and Abstractions is not distributed as a snapshot
(fixed `0.1.0`, no consumer of a snapshot).

**Version.** `YYYY.M.D.HHmm-snapshot.g<shortsha>`, e.g. `2026.9.19.1432-snapshot.g65615e7`.
- The clock part comes from the **commit's committer timestamp** (UTC), not the run's, so a re-run of the
  same SHA yields the same version and is idempotent.
- Leading zeros normalised exactly as `release.yml` does, or the assembly's informational version and the
  nupkg's disagree forever.
- The `g` is deliberate: a prerelease identifier that is all digits must not have a leading zero in
  SemVer 2, and an abbreviated SHA like `0123456` is exactly that. `git describe` uses the same prefix.
- A prerelease sorts below the same core's stable version and `dotnet tool install` ignores prereleases
  unless asked, so a snapshot can never be picked up as a release by accident.

**Verify before publishing** (a snapshot that doesn't install is worse than no snapshot):
1. Assert the nupkg's `tools/*/any/wwwroot/` is non-empty (the phase 137 check; copied rather than shared,
   to leave `release.yml` untouched).
2. Install it into a clean tool root from the artifacts folder **with a `--configfile` that `<clear/>`s
   every other source** — `--add-source` alone *adds* to nuget.org and can make a broken local package look
   fine — and check `dbdatasync version` reports the computed version.
3. Write `DbDataSync.<version>.nupkg.sha512`, the base64 SHA-512 of the nupkg (NuGet's own sidecar format).

**Publish.** `gh release create "snapshot-$VERSION" --prerelease --latest=false --target "$SHA"` with the
nupkg and its `.sha512` attached; notes carry the commit, its subject, the CI run link and
`dbdatasync update --to <version>`. The tag prefix is `snapshot-` — no slash, so a tag is always one URL
path segment — and it is distinct from a release's bare-version tag so the two can never be confused.
`--latest=false` keeps the repo's "Latest" badge on the real release.

**Retention: newest N** (decided 2026-09-19). After a successful publish, list releases, keep those whose
tag starts with `snapshot-` **and** that are prereleases, order by creation time newest first, and
`gh release delete --cleanup-tag --yes` everything past `keep` (default **20**, one expression in the workflow:
`KEEP: ${{ inputs.keep || '20' }}`; ~48MB each, storage is free for a public repo, so this is about clutter, not
cost). Both
conditions are required so nothing that isn't a snapshot can ever be deleted. A `workflow_dispatch` input
`keep` overrides it, so the pruning can be exercised with a small number; the step runs even when nothing was
published, so a dispatch with a smaller `keep` prunes on its own, and `keep` is validated as a positive whole
number (0 would delete the snapshot the run just made).

**Permissions**: `contents: write` (create/delete releases and tags), `actions: read` (the CI lookup). No
`packages`, no `id-token`, no stored secret.

**Bootstrap constraint.** `workflow_run` workflows are read from the **default branch**, and `main` is still
the default (`architecture/branching-and-releases.md` lists moving it as undecided). `main` only ever moves
at a release cut, so `publish-snapshot.yml` does nothing until a release puts it there — the same reason
`promote-test.yml` took a release to become live. Rollout is therefore: land on `dev` → promoted to `test`
→ cut a release → snapshots begin with the next promotion. To prove the job's steps before that, run them
on a throwaway branch with a temporary `push` trigger and a scratch tag prefix
(`snapshot-scratch-`), then delete the trigger and the scratch releases.

## Part B — `dbdatasync update`

### A shared library, because phase 159 needs it from the API

`Cli` references `Api`, not the other way round, so anything the web console will also call cannot live in
`DbDataSync.Cli`. New class library **`DbDataSync.Updates`** (the repo's pattern for a concern with two
consumers — `DbDataSync.Libraries`, `DbDataSync.Certificates`), referenced by `Cli` now and `Api` in
phase 159, with `DbDataSync.Updates.Tests` beside it. Add both to `DbDataSync.slnx`.

- **`ReleaseCatalog`** — lists releases per channel, newest first.
- **`SnapshotStager`** — downloads and verifies one snapshot.
- **`InstallLocator`** — works out how this copy of the tool was installed.
- **`UpdatePlan`** — a pure, immutable record: installed version, target version, channel, install
  location, source (nuget.org, or a staged folder), whether the operation is an *update* or an
  *uninstall + install*, and the service (if any) to stop and start. **Phase 158 renders it as text;
  phase 159 executes it.** One object means what is printed and what is later run cannot drift.

### Channels and sources (hard-pinned, never user-supplied)

| channel | source | how listed |
| --- | --- | --- |
| `stable` | nuget.org, package `DbDataSync`, no `-` in the version | flat container `api.nuget.org/v3-flatcontainer/dbdatasync/index.json` (anonymous) |
| `beta` | nuget.org, `-beta` versions | same index |
| `snapshot` | this repo's GitHub releases, tag prefix `snapshot-`, prerelease | `api.github.com/repos/DbDataSync/DbDataSync/releases` (anonymous) |

The sources are constants, not options. A `--source <url>` that made an update tool fetch and print
install commands for code from anywhere is not a convenience worth having here.

- Versions are always `YYYY.M.D.HHmm[-label]`, so the publish time is derived from the version and needs no
  extra request. Ordering uses NuGet version semantics (`NuGet.Versioning` if it is already in the graph,
  otherwise a small comparer over the four numeric parts plus label).
- Unauthenticated GitHub API access is limited to 60 requests/hour per IP. Use `GITHUB_TOKEN` / `GH_TOKEN` if
  set (raises it; never required). On a rate-limit response, say when it resets and name the variable
  instead of printing an HTTP status.
- Send a `User-Agent` (GitHub rejects requests without one). Page snapshots until `--limit` are found or
  three pages have been read.

### Usage

```
dbdatasync update                          list, then choose (interactive)
dbdatasync update --list [--channel stable|beta|snapshot|all] [--limit N] [--json]
dbdatasync update --to <version>           non-interactive; the channel is inferred from the version
dbdatasync update --channel snapshot       interactive, restricted to one channel
    --stage-dir <dir>                      where a snapshot is staged
```

The interactive form groups by channel, newest first, numbered, marking the installed version and anything
newer than it; choosing a number or typing a version selects it, an empty line cancels. When stdin is
redirected and no `--to` is given it behaves as `--list` rather than waiting on a prompt. Exit code 0 on
success, 1 on any failure (network, checksum, unknown version) — the CLI's existing convention.

### Staging (snapshot only)

- Download the nupkg and its `.sha512` into `<stage-dir>/<version>/`. Default stage dir:
  `<Environment.SpecialFolder.LocalApplicationData>/DbDataSync/updates` — per-user, because in this phase the
  *user* runs the printed commands (phase 159 stages under the data directory instead, as the service).
- Stream to a `.partial` file and rename on success. Verify the SHA-512 against the sidecar; **delete on
  mismatch** and fail. If a verified copy is already staged, reuse it without downloading.
- Be honest in the output about what the check covers: the sidecar is served from the same place as the
  file, so it catches truncation and corruption, **not** a tampered release. Authenticity of a snapshot
  rests on TLS to `github.com` and this repo's own release permissions.
- Stable and beta are **not** staged: `dotnet` fetches them from nuget.org itself, and where the SDK verifies
  package signatures (always on Windows; on Linux/macOS depends on the SDK version — confirm on the one in
  use) nuget.org's repository signature is checked in the bargain.

### The plan, and what is printed

`InstallLocator` reads `Environment.ProcessPath` for the tool layout (`…/.store/dbdatasync/<ver>/…`):
the directory above `.store` is the tool root — the per-user `~/.dotnet/tools` (a **global** install) or
a `--tool-path` directory such as the machine-wide one phase 123 creates. Anything else — a dev build, a
`dotnet run`, the container's `/app/DbDataSync.Cli.dll` — is **not updatable this way**: the command still
lists and (for a snapshot) stages, but declines to print install commands and says why (for a container: pull
the new image tag).

**A lower version is a different operation.** Verified 2026-09-19: `dotnet tool update … --version <lower>`
is refused ("The requested version … is lower than existing version"). So the plan compares the target with
the installed version: higher → `dotnet tool update`; lower → `dotnet tool uninstall` then `install`; equal →
"already installed", nothing to print.

The service comes from the phase 135 `ServiceRegistration` marker at the resolved data directory: if a
service is registered, the plan includes stopping and starting it (`sc.exe stop|start DbDataSync` on
Windows — elevated; `sudo systemctl stop|start dbdatasync` on Linux), otherwise it tells the operator to stop
any running `dbdatasync serve` first. The Windows service and its worker processes hold the tool's files
open, so stopping first is not a nicety there.

```
Installed   2026.9.18.1918
Selected    2026.9.19.1432-snapshot.g65615e7  (snapshot)
Staged      ~/.local/share/DbDataSync/updates/2026.9.19.1432-snapshot.g65615e7  (checksum verified)

Nothing has been changed. To install it:

  1. Stop the service
       sudo systemctl stop dbdatasync
  2. Update
       sudo dotnet tool update --tool-path /opt/dbdatasync DbDataSync --add-source ~/.local/share/DbDataSync/updates/2026.9.19.1432-snapshot.g65615e7 --version 2026.9.19.1432-snapshot.g65615e7
  3. Start the service
       sudo systemctl start dbdatasync
  4. Check
       dbdatasync version
```

Stable and beta print the same shape without `--add-source` (an exact `--version`, which needs no
`--prerelease` flag). Every command is on **one line**, however long: a continuation is `\` in one shell, a
backtick in another and `^` in a third, and a plan that only pastes into the right shell is not one that pastes.

## Checkpoints

1. **`DbDataSync.Updates` project + tests project**, added to the solution. `UpdatePlan` and the version
   comparer first (pure), with tests.
2. **`ReleaseCatalog`** against recorded JSON fixtures of both real sources (captured from the live APIs, not
   invented): channel classification, ordering, the installed/newer marks, non-snapshot and non-prerelease
   GitHub releases filtered out, pagination, rate-limit message.
3. **`SnapshotStager`** over a fake `HttpMessageHandler`: happy path; checksum mismatch deletes the partial
   and fails; truncated body; already-staged reuse; a `.partial` left by an interrupted run is not trusted.
4. **`InstallLocator`** as a pure function over (path, platform): global, `--tool-path`, Windows path forms,
   and the not-a-tool-install cases (dev build, container).
5. **The plan renderer** with exact expected text per case (inline raw string literals — clearer than
   golden files for text this short):
   update vs uninstall+install, each service situation, each platform, snapshot vs stable/beta, not
   updatable.
6. **`UpdateCommand`** wired into `Program.cs` dispatch and `Help`, interactive prompt and `--list --json`.
7. **`publish-snapshot.yml`** per Part A, proven first on a throwaway branch with the scratch prefix.
8. **Docs**: `docs/install.md` (channels, the command, what is and isn't verified) and
   `architecture/branching-and-releases.md` (snapshots are now a fourth artifact of the model, and
   the default-branch constraint above belongs in its "Not yet decided" list).

## How to verify when built

- Unit suites above green; nothing in them touches the network.
- **Against the real feeds**: `dbdatasync update --list` shows real stable/beta versions from nuget.org and,
  once a snapshot exists, real ones from GitHub.
- **End to end**: stage a real snapshot, run the printed commands into a scratch `--tool-path` (exactly what
  this design was tested with by hand), and confirm `dbdatasync version` reports the snapshot.
- **Retention**: publish at least three snapshots, then dispatch with `keep=2` and confirm exactly the older
  ones and their tags go, and no non-snapshot release is touched.
- **Windows** needs a real Windows host to confirm the printed sequence (stop service → install → start)
  works with the locked-file behaviour it exists for, as phases 135/136/140 needed.

## Open questions

- **N.** Defaulted to 20; it is one line in the workflow.
- **`update` versus `upgrade`/`self-update` as the command name.** `update` matches `dotnet tool update`,
  whose commands it prints; `upgrade` would read better for a downgrade. Left as `update`.
- **Whether `dbdatasync version` should also say which channel the running build came from** (a version
  containing `-snapshot.` or `-beta` already does, textually). Cheap, not required here.
- **Update checks that phone home.** This phase only contacts nuget.org and GitHub when the operator runs
  the command. Anything periodic belongs to phase 159 and needs its own answer for air-gapped installs.

## Progress

Built, with each piece's verification:

- **`DbDataSync.Updates`** (+ `DbDataSync.Updates.Tests`, both in `DbDataSync.slnx`): `ReleaseVersion` (NuGet
  ordering, safe-to-name-a-path parsing), `ReleaseCatalog`, `SnapshotStager`, `InstallLocator`, `UpdatePlan` /
  `UpdatePlanner`, `UpdatePlanRenderer`. **118 unit tests**, no network. The nuget.org fixture is a real captured
  response; the GitHub one is the real release list (trimmed) with synthetic snapshot entries added in front,
  since no snapshot exists yet — three valid ones and seven each broken one way the catalog must skip. A mutation
  check (disabling the checksum comparison) made the two checksum tests fail, as it should.
- **`dbdatasync update`** (`UpdateCommand`, wired into `Program.cs` and `Help`): **34 end-to-end tests** through
  the real catalog, stager, planner and renderer against a fake network and a fake machine (Windows, container,
  development build, each service manager, interactive and non-interactive).
- **`publish-snapshot.yml`**: YAML parses; every shell step passes `bash -n`; the version computation, the
  checksum (`openssl … | base64 -w0`, byte-identical to an independent implementation), the keep validation and
  the retention `jq` filter (deletes only the three oldest of five snapshots and leaves a beta, a stable release
  and a look-alike non-prerelease `snapshot-…` tag alone) were each run against realistic inputs.
- **Docs**: `docs/install.md` has an Updating section; `architecture/branching-and-releases.md` describes the
  snapshot mechanics, the two constraints below, and adds the promote-checks-out-the-wrong-SHA question.

Verified **for real**, not just in tests:

- `dbdatasync update --list`, `--to <stable>` and a bad `--to` against the live nuget.org and GitHub.
- A locally packed build (version `…-snapshot.gtest001`) installed into a scratch `--tool-path`, then
  `update` run **from that real install**: `AppContext.BaseDirectory` does contain `.store/dbdatasync/`, the
  location is detected as a tool-path install with the right root, and the printed **uninstall + install** for
  an older version ran and worked.
- The printed **snapshot** command — `dotnet tool update --tool-path … --add-source <folder> --version
  <snapshot>` — ran for real, upgrading a stable install to a snapshot version that nuget.org does not have.
- The packed nupkg was named exactly `DbDataSync.<version>.nupkg` for a `-snapshot.g…` version, which the
  workflow asserts and the catalog depends on.

Found while building it:

- **`dotnet tool update` refuses a lower `--version`** ("The requested version … is lower than existing
  version"), so a lower target is an uninstall + install. The plan's `Reinstall` operation exists for this.
- **`--to <the installed version>` is answered without the network.** Retention prunes old snapshots, so the
  version someone is running can outlive its release; asking about it must not depend on it still being listed.
- **`promote-test.yml` checks out `dev`'s tip, not the SHA CI passed on**, and a push made with `GITHUB_TOKEN`
  triggers no CI on `test` at all; `workflow_run` workflows are read from the default branch. All three shaped
  Part A and are written up in `architecture/branching-and-releases.md`.

**Not verified here, and why this phase is not in `done/`:**

- **`publish-snapshot.yml` has never run.** It cannot until a release puts it on `main`. Plan: land on `dev`
  → promoted to `test` → cut a release → watch the next promotion publish a snapshot → then confirm
  `dbdatasync update --list` shows it, `--to` stages and verifies it, and a dispatch with `keep=2` prunes
  exactly the older ones. Before that release, the job's steps can be exercised on a throwaway branch under a
  temporary `push` trigger and a scratch tag prefix.
- **Windows.** The printed Windows sequence (`sc.exe stop` → tool commands → `sc.exe start`) is unit-tested as
  text and follows the locked-file behaviour it exists for, but has not been run on a Windows host, as phases
  135/136/140 also needed.
- Whether nuget.org's repository signature is checked on install depends on the SDK (always on Windows; on
  Linux/macOS by SDK version) — noted in `docs/install.md`'s trust wording, not tested.
