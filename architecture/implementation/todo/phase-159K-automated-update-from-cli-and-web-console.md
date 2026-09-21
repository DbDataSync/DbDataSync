# Phase 159 — apply an update automatically, from the CLI and the web console (planned)

**Updated 2026-09-21 (phase 164):** every `DbDataSync:SelfUpdate*` key this doc names below was renamed
under phase 164's config reorg — `SelfUpdateEnabled` (bool) is now `Updates:Mode` (`manual`/`disabled`),
and `SelfUpdateChannels`/`SelfUpdateDrainTimeoutSeconds`/`SelfUpdateConfirmAfterSeconds` are now
`Updates:Channels`/`Updates:DrainTimeoutSeconds`/`Updates:ConfirmAfterSeconds`. Not rewritten
line-by-line below; see `docs/configuration.md`'s `DbDataSync:Updates:*` section for the current names.

**Status**: In progress. The Linux half of the spike is done (2026-09-19, findings below) and amended
the design; the **Windows** half and the **root/sandbox** half could not be run on the machine available
(no Windows host, no root, and this Ubuntu blocks the user namespaces a user unit needs for sandboxing) and
are still open. **Second of two ordered phases** — depends on phase 158
(`phase-158K-snapshot-releases-and-cli-update-staging.md`): its `DbDataSync.Updates` library (release
catalog, snapshot stager, install locator) and its `UpdatePlan` record are what this phase executes.
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
runs it. **The commands come from one place** (`UpdateCommands`, in `DbDataSync.Updates`) — the printed plan and
the executed one are the same list, so they cannot disagree (158's renderer was moved onto it, and its 118 tests
did not change).

*Amended while building.* This section first said the plan would be turned into a helper **script**. On Linux
that turned out to be unnecessary: the unit runs `dbdatasync internal apply-update` (ordinary C#, tested like any
other) as its privileged pre-start step. A script is only needed for **Windows**, where the helper must outlive the
process being replaced — and that half is deliberately not built (see "Windows" below).

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

1. The service validates the version, writes `pending-update.json` (a version and who asked), drains, and exits with
   the reserved code **75**; the unit's existing `Restart=on-failure`, with `SuccessExitStatus=75` and
   `RestartForceExitStatus=75` (spike finding), brings it back without logging a failure.
2. `ExecStartPre=-+dbdatasync internal apply-update` runs first, as root and outside the unit's sandbox. It **derives
   everything itself** (see "The trust boundary"), keeps a copy of the outgoing package in root's own directory, and
   applies the update, recording it as on trial. The old binary running the update is fine on Linux.
3. **Confirm on healthy**: the new version, once it has been up and serving for `ConfirmAfterSeconds` (spike
   finding: not merely at "ready"), writes a note the privileged step reads at the next start, and marks the display
   state succeeded. If a start finds an update that was applied but never confirmed, `apply-update` rolls back instead
   of applying again — a start-counting scheme in the spirit of A/B boot slots, needing no supervisor process.
   **Verified** against systemd's restart sequencing (spike) and against real tooling (see Progress); **not** yet as a
   whole under a real system unit.
4. The unit only carries any of this when root installed it with `--self-update`; `config check` warns when
   `DbDataSync:SelfUpdateEnabled` is on and the installed unit lacks the marker.

`dbdatasync update --apply` from a terminal on Linux/macOS with no service applies inline (replacing files
under a running process is fine there) and says a running `serve` needs restarting. Containers are declined:
an update there is a new image tag.

### Draining

`UpdateDrainState` is a flag. While it is set, `SchedulerService.TickAsync` returns before enqueuing anything, and
`UpdateDrainMiddleware` answers **409** to any request that would change something — every non-GET/HEAD/OPTIONS
except `/api/auth`, `/api/admin/update`, `/api/health` and `/hubs` — with "an update is being applied, so changes
are paused until the service has restarted". The service then waits until nothing is running (`TaskRunStore`'s
running runs plus live worker processes) or `SelfUpdateDrainTimeoutSeconds` (default 120) passes, and only then
asks the host to stop. Interrupted runs are reconciled at the next start (`ReconcileOrphanedRuns`, journalled work),
so a timeout is survivable rather than a data-loss path. This deliberately does **not** reuse replication pause:
that writes pause records and fires notifications, neither of which an update should.

*Amended while building:* this section first said each run-triggering endpoint would return 409. There are a dozen
of them and the next one added would have to remember; one middleware covers all of them, at the cost of also
pausing configuration edits for the (bounded) drain window — which is a reasonable thing to want as the process is
about to restart.

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
- **Verification is thinner than first written — see "Package signing".** Stable/beta go through `dotnet tool` from
  nuget.org over TLS, and `dotnet tool install` does **not** check package signatures or NuGet's trust policy (tested;
  this line first claimed it did); a snapshot has its SHA-512 checked against a same-origin sidecar (corruption, not
  tampering).
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

## The trust boundary (added after review — the first design had a hole)

**What was wrong.** The design above, and the first build of it, had the privileged step — `ExecStartPre=+`, running
as root — read `pending-update.json` and act on **all of it**: which folder to install from, which tool directory,
which previous version to roll back to. That file lives in `<data root>/updates/`, inside the hardened unit's
`ReadWritePaths`, so the *unprivileged* service can write it, and so can anything that has compromised the service.
The rollback package, the record of an update on trial and the log were in the same directory. A compromised service
could write a request naming its own package, or plant a "rollback" one, and root would install it at the next
restart: **code execution as root**. It also applied to every default Linux install, because the unit always carried
the step. A second hole sat beside it: `chown -R` over a directory the service controls follows symlinks. Both were
found by rereading the design from the attacker's side, before anything was committed.

**What it is now.**

- **The request is a version, and who asked. Nothing else** (`PendingUpdate`). There is nowhere in it to name a
  source, folder, package or path, and extra fields in the JSON are ignored.
- **The privileged step derives everything itself** (`PrivilegedContext`): which installation it is (its own
  location), what is running (its own version), whether the version exists (the pinned release sources — it looks
  the version up again), and — for a snapshot — the package, which *it* downloads from GitHub into a private temp
  directory and verifies. It refuses anything that is not a stable, beta or snapshot version of this product, or not
  found in the pinned sources.
- **Two directories, split by who may write them** (`UpdateWorkspace`). The service's `<data root>/updates/` holds
  only what the service legitimately writes: the request, a note that the new version has served long enough
  (`confirmed-update.json`) and the display state the console shows. **Root's** `/var/lib/dbdatasync-update/` — outside
  the data root, which the service's user owns and could rename things in — holds the record of an update on trial, the
  spare copy of the outgoing package used to roll back, and the log. The service cannot plant a rollback package
  because it cannot write where the rollback is read from.
- **Reads from the service's directory are hostile**: a symlink, an oversized file or malformed JSON is "absent".
  **Writes are a temp file moved over the target**, so a path swapped for a symlink is replaced, not followed, and
  writing into the service's directory refuses if that directory is itself a link. No `chown`, no `.gitignore`
  appends (the data root is not a repository anyway).
- **`dotnet` runs in root's directory with a NuGet config pinned to nuget.org.** `dotnet tool` reads `nuget.config`
  from its working directory, which under the unit was the service-writable data root — a compromised service could
  have added a package source of its own.
- **It does nothing unless it is root** (`internal apply-update`; `--as-current-user` to override, for tests and for a
  machine where the operator and the service are one user).
- **The step is opt-in, and root's decision**: `sudo dbdatasync service install --self-update`. A default unit carries
  none of it. A setting the service could write cannot be the thing that switches on a root-privileged step.
- **`-+` rather than `+`.** Checked on systemd 255: a `+` step that fails stops the service *starting at all*;
  `ExecStartPre=-+…` does not, and does not care if the binary is missing. That matters because an update can go back
  to a version older than this feature, whose `internal` command does not know `apply-update`.
- **The service stages nothing.** It validates the version so an admin is told at once, writes the request, winds work
  down and exits 75.

**What a compromised service can still do, honestly**: ask for an update to any *official* release in an enabled-or-not
channel, including an older one with known bugs (the step re-validates against the pinned sources but the channel
allow-list is the API's, not root's); forge the confirmation note, which can at worst stop a bad version being rolled
back; and race a symlink swap of its own `updates/` directory between root's check and root's write, which can at worst
create or replace a file named `update-state.json` (display JSON) elsewhere. None of these is code execution. The first
is the one to weigh: it is bounded by the fact that only genuine releases can be installed.

**What review found late, by running it for real.** A relative `--state-dir` made a rollback uninstall the new
version and then fail to install the old one, because the child `dotnet` runs from that directory and a relative
`--add-source` resolved against it. Every path in the workspace is now absolute, the CLI's `--stage-dir` too, and a
rollback whose kept package cannot be installed falls back to nuget.org for a stable or beta version rather than leave
nothing installed. Unit tests had used absolute temporary paths and missed it.

## Package signing (investigated 2026-09-19 — moved to a follow-up)

Two claims this phase (and 158) made about signatures were wrong, and the fuller finding is in
`architecture/planning/todo/follow-up-phase-158-159-release-and-snapshot-packages-are-unsigned-and-tool-install-does-not-verify-them.md`.
In short: **`dotnet tool install` does not enforce NuGet's trust policy** (`signatureValidationMode=require` /
`trustedSigners`) — tested, though `dotnet restore` does — so nothing in this phase checks a package signature, and
nuget.org's repository signature is *not* "checked in the bargain". Neither release nor snapshot packages are
author-signed (the GitHub-release copy is not signed at all). What does gate with stock tooling is
`dotnet nuget verify --certificate-fingerprint`, which the privileged step could run itself. The trust boundary above
bounds a compromised *service* to installing a genuine release; signing is the control for a compromised *release
pipeline*, and is not a prerequisite for shipping this phase.

## Spike findings (2026-09-19, Linux, systemd 255 user manager)

Stand-in scripts for the app and for the apply step under real transient units (`Type=notify`,
`NotifyAccess=all`, `Restart=on-failure`, `ExecStartPre=+…`), on the throwaway user manager of a workstation.
Nothing of this repository was involved — it tests systemd's sequencing, the part the design leaned on.

**Confirmed**

- **The hand-off works.** The running app sees a pending intent, drains, and exits **75**; systemd restarts
  it; `ExecStartPre=+` runs *again on that restart* and applies the update before `ExecStart`; the new version
  starts, signals ready, and confirms. `NRestarts=1`, unit ends `active (running)`, `Result=success`.
- **A version that crashes at startup is rolled back.** Restart → `ExecStartPre` sees an applied-but-
  unconfirmed update → rolls back instead of applying → the old version comes up. `NRestarts=2`.
- **A version that hangs without ever signalling ready is rolled back too**, via `TimeoutStartSec` killing
  it and the same next-start check — no supervisor process and no timer of our own.
- **`SuccessExitStatus=75` + `RestartForceExitStatus=75` are needed.** With `Restart=on-failure` alone, the
  deliberate exit is logged as `Main process exited, code=exited, status=75/TEMPFAIL` and `Failed with result
  'exit-code'` on every update — noise that trips unit-failure monitoring. With both set it still restarts
  (`NRestarts=1`) and the journal shows only "Scheduled restart job".

**Design changes that follow**

- **The unit template gets those two directives** along with `ExecStartPre=+`.
- **Confirm after a grace period, not at "ready".** The stand-in confirmed the moment it signalled ready, so a
  version that starts and then dies ten seconds later would count as good. The real confirmation is made once
  the app has been serving for `ConfirmAfterSeconds` (default 60), and the rollback check is "applied and not
  yet confirmed". The real unit's `RestartSec=5` keeps a bad update inside systemd's default start-rate limit
  (5 starts in 10s) for the two starts it takes to roll back.
- **Windows self-apply stays off until it is spiked.** Everything else here is built so the Windows helper can
  be added behind the same capability probe; until someone has watched it survive a service stop on a real host,
  `canApply` is false on Windows and the operator gets phase 158's printed commands.

**Not established, and needs a real host**

- **The sandbox half.** Whether `ExecStartPre=+` really runs outside `ProtectSystem=strict` /
  `NoNewPrivileges=yes` and `User=`. The user manager here cannot test it: `kernel.apparmor_restrict_
  unprivileged_userns=1` means it cannot create a mount namespace, so `ProtectSystem=` and `ReadOnlyPaths=`
  silently do nothing (the unit's mount namespace was the host's), and both the app and the apply step could
  write everywhere. systemd's documentation says the `+` prefix runs "with full privileges … not subject to"
  `User=` and the namespacing options; that is the claim this design rests on, and it is unobserved. It needs a
  system unit and root.
- **Windows**: whether a child of the service survives the service stopping, and the `schtasks` fallback.

## Checkpoints

1. **Spike** — *Linux sequencing done (findings above); the root/sandbox half and all of Windows are not.*
2. **`UpdateApplier`** — done, as C# rather than generated scripts (see the amendment above): install, keep the
   rollback package aside, roll back (falling back to nuget.org for a stable/beta version), confirm.
   `ApplyPendingAsync(PrivilegedContext)` is the unit's step and treats the request as hostile;
   `ApplyNowAsync` / `RollBackAsync` / `Complete` are the CLI's synchronous path.
3. **Work directory and state** — done, split by trust: `UpdateWorkspace` (the service's directory vs root's),
   `UpdateStateStore` (hostile reads, rename-based writes), `dbdatasync internal apply-update` (root only; always
   exits 0). *Changed from the first build:* no `chown`, no `.gitignore` appends, no reading anything but a version
   from the service's directory.
4. **CLI** — done: `update --apply [--yes] [--url] [--health-timeout]` and `update --status`. **Added beyond the
   original design:** a synchronous path. A terminal is not the service, so it can stop the service, install, start
   it, check health and roll back itself, leaving nothing "on trial". It works in a private temporary directory.
5. **Draining** — done: scheduler gate, drain middleware, worker probe, timeout.
6. **API** — done: `api/admin/update/{status,releases,apply}` (`UpdateService`, `AdminUpdateController`), four
   settings (`SelfUpdateEnabled`, `SelfUpdateChannels`, `SelfUpdateDrainTimeoutSeconds`,
   `SelfUpdateConfirmAfterSeconds`) on the Configuration screen and in `docs/configuration.md`,
   `UpdateConfirmationService`. The service now stages nothing. `HealthTimeoutSeconds` was not needed: on the
   service path systemd's start timeout and the confirmation grace period do that job.
7. **Unit template** — done, opt-in: `service install --self-update` adds `Environment=DBDATASYNC_SELF_UPDATE=1`,
   `ExecStartPre=-+…apply-update`, `SuccessExitStatus=75`, `RestartForceExitStatus=75`; an ordinary unit is
   unchanged. `config check` warns when `SelfUpdateEnabled` is on and the unit lacks the marker.
8. **SPA** — done: Admin → Updates (`AdminUpdatesPage`), driven in a real browser by
   `tests/DbDataSync.Web.Tests/tests/admin-updates.spec.ts` with the update endpoints scripted, including the
   service going away and coming back and one that never does.
9. **Docs** — done: `docs/install.md` (Updating), `docs/configuration.md`.
10. **End to end on real hosts** — **not done**, and cannot be from the machine this was built on: a full apply
    through a real system unit on Linux, a deliberately broken version forcing a real rollback under systemd, and
    everything on Windows.

## Progress

**Verified for real, not just in tests** (Linux, real `dotnet tool`, real nuget.org, real nupkgs packed from this
build):

- **The privileged step, end to end, three cycles**, run from the installed tool itself with `--as-current-user`:
  1. A **hostile request** — naming its own `sourceDirectory`, a `toolRoot` of `/etc`, `installKind: global`, a
     bogus previous version, a snapshot channel and a package URL — was ignored. The step installed the *real
     published* stable release from nuget.org into its *own* tool root, kept the outgoing package in the root-only
     directory (not the service's), and the log never mentions the planted source. The next start, with nothing
     confirmed, **rolled back** by reinstalling the previous build from that kept copy — a build that exists nowhere
     else.
  2. A **confirmed** update (the service's note in place) was settled at the next start: it stayed, and the spare
     copies and record were removed.
  3. A version **not in the pinned sources** was refused, recorded, and nothing changed.
- **Running it found a real bug** the unit tests missed: a relative `--state-dir` made the rollback uninstall the
  new version and then fail to install the old one (the child `dotnet` runs from that directory, so a relative
  `--add-source` resolved against it). Fixed by making every path absolute, plus the nuget.org fallback for a
  stable/beta rollback; the e2e was then re-run clean.
- `dbdatasync update --to <stable> --apply --yes` as a real downgrade from a snapshot, through nuget.org.
- systemd 255: the rendered unit against `systemd-analyze verify`; a `+`-prefixed, quoted `ExecStartPre` with spaces
  in the path running; and that **a failing `+` step blocks the service starting while `-+` does not** — including
  when the binary does not exist at all.
- The whole Updates screen in Chromium, against scripted endpoints.

**Tests**: 202 in `DbDataSync.Updates.Tests` (including the boundary suite: a request cannot steer the install, a
planted package or rollback is never used, symlinks are not followed, `nuget.config` is pinned); 209 in
`DbDataSync.Cli.Tests` (11 skipped, Windows-only) covering the update command, the opt-in unit, the readiness check
and the internal command's refusal to act unless root; 70 API tests for the service, controller, drain middleware,
confirmation worker and scheduler gate; 8 Playwright scenarios.

**Not verified here** — each needs a host this build machine is not:

- **`ExecStartPre=-+` outside the hardened sandbox.** The claim the Linux design rests on — that the step ignores
  `User=`, `NoNewPrivileges` and `ProtectSystem=strict` — is systemd's documented behaviour and is unobserved: the
  user manager here cannot create a mount namespace (`kernel.apparmor_restrict_unprivileged_userns=1`). It needs a
  system unit and root.
- **Root's directory created as root.** `/var/lib/dbdatasync-update` is created `0755` by the first privileged step;
  that mode and its effect on the service's read of the state file are written and unit-tested (with a temp
  directory), never run as root.
- **A real service updating itself**: exit 75 → systemd restart → apply → new version serves → confirmation after
  the grace period, and the same with a version that is deliberately broken. The pieces are each verified; the
  whole is not. Nor could the snapshot path be run for real: no snapshot exists yet, and the step refuses to install
  anything the pinned sources do not list.
- **Windows** — deliberately off (`canApply` is false, `--apply` refuses and prints the commands). A running
  `dbdatasync.exe` and its service hold their own files open, so it needs a helper that outlives them; that has to
  be watched working on a real host (or the `schtasks` fallback) before it is switched on.

## Open questions

- **Linux: `ExecStartPre=-+`, or a sudoers/polkit rule?** `-+` is built and needs no host configuration beyond the
  opt-in; whether it behaves as documented under the hardened unit is the unverified part above.
- **Should the privileged step enforce the channel allow-list?** Today `SelfUpdateChannels` is the API's, so a
  compromised service can still *request* an older official release in a channel an admin never enabled. It cannot
  install anything that is not a genuine release, so this is bounded, but root could refuse by reading a root-owned
  setting; that would be a second place to configure it.
- ~~**Do sessions survive the restart?**~~ **Yes, by construction:** sessions are rows in the state database
  (`SessionStore`), not in-memory, so the same cookie works afterwards; the page keeps polling through the restart
  without re-authenticating. Read from the code, not yet observed across a real restart.
- **Named-account Windows services**: moot until Windows is switched on.
- **macOS**: no service manager integration exists in this repo, so `canApply` is false there and `--apply` is
  inline-only.
- **A passive "update available" notice** (deliberately absent). If wanted it needs an opt-in setting and an honest
  answer for air-gapped installs.
- **Confirmation grace period.** 60 seconds by default and configurable; whether that is the right default is a
  judgement about how long a version must serve before "healthy" means anything.
