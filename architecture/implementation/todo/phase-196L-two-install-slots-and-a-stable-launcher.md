# Phase 196L — Two install slots and a single stable launcher

**Status**: Built. Verified end to end on Linux against a real release; Windows and a real systemd unit are not
verified yet (see Progress).
**Plan reference**: `architecture/planning/done/two-install-slots-and-a-stable-launcher.md`. That doc settles
the design; this one says how it lands in this codebase. It also corrects two points there that did not survive
contact with the code (the load context and the process-global values; see "Where this departs from the plan").
**Supersedes** the parts of phase 159K that apply an update: the privileged `internal apply-update` step, the
kept rollback package, and the console's own apply button. 159K's release listing, `update --status` and health
probe carry over.

## What changes for an operator

| | before (158K/159K) | after (196L) |
| --- | --- | --- |
| What the service and PATH run | the tool's own shim in the tool directory | `dbdatasync[.exe]` **launcher** in the same directory, never replaced by an update |
| Where versions live | one `dotnet tool` install at the tool directory | `versions/a` and `versions/b`, each its own `--tool-path` install; `current.txt` says which is live |
| `dbdatasync update --apply` | Linux only; stop → `dotnet tool update` in place → start → health; rollback reinstalls | **Linux and Windows**; install into the inactive slot → stop → flip `current.txt` → start → health; rollback flips back |
| Rolling back later | reinstall the old version by hand | `dbdatasync update --rollback` (flip + restart + health) |
| Web console → Admin → Updates | lists releases, **Update…** button (Linux, `--self-update` unit only) | lists releases, shows slots, **copyable CLI commands** per release, with instructions; no apply button |
| `service install --self-update` | adds a root `ExecStartPre=+` apply step | accepted and ignored with a notice; no unit gets that step any more |

## Layout on disk

```
<tool dir>/                      /opt/dbdatasync, C:\Program Files\DbDataSync (CliOptions.DefaultToolDir)
  dbdatasync[.exe]               the launcher's apphost: what the service and PATH point at
  dbdatasync.dll                 the launcher itself (+ .runtimeconfig.json, .deps.json)
  current.txt                    "a" or "b" — the commit point
  versions/
    a/                           a plain `dotnet tool install --tool-path` root (.store/, its own shim)
    b/
```

The tool directory is the one operators already use, so the service's `binPath`/`ExecStart` and the PATH entry
(`/usr/local/bin/dbdatasync` symlink, Windows machine PATH) name **the same file before and after**. The legacy
shim at that path is replaced by the launcher. Nothing that points at it has to change.

## The launcher — `src/DbDataSync.Launcher`

A `net10.0` framework-dependent console app, `AssemblyName=dbdatasync`, `RollForward=latestMajor`. It has **no
package or project references**, so an ordinary update never needs to change it. `Main`:

1. Reads `current.txt` next to itself (`a`/`b`, trimmed, nothing else accepted).
2. Finds the one `DbDataSync.Cli.dll` under `versions/<slot>/.store/dbdatasync/<v>/dbdatasync/<v>/tools/<tfm>/any/`.
   Zero or more than one is an error that names the directory and says how to fix it.
3. Sets the process-global values to the slot's (next section), and wires `AssemblyDependencyResolver`
   (built against that dll) into `AssemblyLoadContext.Default.Resolving` and `.ResolvingUnmanagedDll`.
4. Loads the dll into the **Default** context, calls `Assembly.SetEntryAssembly` on it, and invokes its entry
   point with `args`. The return value becomes the exit code. An exception unwraps from
   `TargetInvocationException` and is rethrown with its original stack.

The pointer file name, the slot names and the store walk live in one source file,
`src/DbDataSync.Updates/SlotPaths.cs`. It is **compiled into both** the launcher (as a linked file) and
`DbDataSync.Updates`, so the launcher and the updater cannot disagree about the layout, and the launcher still
references nothing.

### Frameworks

The payload is an ASP.NET Core app. Shared-framework assemblies come from the frameworks named in the *host's*
runtimeconfig, never from a loaded dll's. So the launcher carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />`,
even though it uses nothing from it. Without that, `serve` fails to load `Microsoft.AspNetCore.*`. The payload's
own runtimeconfig has only default `configProperties` today (checked on a real pack). If one is ever added, it has
to be mirrored in the launcher, because runtime properties are fixed before the launcher's `Main` runs.

## Where this departs from the plan

**Default context, not a custom one.** `LibraryRegistry` arms its resolver on `AssemblyLoadContext.Default`.
`DriverPluginLoadContext.Load` returns null, deferring to Default, for every shared contract
(`DbDataSync.Drivers.Abstractions` and the rest). A payload in a private context would leave Default without
those, and every driver plugin would load a second copy or fail on type identity. Loading into Default with
resolver-backed events gives the payload exactly the context it has today.

**Process-global values are set once, by the launcher**, rather than fixed call site by call site:

| value | read by | set to |
| --- | --- | --- |
| `AppContext.BaseDirectory` (`APP_CONTEXT_BASE_DIRECTORY`) | `InstallLocator` callers, `WebRootLocator` (wwwroot, docs), `ApiOptions` TaskRunner/validator worker paths | the slot's `tools/<tfm>/any/` directory |
| deps file (`APP_CONTEXT_DEPS_FILES`) | `DependencyContext.Default`, and through it MVC application-part discovery | the slot's `DbDataSync.Cli.deps.json` |
| entry assembly (`Assembly.SetEntryAssembly`, .NET 9+) | `UpdateHostFacts` (running version), ASP.NET's `ApplicationName` | the slot's `DbDataSync.Cli` |
| `DbDataSync.Launcher.Root` / `.Slot` (new `AppContext` data) | `LauncherContext` in `DbDataSync.Updates`: "am I under a launcher, and which slot" | tool directory / `a`/`b` |

`Environment.ProcessPath` is left alone. It is the launcher, which is exactly what `service install` should
register and what `tool install` should put on PATH (the plan's point, confirmed).

A spike on 2026-09-28 checked this against a real `dotnet pack` + `dotnet tool install --tool-path versions/a`
of the current code, run through a launcher built this way. `version` printed the slot's version, a bad command
returned its exit code 1, and `serve` answered `/api/health` and routed `/api/about` through auth (401). It
created its SQLite state database (native `e_sqlite3`) and git repository (native libgit2), and served
`/docs/install.md` exactly as a direct run of the same install did.

## Packaging — one launcher per RID, inside every package

An apphost is a native, per-platform binary, and the tool package is RID-agnostic. So the Cli pack builds the
launcher once per RID and ships each complete output (apphost, dll, runtimeconfig, deps) under
`tools/net10.0/any/launcher/<rid>/`. RIDs: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`,
`linux-musl-arm64`, `osx-x64`, `osx-arm64`. That is under 1.5 MB. Publish runs one `dotnet build -r <rid>` of
the launcher per RID (`PublishLaunchers` in the Cli csproj). A separate restore per RID keeps the apphost packs
out of every ordinary restore. Cross-RID apphosts build fine on Linux (checked: `win-x64` produced
`dbdatasync.exe`, `linux-arm64` an ELF apphost). An ordinary `dotnet build` of the Cli copies only the build
machine's launcher (`launcher/<own rid>/`), so the inner loop and tests stay offline-friendly.

Installing the launcher (`LauncherInstaller`) copies `launcher/<SlotPaths.PortableRuntimeIdentifier()>/` from the
**current slot's** payload into the tool directory. It only copies when a file differs, writes each file to a
temp name and renames it into place, and on Windows first renames a file that is in use (the running launcher's
own `.exe`) aside to `*.old`. `*.old` files are deleted best-effort on the next run. An unknown RID refuses
with a message; that install stays legacy, and the printed-commands path still works.

## Updating — `DbDataSync.Updates` + `dbdatasync update`

- **`SlotLayout(toolDirectory)`**: current slot (from the pointer), the other slot, each slot's installed
  version (the `.store/dbdatasync/<v>` directory name), `Flip(to)` (temp file + rename, so a torn write is
  impossible), and `Check()`. `Check()` is the plan's v1 sanity check. It warns when the pointer is missing or
  names an empty slot. It notes, not warns, when the current slot is **older** than the other one. That is the
  normal state after a rollback, so it is shown with the command that goes forward again. Shown by
  `update --status` and the console.
- **Installing into the inactive slot** (`SlotInstaller`). *Changed while building:* the slot's directory is
  deleted and the version gets a plain `dotnet tool install --tool-path` (`UpdateCommands.IntoEmptySlot`). This
  replaces planning `update` or `uninstall + install` against the slot's version. The slot holds whatever was
  current two updates ago: newer, older, or half-installed by an interrupted run. Nothing runs from it, so starting
  clean is always right, and one command covers every case. The result is checked (exactly one runnable payload
  of that version) before anything depends on it. If the target is already in the inactive slot, nothing is
  installed; the flow only flips. If the target is what is running, there is nothing to do. `UpdatePlan` itself
  is unchanged and still describes the update against the running version.
- **`UpdateApplyFlow`**, one path for both directions, `--apply` and `--rollback`: install into the inactive
  slot (apply only) → stop the service → flip → start → health (`IHealthProbe`, unchanged). If healthy, record
  success; the old slot is untouched. If not, flip back, restart, and record a rollback. With no service it
  flips and says the next `dbdatasync` run uses the new version. Progress is recorded in the existing
  `UpdateStateStore` (`<data>/updates/update-state.json`), so the console shows an update the CLI is doing.
- **Service control on Windows**: `ServiceController` stop/start with `WaitForStatus` (the package is already
  in the graph through `Microsoft.Extensions.Hosting.WindowsServices`). Linux keeps `systemctl`.
- **Windows is on.** The reason 159K switched it off was replacing files a running process holds. The install
  now only ever targets the slot that is not running. An *interactive* old `dbdatasync` still running from
  that slot makes `dotnet tool` fail. Nothing has been flipped at that point, so this is reported as such.
- **Privileges**: the flow checks up front that it can write the tool directory, and refuses with the
  `sudo …`/"elevated prompt" sentence rather than failing halfway.
- **`update --rollback [--yes]`**: the same flow without the install step, refused when the other slot is empty.
- **`update --status`**: slots, versions, `Check()` findings, then the existing history.

## Migrating an existing install (automatic, loud)

`update --apply`, `service install` and `launcher repair` all check `LauncherContext` first. If the process is
not under a launcher but is a `--tool-path` install, they convert that install before anything else, and print
that they are doing it:

1. Install the **running** version into `versions/a`. The source is its own nupkg from the legacy `.store` via
   `--add-source` (`ToolStore.FindInstalledNupkg`), so no network is needed. nuget.org is the fallback for a
   stable or beta version.
2. Install the launcher into the tool directory, replacing the legacy shim at the same path (renamed aside on
   Windows, where it is the running image).
3. Write `current.txt` = `a`.
4. Re-register a registered service: rewrite and reload the systemd unit (which also drops 159K's
   `ExecStartPre` step), or `sc config … binPath=` on Windows. The path is the same, so on a standard install
   this changes nothing but the unit's retired lines.
5. Delete the legacy `<tool dir>/.store` best-effort. On Windows the converting process is still running from
   it, so a later run finishes the job. Until then `dotnet tool list --tool-path <tool dir>` would still list
   it, and running `dotnet tool update --tool-path <tool dir>` by hand would overwrite the launcher. The docs
   say to use `dbdatasync update` from now on.

A **global** (`-g`) install is not converted. Its directory belongs to `dotnet tool`, whose shim and manifest
a launcher cannot live beside. `--apply` there refuses and prints the phase-158 commands, which still work for
a user-owned install. The docs already steer services to the machine-wide tool directory.

## Updating the launcher itself — `dbdatasync launcher repair`

`dbdatasync launcher repair`: under a launcher, it re-copies the current slot's bundled launcher over the
installed one and re-registers a registered service. From a legacy install, it runs the migration above.
`service install` does the same launcher refresh as part of registering, so re-running it is also a repair.
Ordinary swaps never touch the launcher.

## Retired (the service-initiated path)

- `AdminUpdateController`'s `POST apply`; `UpdateService.RequestAsync`, the drain, and exit code 75
  (`ServeCommand`); `UpdateDrainState`/`UpdateDrainMiddleware` and the scheduler's drain check;
  `UpdateConfirmationService`.
- `internal apply-update`. It stays as a **no-op that exits 0**, because an old unit's `ExecStartPre=-+` may
  still call it until the unit is rewritten. `UpdateApplier`'s pending/confirm/applied records, the kept
  rollback package and the privileged directory all go (the doc's `UpdateApplier.cs:233-239,291-340,372-390`).
- The systemd unit's `ExecStartPre` step, `DBDATASYNC_SELF_UPDATE`, and `SuccessExitStatus/RestartForceExitStatus=75`.
  `service install --self-update` prints that it is no longer needed. The `config check` finding about a unit
  without the apply step goes.
- `DbDataSync:Updates:DrainTimeoutSeconds` and `ConfirmAfterSeconds` leave the config catalog.
  `DbDataSync:Updates:Mode` stays, meaning "the console may look up releases" (it makes outbound calls).
  `Channels` stays.

## Web console — Admin → Updates keeps its page, gains commands

What the page shows:

- **This installation**: the running version and slot, the other slot's version, and `Check()` findings.
- **Available releases**: same list. Each row's button becomes **Commands**, which opens a panel with the
  commands for that version, each with a copy button.
  - Linux: `sudo dbdatasync update --to <v> --apply`.
  - Windows: the same without `sudo`, "in an elevated PowerShell".
  - `--repo <path>` is always added, because the server knows its data directory. *Changed while building:* the
    default lives in the CLI project, and an explicit `--repo` is never wrong where a defaulted one could be.
  - Plus a one-line explanation of what will happen, including that a legacy install converts itself first.
- **Roll back**: when the other slot holds a version, the `update --rollback` command.
- **Progress**: when the CLI is mid-update (phases recorded in the state file), the same progress card as
  today, now fed by the CLI.
- **History**: unchanged.

The commands are built **server-side** (`UpdateCliCommands` in `DbDataSync.Updates`), because the server knows
its OS, data directory and install kind. They are also the one place the CLI's own messages quote them. A
container or a non-tool install gets no commands, only its reason, as today. With `Updates:Mode` disabled, the
page still shows the plain `dbdatasync update` command, which lists releases from the server's own shell and
needs no console setting.

## Checkpoints (one commit each, pushed to `origin/dev`)

1. This doc + the planning doc moved to `planning/done/`.
2. `DbDataSync.Launcher` + `SlotPaths` + per-RID packaging in the Cli pack + tests.
3. `DbDataSync.Updates`: `SlotLayout`, `LauncherContext`, `LauncherInstaller`, slot planning, `UpdateCliCommands`;
   retire the applier's privileged pieces.
4. CLI: slot-based `update --apply/--rollback/--status`, migration, Windows service control, `launcher repair`,
   `service install` changes; retire `apply-update` and the unit's self-update lines.
5. API + web: retire the console apply path; slots and commands in status; the page.
6. Docs (`install.md`, `configuration.md`), end-to-end verification on a real pack, Progress and Retrospective here.

## How to verify

- Unit: `SlotPaths`/`SlotLayout` (pointer parsing, store walk, flip atomicity, `Check()`); planning against the
  inactive slot; `UpdateApplyFlow` with fakes (healthy → flipped; unhealthy → flipped back; install failure →
  never flipped; service stop failure → nothing changed); migration steps with a fake runner; the commands
  text; the unit text without the self-update lines.
- **End-to-end on Linux, for real**:
  1. Pack two versions into a folder feed and `dotnet tool install --tool-path <dir>` the older one, legacy
     style.
  2. `update --apply` to the newer one: it converts (loud), installs into `b`, flips, and the next run reports
     the new version.
  3. `update --rollback` goes back. `update --status` shows both slots.
  4. `serve` through the launcher answers `/api/health`.
- Web: Playwright against the page with the status/releases API stubbed, as today's spec does; copy buttons and
  commands asserted.
- **Not verifiable here**: Windows as a whole (service stop/start, renaming the running launcher `.exe`, the
  apphost finding `hostfxr` under a service account), systemd under a real system unit, macOS apphost signing.
  These are recorded as such in Progress, like 159K's.

## Progress

**Built for Linux and Windows; verified end to end on Linux only.** It stays in `todo/` until a real Windows host
and a real systemd system unit have run it, as 159K did.

- **End to end, for real (2026-09-29, Linux, no service).** A `dotnet pack` of this code as 2026.9.29.1 was
  installed the pre-196L way (`dotnet tool install --tool-path tool`).
  - `tool/dbdatasync update --to 2026.9.25.1104 --apply --yes`, against the real nuget.org release, printed the
    conversion. It installed 2026.9.29.1 into `versions/a` from its own kept nupkg (offline) and replaced the shim
    with the launcher. It then installed 2026.9.25.1104 into `versions/b` from nuget.org and flipped to `b`.
  - `tool/dbdatasync version` then printed 2026.9.25.1104, so **the launcher ran a real release built before the
    launcher existed**.
  - With the pointer set back to `a` by hand (slot b's code predates slots): `update --status` listed both slots,
    and `update --rollback --yes` flipped to `b`, which then ran.
  - `update --to 2026.9.25.1104 --apply` from `a` found it already in `b` and only flipped. That run also removed
    the legacy `.store`. `launcher repair` found the launcher up to date.
  - `serve` through the launcher answered `/api/admin/update/status` with the right running version,
    `installKind` `ToolPath`, both slots, and the commands. So the entry-assembly and base-directory overrides hold
    in a real host.
  - Two cosmetic things it found were fixed: a trailing `/` on the tool directory, and an "Installing …" line
    printed when the slot already held the version.
- **Not verified, because it needs a host this was not built on**:
  - Windows as a whole: `WindowsServiceControl` against the real SCM, renaming the running `dbdatasync.exe` aside
    during conversion or repair, the apphost finding `hostfxr` under a service account, and `sc config` on
    conversion.
  - The stop, flip, start and health sequence under a real systemd **system** unit as root, and the unit rewrite on
    conversion.
  - The health-failure switch-back is covered by tests with a fake service and health probe only.
  - macOS apphost signing: the `osx-*` launchers are built on Linux and not signed.
- **Checkpoints 5–6 (web console + docs)**: built. Admin → Updates keeps its page. It shows both slots, a "How to
  update" card with the list/apply/rollback/status commands, and a **Commands…** dialog per release with that
  version's exact command. Every command has a copy button and a sentence on where to run it and what it does. An
  update run from a shell shows its progress, including through the restart gap. The Playwright spec (10 tests)
  passes in Chromium, including reading the copied text back from the clipboard. Its screenshots are
  `screenshots/admin-updates/196-*`; the `159-*` ones showed the retired apply flow and are removed.
  `docs/install.md` and `docs/configuration.md` describe slots; 159K's doc notes what this supersedes.
- **Checkpoints 3–4 (update library + CLI)**: built and unit-tested (Updates 178, Cli 246 passing).
  `SlotLayout`/`LauncherContext`, `SlotInstaller`, `LauncherInstaller`, `SlotMigration`, `UpdateCliCommands`;
  `UpdateApplier`'s pending/confirm/applied/kept-package machinery and the privileged workspace are gone.
  - `update --apply/--rollback/--status` run on slots, and Windows is on (`WindowsServiceControl` waits on the
    SCM).
  - The conversion is automatic and loud, and `launcher repair` exists. `service install` puts the launcher in
    place (converting first if needed).
  - `internal apply-update` is a no-op, and units lose the self-update lines. `config check` now warns about a unit
    that still runs the retired step.
  - API: the apply endpoint, drain, confirmation service and exit 75 are gone. `UpdateService` is read-only and
    returns slots and commands.
- **Checkpoint 2 (launcher + packaging)**: built. `src/DbDataSync.Launcher`, `SlotPaths` linked into it, and
  launchers for all eight RIDs in a real `dotnet pack` (31 s, 1.1 MB; `win-*` carry `dbdatasync.exe`). The
  release and snapshot workflows now fail a package that lacks any of them. The container build passes
  `LauncherRuntimeIdentifiers=none`. `LauncherTests` runs the real launcher against this build laid out as a
  slot: the version is printed, the exit code is passed through, and a missing pointer or empty slot names its
  fix.

## Retrospective

- **The plan's load context would have broken every driver plugin.** "A custom `AssemblyLoadContext`" reads as the
  textbook answer. But `DriverPluginLoadContext` and `LibraryRegistry` both defer shared contracts to Default. A grep
  for `AssemblyLoadContext` before writing the phase doc caught it; the spike would not have, because it exercised
  no plugin.
- **A launcher needs more than the plan listed.** Two things were missing. First, the ASP.NET framework reference:
  frameworks come from the host's runtimeconfig. Second, `APP_CONTEXT_DEPS_FILES` and `Assembly.SetEntryAssembly`
  as well as the base directory. The entry assembly decides the running version the console reports, and the deps
  file decides MVC's application parts. Setting all of them once, in the launcher, was simpler and harder to get
  partly wrong than fixing call sites.
- **An apphost is per-platform and a tool package is not.** That became eight launchers in every package, picked by
  a portable RID computed from OS and architecture. The distribution-built SDK here reports `ubuntu.24.04-x64`,
  which would never match a directory name.
- **Emptying the inactive slot beat planning against it.** The slot holds whatever was current two updates ago:
  newer, older, or half-installed. Deleting it and running one `tool install` covers all of those. Choosing
  `update` or `uninstall + install` by direction would have needed a case per possibility.
- **The first end-to-end run found only cosmetics** (a trailing slash, one misleading line). The one path it could
  not reach, the real service on either OS, is the one the Progress section lists as unverified.
