# Updating DbDataSync from the UI/CLI, with release channels

**Status: proposal, not agreed. Raw thought plus what was checked on 2026-09-19; nothing built.**
Companion to `architecture/planning/done/snapshot-packages-on-github-packages.md`, which is where the
"how do snapshots reach a consumer without a PAT" question came from.

## The idea

`dbdatasync update [--channel stable|beta|snapshot]` and an equivalent control in the web console: pick a
channel, see what's newest on it, apply it, and have the service come back on the new version — without
the operator hand-running `dotnet tool update` and restarting the service.

## Channels, and where each one already lives

| channel | source | anonymous today? | install step |
| --- | --- | --- | --- |
| `stable` | nuget.org, non-prerelease | yes | `dotnet tool update` (nuget.org is a default source) |
| `beta` | nuget.org, `-beta` versions (`release.yml`'s `beta` checkbox) | yes | `dotnet tool update --prerelease` |
| `snapshot` | GitHub prerelease per promoted `test` build, nupkg attached | yes (see snapshot doc) | **download the asset, install from that folder** |

Only `snapshot` needs anything new. Version discovery: stable/beta is nuget.org's flat-container index
(`api.nuget.org/v3-flatcontainer/dbdatasync/index.json`, anonymous) or just `dotnet tool search`/the update
command itself; snapshot is the GitHub releases API filtered to the `snapshot/` tag prefix (anonymous, but
rate-limited to 60 requests/hour per IP unauthenticated — fine for a manual check, not for polling).

## What was tested

Against the real `2026.9.18.1918` release, nuget.org excluded via `--configfile` so the source under test was
the only one: a raw release-asset URL as a source **fails** (NuGet appends `FindPackagesById()` to it and
GitHub answers `618 jwt-not-provided`), while an anonymously downloaded nupkg in a plain folder
**installs cleanly** as the only source. Details in the snapshot doc.

## The hard part: the running process can't replace itself

Not tested (no Windows box available here), but this is the well-known shape of it:

- **Windows** locks the running tool's DLLs and its apphost shim, so `dotnet tool update` from inside the
  running process (or while the service is running at all) fails on the files it needs to replace.
- **Linux** lets files be replaced under a running process, so the update itself is not blocked — but the
  service is still running the *old* code until restarted, and a `systemctl restart` issued from inside the
  service kills the very process that issued it. The unit is `Type=notify` with default `KillMode`, so
  anything the service spawns as an ordinary child is in its cgroup and is killed with it on stop.
- **Workers**: `DbDataSync.TaskRunner` runs are separate processes launched from the same tool directory,
  so an update has to cope with (drain or interrupt) in-flight runs. Runs are journaled and orphans are
  reconciled on restart (`ProcessSupervisor.ReconcileOrphanedRuns`), so a hard stop is survivable — but a
  drain ("stop scheduling, let runs finish, then stop") is the polite version and needs designing.

## Sketch: a detached helper does the swap

The running process's job is only to *start* the update and get out of the way:

1. **Resolve the target** on the chosen channel and, for `snapshot`, download the nupkg to a temp folder
   (anonymously) first, while the app is still fully up — so a failed download never leaves it half-updated.
2. **Write a small helper script** (`.ps1` on Windows, `.sh` on Linux) to a temp location and launch it
   *detached*. A script rather than a copy of the .NET app because it must not run from the directory it
   is about to replace (a self-updater that locks its own files fails the same way), and a script needs
   nothing but the shell. Detaching is platform work: Windows service child processes are not killed by the
   SCM by default, whereas on Linux it has to escape the unit's cgroup — `systemd-run --no-block` (a
   transient unit) is the usual way — or `KillMode=process`.
3. **The helper** waits for the service to be stopped (or stops it: `sc stop` / `systemctl stop`), runs
   `dotnet tool update --tool-path <dir> DbDataSync [--prerelease]` (or `install --add-source <folder>
   --version X` for a snapshot), starts the service again, then runs the existing `dbdatasync health` check.
4. **Rollback** if the health check fails: reinstall the previous version. For stable/beta that's nuget.org;
   for a snapshot the previous nupkg needs to have been kept in the temp folder until health passes.
5. **The UI** can't hold a connection across a restart, so it shows "updating" and polls `/health` and the
   reported version until the new one answers (or a timeout says the helper's log is where to look).

For `dbdatasync update` run from a terminal rather than the service, the same helper is needed on Windows
(the CLI is the running tool) and unnecessary on Linux, where it can update inline and just tell the operator
the service needs a restart — or restart it.

**Where the tool is installed decides what's possible.** `dbdatasync tool install` (phase 123) lays down a
`dotnet tool install --tool-path` copy and puts it on `PATH`, so an updater can find its own install root
from `Environment.ProcessPath` and update that copy. A per-user `dotnet tool install --global` works the
same way but only for that user; a service running as another account can't touch it. A container isn't a
tool install at all — an update there is a new image tag, and this feature should detect that and decline
rather than try.

## Things that need a decision before this is safe to build

- **Trust.** A web UI that can replace the code the service runs, as the service's account, is a
  significant capability. At minimum: admin-only, an explicit confirmation, the source hard-pinned to
  the official `DbDataSync` package id on nuget.org and to this repository's own releases — never a
  user-supplied URL — and it should be off by default or behind a config switch.
- **Verification.** nuget.org repository-signs what it hosts, so a stable/beta install through `dotnet`
  gets that checked (`dotnet nuget verify`). A GitHub release asset is the *pre-signing* original, so a
  snapshot download has no repository signature to check. The mitigations are a published SHA-512 next to
  the asset (in the release notes, or a checksum file) that the updater compares, and trusting TLS to
  `github.com` for the rest. Snapshots are a development channel, which lowers the stakes; it should still
  be an explicit, labelled choice, not the default.
- **Privileges.** The service account must be able to write the tool directory and stop/start its own
  service. `LocalSystem` can; a named `--account` may not (phase 135 already made ownership per-account).
  `service install` could grant that up front, or the updater could refuse with an explanation.
- **Does "restart required" (phase 120's server-side flag) generalise?** The console already tells an
  operator when a change needs a restart; "a newer version is available" could sit alongside it as a
  passive notice long before any apply button exists.

## A smaller first step

Everything above is optional to start with. The useful first slice is the read-only half: `dbdatasync
update --check` (and a console notice) that reports the running version, the newest on each channel, and
the exact command to run — no download, no swap, no privileges. It exercises channel discovery end to end and
leaves the risky part (replacing running code) for a second phase once the trust questions have answers.

---

# Outcome — resolved 2026-09-19

Agreed as **two ordered phases** rather than one:

1. `architecture/implementation/todo/phase-158K-snapshot-releases-and-cli-update-staging.md` — the read-only
   half this doc's "smaller first step" described, widened: `dbdatasync update` lists releases, lets the
   operator choose, stages a snapshot's nupkg, and prints the commands to run. Also publishes the snapshots.
2. `architecture/implementation/todo/phase-159K-automated-update-from-cli-and-web-console.md` — the detached
   helper, the Linux `ExecStartPre=+` route, draining, rollback, the API and the Updates page. Opens with a spike
   on real hosts, because the two mechanisms this doc sketched are unproven.

Two things checked while writing them changed this doc's sketch: `dotnet tool update --version <lower>` is
refused (a rollback has to be uninstall + install), and the installed tool keeps its own nupkg in `.store/`
(so a rollback package is available locally, but only until that version is replaced). The default Linux unit
is also hardened enough (`NoNewPrivileges`, `ProtectSystem=strict`) that the "detached helper" sketched here
cannot work there as written — phase 159 replaces it with a privileged `ExecStartPre=+` step.
