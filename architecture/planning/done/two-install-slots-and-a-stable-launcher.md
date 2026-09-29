# Two install slots and a single stable launcher

**Outcome (2026-09-28): agreed, and carried by
`architecture/implementation/todo/phase-196L-two-install-slots-and-a-stable-launcher.md`.** Two points
below turned out wrong or incomplete against the code, and the phase doc changes them. Both are noted
here, not quietly dropped:

- **"a custom `AssemblyLoadContext`"**: this can't work as written. `LibraryRegistry` and
  `DriverPluginLoadContext` both defer shared contracts (`DbDataSync.Drivers.Abstractions` and the rest)
  to `AssemblyLoadContext.Default`. Payload assemblies sitting in a private context would never be found
  there, and every driver plugin would break. The launcher loads the payload into the **Default** context
  instead, with `Resolving`/`ResolvingUnmanagedDll` handlers backed by the same
  `AssemblyDependencyResolver`.
- **"`AppContext.BaseDirectory` … the fix is a call-site change"**: it is wider than `InstallLocator`.
  `WebRootLocator`, the TaskRunner worker's beside-the-assembly resolution (`ApiOptions`), and ASP.NET's own
  entry-assembly and deps-file lookups all read process-global values. The launcher sets the slot's values
  once, before it calls in: `APP_CONTEXT_BASE_DIRECTORY`, `APP_CONTEXT_DEPS_FILES` and
  `Assembly.SetEntryAssembly`. Checked in a spike against a real packed tool install.

The user also asked to **keep the web console's Updates page**. The page stops being able to apply an
update, as decided below. It now gives the admin copyable CLI commands, with instructions, for applying one
from a shell.

---

**Status: design decided, not yet built.** The first revision of this doc kept two install slots but still
had a small process (a compiled shim on Windows, a bash script on Linux) spawn a *separate* payload process
and relay its exit code. The second revision dropped the separate process entirely: one long-lived launcher
exe loads the current slot's managed DLL directly, in its own process, and calls into it. This revision
closes out that one's open questions (migration, the service-initiated path, launcher self-update, the
version sanity check, and the disk-cost tradeoff) — all decided in review, none left open. Nothing here has
been built.

## The core idea

Keep two `dotnet tool install --tool-path` installations on disk — slot **A** and slot **B** — and
alternate which is current, same as the previous revision. What changes is the thing that's actually
registered as the Windows service and put on PATH: a single, small, **cross-platform** `DbDataSync.Launcher`
exe that never itself changes across an ordinary update. At startup it reads a one-line pointer file next
to itself, resolves that slot's `DbDataSync.Cli.dll`, loads it into its *own* process (not a child process)
via a custom `AssemblyLoadContext`, and invokes its entry point directly, passing argv straight through.

Updating means: install the target version into whichever slot isn't current, flip the pointer file,
restart. **The service registration and PATH entry are written once and never touched again** — that's
the property the previous revision didn't have (it still had to `sc config` the service's binPath at every
swap, because it registered a specific slot's own apphost directly). Rollback is the same operation run in
reverse: flip the pointer back, restart. No reinstall either way, same as the previous revision.

## Why this removes almost the whole Windows lock problem

Unchanged from the previous revision's reasoning: the install step never targets the files behind either
the running service or the running CLI process, because it always targets the slot that's *not* current.
Stopping and restarting the service is the only unavoidable step, and that's an ordinary stop/start, not a
file-replace — so the downtime window is bounded by how long the service takes to stop and start, not by
how long Windows takes to let go of a lock that's been observed to outlive a 15–30s wait.

## The launcher

One project, used identically on both platforms — there's no more Windows-shim/Linux-script split, because
the mechanism (load a managed assembly into the current process and call it) is plain .NET and behaves the
same on both. Concretely, `Main` does roughly:

1. Read a pointer (`current.txt`, containing `a` or `b`) from its own directory.
2. Resolve `versions/<slot>/DbDataSync.Cli.dll`.
3. Load it into a new `AssemblyLoadContext` and invoke its entry point with the process's own `args`.
4. Return whatever that call returns as its own exit code.

Because the service is fully stopped and restarted at every swap (as it is today), the launcher's process
lifetime and one version's lifetime are the same thing — it only ever needs to do this once, at process
start, and never needs to unload or hot-swap anything mid-process. That keeps this boring: no collectible
`AssemblyLoadContext`, no runtime reloading, just an ordinary load that lives for the process's whole life.

Three things this needs to get right, not hand-waved:

- **Native dependency resolution.** `DbDataSync.Cli` pulls in LibGit2Sharp, SQLite and DuckDB.NET, whose
  native assets live under `runtimes/<rid>/native/` relative to the dll and are normally resolved by the
  `dotnet` muxer reading the target's own `.deps.json`. A bare `Assembly.LoadFrom` doesn't do that. The
  fix is the standard, documented pattern for exactly this case: a custom `AssemblyLoadContext` paired with
  `AssemblyDependencyResolver` constructed against the slot dll's path, wiring both `Resolving` (managed)
  and `ResolvingUnmanagedDll` (native) — a known amount of real work, not a blocker.
- **`AppContext.BaseDirectory` is process-global, not per-loaded-assembly.** `InstallLocator.Locate`
  (`src/DbDataSync.Updates/InstallLocator.cs:38`) reads it directly to work out which install this is —
  under the launcher it would report the *launcher's* directory, not the slot's, and misidentify the
  install entirely. `InstallLocator.Locate` already takes `baseDirectory` as an explicit parameter, so the
  fix is a call-site change (pass the resolved slot directory) rather than a redesign — but it has to be
  made deliberately everywhere this assumption is currently implicit, not discovered later by something
  quietly misbehaving.
- **`Environment.ProcessPath` already does the right thing, with no change.** `ServiceCommand.Install`
  reads it to decide what to register as the service binary (`src/DbDataSync.Cli/ServiceCommand.cs:77`);
  under the launcher model that correctly resolves to the launcher's own stable path — which is exactly
  what should be registered, once, forever.

One more reassurance rather than a risk: things like `dbdatasync --version`, which read
`typeof(Help).Assembly`'s informational version (`src/DbDataSync.Cli/UpdateCommand.cs:34`), keep working
unmodified — that reflection happens from *inside* the loaded payload on its own type, so it resolves to
the slot's own assembly regardless of which `AssemblyLoadContext` loaded it.

## How the launcher starts on Windows 10, and finds the .NET runtime

This needs no new mechanism — it's the same one every framework-dependent .NET exe already uses, including
today's own `dbdatasync.exe`. The launcher is an ordinary `net10.0`, framework-dependent, apphost-generated
console exe (`UseAppHost` default-on, `SelfContained=false`, same as `DbDataSync.Cli.csproj` already is).
The SDK-generated apphost embeds enough to find `hostfxr` itself — an embedded path hint, then the registry
(`HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\<arch>\InstallLocation` on Windows), then well-known
`Program Files` locations, then `PATH` — and from there resolves a compatible shared runtime and boots the
CLR. Setting `RollForward=latestMajor` on the launcher's own project (matching the payload's setting)
keeps its own resolution at least as permissive as what it's about to load. Windows 10 support is whatever
the target `net` version already supports for desktop OS baselines — identical to what the existing
`dbdatasync.exe` already requires, not a new constraint the launcher introduces.

Worth calling out explicitly: this is **more robust than the `dotnet exec <dll>` style** used elsewhere in
this codebase (`ProcessSupervisor.BuildStartInfo`, `src/DbDataSync.Api/Services/ProcessSupervisor.cs:114-116`)
specifically for a service context, because apphost-style launching never needs `dotnet` on `PATH` at all —
it talks to `hostfxr` directly. `ProcessToolCommandRunner.ResolveDotnet` already special-cases `DOTNET_ROOT`
(`src/DbDataSync.Updates/UpdateApplier.cs:504-514`) precisely because a service account's environment can
have no usable `PATH`; the launcher sidesteps that whole class of problem by construction rather than
needing its own fallback.

## Deciding which slot to load: a pointer file, not directory-scanning

Considered alternative to the pointer file: skip it entirely, have the launcher enumerate both slots, read
each one's installed version, and always run whichever is numerically higher — using the `ReleaseVersion`
type that already exists for exactly this comparison (`src/DbDataSync.Updates/ReleaseVersion.cs`). It's an
appealing idea because it removes a piece of state that could in principle drift from reality.

It doesn't actually get to statelessness, though, once rollback is considered. A forward update under pure
"highest wins" needs no pointer — install the new version into the stale slot and it's automatically
preferred next launch. But rollback means preferring the **lower**-versioned slot, which a pure
version-comparison rule can't express without an override — so some small piece of state ends up being
needed anyway, just narrower and only touched in the exceptional case rather than on every swap. That's
worse, not better: two different code paths (compare-and-pick for the common case, an override file for the
uncommon one) instead of one. It also has a sharper failure mode — if an install into the inactive slot is
interrupted partway (crash, disk full) leaving a slot that merely *looks* newer, pure scanning could pick
up a broken half-installed slot the moment its version marker is written, whereas a pointer file is only
ever flipped **after** an install (and optionally a health check) is confirmed to have succeeded, so it
naturally acts as a commit point that an incomplete install never crosses.

**Decided:** keep the explicit pointer file as the single source of truth, used identically for both an
update and a rollback (flip the value, restart — same code path either direction). **In v1**, the launcher
(or `dbdatasync update --status`) also reads both slots' actual versions and logs a warning if the pointer
names a slot whose version doesn't look like a sane forward step from the other — cheap given
`ReleaseVersion` already exists for the comparison, and it catches pointer/reality drift early rather than
trusting a possibly-stale pointer forever. Version comparison stays a sanity check, never the thing that
decides what actually runs.

## Install/swap sequence

1. Resolve the inactive slot from the pointer.
2. Run the existing `UpdateCommands.Install`/`Rollback` invocations
   (`src/DbDataSync.Updates/UpdateCommands.cs:17-31`) against that slot's tool-path — unchanged
   invocations, aimed at a specific inactive directory instead of "wherever this process is running from."
   As in the previous revision, `UpdatePlanner.OperationFor` needs to compare against whatever version is
   already sitting in *that slot*, not the currently-running version — those are different numbers under
   this model.
3. Stop the service.
4. Flip the pointer file. One write, no `sc config`, nothing platform-specific — the service and PATH both
   already point at the launcher, which re-reads the pointer fresh on its next start.
5. Start the service, poll health (`IHealthProbe`, unchanged).
6. Healthy → done, old slot untouched. Unhealthy → flip the pointer back, restart. No reinstall.

## What this removes from the current design entirely

Everything the previous revision already removed (the retry-loop helper process, `UpdateApplier`'s
kept-rollback-package path — `src/DbDataSync.Updates/UpdateApplier.cs:233-239,291-340,372-390`), plus, new
in this revision:

- **The per-swap `sc config`/service-reconfiguration step.** The previous revision still had to repoint the
  service's binPath at every swap because it registered a slot's own apphost directly. The launcher's path
  never changes, so this goes away entirely — the service is registered once, at initial setup, full stop.
- **The Windows-shim/Linux-script split.** One cross-platform launcher project replaces both.

- **The service-initiated ("pending update") path is retired entirely**, not migrated. The Admin UI's
  "request an update, applied at the next scheduled/administrative restart" flow —
  `AdminUpdateController`'s `apply` endpoint → `UpdateService.WritePending` → consumed by
  `UpdateApplier.ApplyPendingAsync`/`ConfirmAsync` at the next systemd start
  (`ExecStartPre=-+... internal apply-update`, `src/DbDataSync.Cli/SystemdService.cs:288`,
  `src/DbDataSync.Cli/InternalCommand.cs:59-119`) — goes away, rather than being reworked onto slots. All
  updates funnel through the CLI-driven path from here. **Real, visible consequence, not just internal
  cleanup:** the Admin UI loses its current ability to request an update itself; an operator applies one
  from a shell. If "trigger an update from the console" is wanted again later, it needs a new design on top
  of the slot flow (e.g. the API shelling out to `dbdatasync update --apply` the way `ProcessSupervisor`
  already spawns other child processes) — not assumed back into scope here.

## Packaging / bootstrap

The launcher's published output ships alongside each version's payload (so any installed version can supply
or refresh it), but it is **not** reinstalled at every swap — only at first setup, or on the rare occasion
the launcher itself needs a fix. Bootstrap for a fresh machine: `dotnet tool install --tool-path <slot A>`
the first version as today, then a one-time step (part of `dbdatasync service install`) copies that
version's bundled launcher binary out to its fixed location, writes the pointer file naming slot A, and
registers the service/PATH against the launcher rather than slot A's own apphost.

**Migrating an existing install.** A plain `dotnet tool install -g`/`--tool-path` from before this shipped
predates slots and the launcher entirely. **Decided:** this is handled automatically, not through a
separate opt-in command — the first `dbdatasync update --apply` run against such an install detects there
are no slots yet, treats the existing install as slot A, installs the launcher, writes the pointer, and
re-registers the service/PATH against it, all as one step. It prints plainly what it did (something like
"this install predates versioned slots; converting it to slot A and installing the launcher before
applying the update") rather than doing it silently — the operator sees a one-time structural change
happened, not just an ordinary version bump.

**Updating the launcher itself.** Its own code changing (a bug in the `AssemblyLoadContext`/dependency-
resolution logic, say) is rare and is **not** the same operation as an ordinary version swap. **Decided:**
re-running `dbdatasync service install` (idempotent already) — or a dedicated `dbdatasync launcher repair`
if that reads better — copies the current slot's bundled launcher binary over the installed one and
re-registers the service. Ordinary swaps never touch it; this is a separate, explicit, rarely-used path.

**Disk cost.** Two full installs live side by side permanently instead of one. **Decided:** accepted as a
plain tradeoff for this design — not pursuing a reduction (e.g. hard-linking shared files between slots)
for now.

## Open questions

None remaining from this revision — the four items above (migration, the service-initiated path, launcher
self-update, and the version sanity check) and the disk-cost tradeoff were all decided in review. Next
step, when wanted: turn this into an `architecture/implementation/todo/` phase doc ready to build, per this
folder's own workflow (a planning doc moves to `done/` once there's an agreed plan, with a pointer to where
the real design now lives).
