# Phase 159 — apply an update automatically, from the CLI and the web console (planned)

**Status**: Planned, not started. **Second of two ordered phases** — depends on phase 158
(`phase-158K-snapshot-releases-and-cli-update-staging.md`): its `DbDataSync.Updates` library (release
catalog, snapshot stager, install locator) and its `UpdatePlan` record are what this phase executes. **Starts
with a spike** (checkpoint 1) — the two hard mechanisms below are unproven and both need a real host.
**Plan reference**: `architecture/planning/done/self-update-and-release-channels.md`.

## Why

Phase 158 leaves the operator running printed commands by hand. This phase makes the swap automatic:
`dbdatasync update --apply`, and an **Updates** page in the web console — choose a version, confirm, watch
the service come back on it. The reason it is a phase of its own is that the running product has to replace
the code it is running from, which is not possible in the obvious way:

- **Windows** holds the tool's DLLs and its apphost shim open while it runs, so `dotnet tool update` cannot
  replace them from inside the process, or while the service is up at all.
- **Linux** can replace the files under a running process, but the default unit is hardened
  (`SystemdService.cs`: `NoNewPrivileges=yes`, `ProtectSystem=strict`, `ReadWritePaths={root}`, running as
  an unprivileged user) — the service can neither write the tool directory nor `systemctl stop` itself.
  Anything it spawns as an ordinary child is also in its cgroup and is killed when the unit stops.
- **Worker processes** (`DbDataSync.TaskRunner`, one per run) execute from the same tool directory, so
  in-flight runs have to be drained or interrupted.

## Design

### One plan, two ways to run it

Phase 158's `UpdatePlan` is the single description of what an update is (installed → target, install
location, source, update vs uninstall+install, the service to stop/start). 158 renders it as text; this phase
adds `UpdateApplier` (in `DbDataSync.Updates`), which turns the same object into a **helper script** —
`.ps1` on Windows, `.sh` on Linux — as pure text, so it is unit-testable as golden files with no host. A
script rather than a copy of the .NET app: the helper must not run from the directory it is about to
replace, and a script needs nothing but the shell.

Work directory `<data root>/updates/` (gitignored the way `service-registration.json` is, outside the config
repo): the staged package, `pending-update.json` (the intent), `update-state.json` (phase, message, result,
last 10 history entries — file-based so a restart and every open tab see the same thing, the pattern
`RestartRequiredState` already uses), `update.log`, and `rollback/`.

### Rollback (both facts verified 2026-09-19)

- The installed tool **keeps its own nupkg**: `.store/dbdatasync/<ver>/dbdatasync/<ver>/dbdatasync.<ver>.nupkg`
  (+ `.sha512`; the installed tree is ~200MB for a ~46MB nupkg). It is deleted when that version is replaced,
  so before applying, the helper **copies it into `rollback/`**.
- `dotnet tool update … --version <lower>` is **refused** ("requested version … is lower than existing
  version"), so a rollback is `dotnet tool uninstall` + `install --add-source rollback/ --version <old>`. There
  is a window with nothing installed, which is why the rollback package is local: reinstalling depends on no
  network. The forward direction uses `update` where it can, which leaves the old version in place if it fails.
- Triggered when the new version is not healthy within `HealthTimeoutSeconds` (default 90). Recorded in
  `update-state.json` as `rolledBack`, with the reason, and surfaced in the UI and `update --status`.

### Windows: a detached helper, run as the service account

The service process (LocalSystem) writes the script and the intent, launches the helper detached, and the
helper: waits out the drain → `sc.exe stop DbDataSync` and waits for `STOPPED` → copies the rollback
package → `dotnet tool update|install …` → `sc.exe start` → polls health → rolls back on failure. **Unproven:**
that a child of the service survives the service stopping (the SCM does not kill children by default, but a
job object could). The fallback that removes the question is a one-shot `schtasks` task as SYSTEM, which is
outside the service's process tree entirely.

A service registered under a **named `--account`** usually lacks the right to stop itself and may not own
the tool directory (phase 135 made ownership per-account). Do not guess: a capability probe
(`canApply` + `cannotApplyReason`) tells the UI and CLI when this install cannot self-update and why, and
they fall back to phase 158's printed commands.

`dbdatasync update --apply` run from a terminal on Windows is the same situation — the CLI *is* the running
exe — so it hands off to the same helper and exits.

### Linux: stage, exit, let systemd apply it before the next start

The hardened service cannot apply an update itself, and loosening the unit to let it would undo the reason
for hardening it. systemd already has a mechanism for exactly this: **`ExecStartPre=+`** — the `+` prefix
runs a command with full privileges, without the unit's `User=` or filesystem sandboxing.

1. The service stages the package and writes `pending-update.json` (both inside `ReadWritePaths`), drains,
   and exits with a non-zero code; the unit's existing `Restart=on-failure` brings it back.
2. `ExecStartPre=+dbdatasync internal apply-update` runs first, unsandboxed, and applies the pending
   update to the tool root (copying the rollback package first). The old binary running the update is fine on
   Linux.
3. **Confirm on healthy**: the new version, once it is up and serving, marks `update-state.json`
   `succeeded` and clears the pending intent. If a start attempt finds an update that was applied but never
   confirmed, `apply-update` rolls back instead of applying again — a start-counting scheme in the spirit of
   A/B boot slots, needing no supervisor process. **Unproven** against `Type=notify`, `RestartSec` and
   `StartLimitBurst`.
4. This changes the generated unit, so existing installs need it regenerated: a unit-template version stamp,
   and a `config check` warning when the installed unit predates it.

`dbdatasync update --apply` from a terminal on Linux/macOS with no service applies inline (replacing files
under a running process is fine there) and says a running `serve` needs restarting. Containers are declined:
an update there is a new image tag.

### Draining

An `UpdateState` service gains a **draining** flag. While set, `SchedulerService.TickAsync` enqueues nothing
new and the run-now / initial-load endpoints return **409** ("an update is in progress"); the applier waits
until `ProcessSupervisor` has no running work or `DrainTimeoutSeconds` (default 120) passes, then proceeds.
Interrupted runs are reconciled at the next start (`ReconcileOrphanedRuns`, journalled work), so a timeout is
survivable rather than a data-loss path. This deliberately does **not** reuse replication pause: that writes
pause records and fires notifications, neither of which an update should.

### Trust — what makes this safe to ship

A page that replaces the code a service runs, as the service's account, is a large capability, so the
defaults are closed:

- **Off by default**: `DbDataSync:SelfUpdate:Enabled=false` (an Admin → Configuration setting, phase 81's
  convention). When off, `status` still answers (so the UI can say why) and `apply` returns 403.
- **Channels are opt-in**: `DbDataSync:SelfUpdate:Channels` defaults to `stable`; `beta` and `snapshot` must be
  added deliberately. A snapshot is a development build whose only integrity check is a same-origin checksum
  (phase 158), and the confirmation dialog says so.
- **Admin only**, `[Authorize(Policies.Admin)]` stated on every action per this repo's convention.
- **The client never supplies what gets installed.** `apply` takes a version; the server re-lists the pinned
  sources itself and refuses anything not on that list. No URL, no package id, no source, ever.
- **Verification is the phase 158 story**: stable/beta go through `dotnet tool` from nuget.org (where the SDK
  verifies package signatures, that includes nuget.org's repository signature — confirm on the SDK in use);
  a snapshot has its SHA-512 checked against the sidecar (corruption, not tampering).
- **No phone-home.** Nothing checks for updates in the background — an air-gapped or locked-down install
  should not start making outbound calls because of this feature. The catalog is fetched only when an admin
  opens the page or runs the command.
- Every request records who, from-version, to-version and when, in `update-state.json` history and the log.

### API

All under `api/admin/update`, all `[Authorize(Policies.Admin)]`:

- `GET status` → running version, install kind, `enabled`, `canApply` + `cannotApplyReason`, current state
  (`idle | staging | draining | applying | restarting | succeeded | rolledBack | failed`) and the last result.
- `GET releases?channel=&limit=` → phase 158's catalog, fetched server-side (a `502` with a message when the
  server cannot reach the sources — an install with no outbound access is a supported one).
- `POST apply { version }` → validates against the catalog, stages, writes the intent, launches; `202` with an
  update id, `409` if one is already running.

`/api/health` stays anonymous and `{status:"ok"}` — it currently carries **no version**, and putting the
exact version on an unauthenticated endpoint of a product that can be updated hands out a target list. The UI
polls health for liveness across the restart and then reads `status` for the version.

### Web console

New Admin tab **Updates** (`/admin/updates`, `AdminUpdatesPage.tsx`, beside Libraries — `App.tsx` routes,
the Admin rail). Running-version card (install kind; when `canApply` is false, the reason and phase 158's
manual commands); channel tabs limited to the enabled channels; releases table (version, derived publish
time, installed/newer marks) with **Update…**; a confirmation dialog stating the consequences (the service
restarts, in-flight runs are given up to N seconds, a snapshot is a development build); a progress panel
driven by `status`; and after the restart a result banner, including the rolled-back case with a pointer to
`update.log`. The SPA already has a restart-required indicator (phase 120); an applied update clears it the
way a restart does.

### CLI

`dbdatasync update --apply [--to <v> | --channel <c>] [--yes]`; the interactive form of phase 158 gains an
"Apply now? [y/N]" when `canApply`, and prints the phase 158 commands otherwise. `dbdatasync update
--status` prints `update-state.json`. The Linux hook is `dbdatasync internal apply-update` (the `internal`
group already exists for non-public commands).

## Checkpoints

1. **Spike, on real hosts** — a Windows machine (service as LocalSystem) and a Linux machine with systemd
   and the hardened unit: does a helper survive the service stopping; does `schtasks` work as the fallback; does
   `ExecStartPre=+` apply an update under `Type=notify`; does confirm-on-healthy roll back a version that
   never starts. Record the findings here and amend this design before any product code.
2. `UpdateApplier`: script generation for both platforms as golden files; rollback-package copy.
3. Work directory and `update-state.json`; `dbdatasync internal apply-update`.
4. CLI `--apply`, `--yes`, `--status`.
5. Draining: scheduler gate, 409 on run-now and initial-load, supervisor wait, timeout — with tests.
6. API controller, config keys (`Enabled`, `Channels`, `DrainTimeoutSeconds`, `HealthTimeoutSeconds`) and their
   Configuration-screen entries; tests behind a fake `IUpdateLauncher` (non-admin 403, disabled 403,
   free-form version rejected, one-at-a-time 409, unreachable sources 502).
7. Linux unit-template change, its version stamp, and the `config check` warning for a stale unit.
8. SPA page and nav; Playwright coverage against the dev harness with the fake launcher — the real swap
   replaces the binary under test, so it is not something a browser suite can drive.
9. `docs/install.md` — the updating section, the trust model, the opt-in switches.
10. **End-to-end on the real hosts**: a full apply on each platform, and a deliberately broken snapshot to
    force a rollback (a package whose `serve` exits at once), neither of which any suite here can exercise.

## Open questions

- **Linux: `ExecStartPre=+`, or a sudoers/polkit rule** letting the service user run one narrow command? The
  former needs no host configuration; the latter leaves the unit unchanged. The spike decides.
- **Do sessions survive the restart?** If auth cookies depend on key material that is not persisted, the admin
  must sign in again after every update; check, and make the page say so if it is unavoidable.
- **Named-account Windows services**: refuse (proposed), or have `service install` grant the rights up front?
- **macOS**: there is no service manager integration in this repo for it today; `--apply` is inline-only there.
- **A passive "update available" notice** (deliberately absent above). If wanted later it needs an opt-in
  config key and an honest answer for air-gapped installs.
