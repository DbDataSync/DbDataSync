# Phase 123 — `dbdatasync tool install` / `tool uninstall`, and machine-wide install docs

**Status**: Done.
**Plan reference**: `architecture/planning/done/machine-wide-dotnet-tool-install.md`. Built on phase 112
(`CliOptions.DefaultRoot`), phase 111 (`SystemdService`), phase 110 (`SetupCommand`) — all confirmed
already in `done/` before starting. Independent of the 034/035/038 queue and the phase-109g–109i series.

## What this built

### `CliOptions.DefaultToolDir` and `CliOptions.IsUnderUserProfile`

`DefaultToolDir` — `%ProgramFiles%\DbDataSync` (Windows), `/usr/local/dbdatasync` (macOS),
`/opt/dbdatasync` (Linux/other Unix) — the binary analog of `DefaultRoot`'s machine-wide data
directory. `IsUnderUserProfile(path)` — checked against `SpecialFolder.UserProfile`/`$HOME`, plus a
literal `/home/`/`/root` fallback — is the one predicate every "warn or refuse" decision below shares,
so `ToolCommand`, `ServiceCommand`, `SystemdService` and `SetupCommand` never drift onto separately
reasoned checks.

### `dbdatasync tool install` / `tool uninstall` — new `src/DbDataSync.Cli/ToolCommand.cs`

`IToolPathEnvironment` (elevation, Unix symlink read/create/remove, chmod, Windows Machine `PATH`
read/write, macOS `/etc/paths.d` read/write/delete) behind the same fake-for-tests seam
`SystemdService`'s own `ISystemdEnvironment` already established; `RealToolPathEnvironment` is
production. Directory inference: `Path.GetDirectoryName(Environment.ProcessPath)`, overridable with
`--dir`; warns (never refuses) when the inferred or given directory is under a user profile.

- **Linux/other Unix**: `chmod -R a+rX <toolDir>` (own recursive walk over `File.GetUnixFileMode`/
  `SetUnixFileMode`, matching `chmod`'s own capital-`X` semantics — execute only where already
  executable or a directory), then `ln -sfn <toolDir>/dbdatasync /usr/local/bin/dbdatasync`
  (`File.CreateSymbolicLink`, replacing whatever is already there). Not elevated → prints the `sudo`
  command, exits 1, writes nothing.
- **macOS**: writes `/etc/paths.d/dbdatasync` containing the tool directory, instead of a symlink —
  macOS's shells don't treat an arbitrary `/usr/local/bin` symlink as adding to `PATH` the way Linux's
  do, so this needed the platform's own idiom rather than reusing the Linux mechanism.
- **Windows**: appends to the **Machine** `Path` via `Environment.SetEnvironmentVariable(...,
  EnvironmentVariableTarget.Machine)` (never `setx`, which silently truncates past 1024 characters),
  then broadcasts `WM_SETTINGCHANGE` via a `DllImport`'d `SendMessageTimeoutW` so already-open windows
  notice. `[DllImport]`, not `[LibraryImport]` — the source-generated form needs
  `AllowUnsafeBlocks`, which nothing else in this project does, and two P/Invoke calls didn't seem
  worth flipping that switch for.
- **Both idempotent**: install twice reports "already linked"/"already on PATH" and changes nothing;
  uninstall with nothing to remove says so and still exits 0.
- `tool uninstall` removes exactly what `tool install` wrote and prints the
  `dotnet tool uninstall --tool-path <dir>` line to finish the job — it never touches the `--tool-path`
  payload itself.

### `service install` awareness

`ServiceCommand.Install` (Windows) and `SystemdService.Install` (Linux) both now check
`CliOptions.IsUnderUserProfile(executable)`:

- **Windows, or Linux with a non-default `--repo`** (no `ProtectHome=yes`): a warning, printed, then
  continues.
- **Linux with the default machine-wide `--repo`** (the unit hardens with `ProtectHome=yes`, which
  hides user-profile paths from the service entirely): a **hard error** — refuses before writing
  anything, prints the machine-wide install sequence, exits 1. This is the bug phase 123 exists to
  catch before an operator hits "installed cleanly, fails to start."
- `SystemdService.RenderUnit` gained an optional `dotnetRoot` parameter → `Environment=DOTNET_ROOT=<dir>`
  in `[Service]` when non-null. `SystemdService.ResolveDotnetRoot()`: `DOTNET_ROOT` from the environment
  first, else three `.Parent` steps up from `RuntimeEnvironment.GetRuntimeDirectory()`
  (`.../shared/Microsoft.NETCore.App/<ver>/` → the dotnet root) — verified against a real `dotnet`
  executable at that root before trusting it, confirmed empirically on this sandbox
  (`/usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.11/` → `/usr/lib/dotnet`, which does hold
  `dotnet`) before relying on the walk-up count in the shipped code.
- Both `Install` methods gained a "Next: `dbdatasync config check`" line on success.

### `config check` — new `RuntimeDiscoverabilityCheck`

Windows is always `Ok` (the runtime installs machine-wide there by construction). Elsewhere: `Ok` if
`DOTNET_ROOT` is set, `/etc/dotnet/install_location` exists (written by the official install script and
every major package manager's `dotnet-host` package — confirmed present on this sandbox, pointing at
`/usr/lib/dotnet`), or a `dotnet` binary exists at one of five well-known paths (Debian/Ubuntu, RHEL/
Fedora's `lib64`, a tarball extract, Snap). `Warn` otherwise, naming the fix — never `Fail`, since this
is a heuristic and an unusual-but-working layout shouldn't block `config check`.

### `SetupCommand` step 6

`WriteMachineWideToolInstallBlockIfNeeded` prints the two-line machine-wide install sequence above the
service-install line whenever the tool's own executable is under a user profile — split into a
predicate-taking `internal` overload specifically so `SetupCommandTests` can drive it without needing
the *test process's own executable* to live under a profile (it doesn't, on this sandbox — verified
empirically, which is exactly why the split was necessary rather than optional).

### `tools/install-local-tool` + `.cmd` (new)

A thin `dotnet pack`/`dotnet tool update` wrapper for CLI dev-loop iteration — packs every run with a
timestamped `0.1.0-local.<stamp>` version (so `tool update` always sees something new), no staleness
check of its own (same reasoning `tools/dev-harness` gives). `--global` (default, zero friction),
`--machine-wide` (packs, then `sudo dotnet tool update --tool-path <DefaultToolDir>` followed by
`<DefaultToolDir>/dbdatasync tool install` — this phase's own feature, exercised against a local build),
or `--tool-path <dir>`; `--no-spa` skips the SPA build; `--uninstall` reverses whichever target the same
flags select.

### Docs

`docs/install.md` (new — `docs/` didn't exist before this phase): the platform-specific copy-paste
install/update/uninstall sequences, what `service install` needs first, and which contexts see
`dbdatasync` on `PATH` (a login shell) versus which don't (`cron`, needing the absolute path).
`docs/getting-started.md` stays unwritten — nothing here depended on it existing, so `install.md` stands
alone per the plan's own contingency. `src/DbDataSync.Cli/README.md` — the stale `%LOCALAPPDATA%`/
`~/.local/share` paths (pre-phase-112) replaced with the real machine-wide defaults, plus a new
Linux-systemd section (it previously only documented the Windows service) and a machine-wide-install
section pointing at `docs/install.md`. `Help.cs` gained the `tool install|uninstall` entry.

## How it was verified

- `dotnet build DbDataSync.slnx` / full `Category!=Integration` suite green (108 tests in
  `DbDataSync.Cli.Tests` alone, up from 85 before this phase).
- New `tests/DbDataSync.Cli.Tests/ToolCommandTests.cs` (14 tests, via `FakeToolPathEnvironment`): not
  elevated → the `sudo` command and nothing written; elevated → the real symlink + chmod call recorded;
  a second install is a no-op; uninstall removes the symlink and prints the `dotnet tool uninstall`
  line; uninstalling not-elevated refuses; uninstalling nothing installed says so and exits 0; a
  `--dir` under a real (temporary) directory inside `$HOME` warns but still proceeds. The Windows
  Machine-`PATH` logic has no OS branch to fake on this Linux sandbox, so it's tested as the pure
  `ToolCommand.AddToPath`/`RemoveFromPath` functions instead (gains an entry once, not twice; removes
  exactly one entry, leaving the rest byte-for-byte) — exactly what a real Windows run calls, just not
  reachable through `OperatingSystem.IsWindows()` from here.
- `SystemdServiceTests` gained: `RenderUnit` with/without a `dotnetRoot` emits/omits the
  `Environment=DOTNET_ROOT=` line; `ResolveDotnetRoot()` against the real sandbox finds a real `dotnet`
  executable; `Install` with an `executableOverride` under `$HOME` and the default (hardened) root
  refuses with `ProtectHome=yes` in the message and writes nothing; the same override with a
  non-default root warns but still registers. `executableOverride` is a new optional parameter on
  `SystemdService.Install`, defaulting to `Environment.ProcessPath` exactly as before — added because
  the real `Environment.ProcessPath` inside a test process turned out to be the test host's own binary
  (confirmed via a throwaway probe project), never under `/home`, so the hard-error path was otherwise
  untestable without actually installing something there first.
- `ReadinessChecksTests`: the new "Runtime" check appears in the JSON-shaped list, and passes for real
  (not faked) on this box via its `/etc/dotnet/install_location` file.
- `SetupCommandTests`: the predicate-taking overload prints the two-line block when told it's under a
  profile, and prints nothing when told it isn't.
- New `tests/DbDataSync.Cli.Tests/InstallDocsTests.cs`: `docs/install.md` contains this platform's own
  `CliOptions.DefaultToolDir` value verbatim — catches the doc and the constant drifting apart, the
  literal test the plan doc asked for.
- **Manual, on this real (non-elevated) sandbox**: `dbdatasync tool install`/`uninstall` both correctly
  refuse with the `sudo`/elevated command and write nothing when not root (this sandbox never has
  passwordless `sudo`, so the actual root-succeeds path could only be verified through the fake — see
  the open item below); `dbdatasync tool`/`--help` render correctly.
- **Manual, `tools/install-local-tool --global --no-spa`**, for real: packed every project, installed
  `dbdatasync` onto this account's own `~/.dotnet/tools` `PATH`, ran `dbdatasync version`/`--help`
  successfully, then `--uninstall` removed it cleanly (`dbdatasync version` afterward correctly fails
  with "No such file or directory"). The full pack → tool-update → run → uninstall loop, not just a
  build.
- `npm run build` unaffected (no SPA files touched this phase).

## Decisions made

- **`[DllImport]`, not `[LibraryImport]`**, for `geteuid()` and `SendMessageTimeoutW` — the
  source-generated form requires `<AllowUnsafeBlocks>`, which would be a project-wide setting change
  for two P/Invoke calls that are this project's first ever. The classic attribute needs no such change.
- **`Chmod`'s CA1416 platform-compatibility warning suppressed with a scoped `#pragma`**, not threaded
  through as a `[SupportedOSPlatform]`/`[UnsupportedOSPlatform]` attribute pair — the call happens
  through `IToolPathEnvironment`, an interface boundary the analyzer can't see through, and marking the
  interface method itself would force every future implementer (including any Windows-side test double)
  to also carry the attribute for no real benefit, since `Chmod` is only ever reached from the already
  `OperatingSystem.IsWindows()`-gated Unix install branch.
- **`SystemdService.Install` and `SetupCommand`'s tool-install-block helper both gained test-only
  override parameters** (`executableOverride`, and a predicate-taking overload respectively) after
  discovering — empirically, via a throwaway probe project — that `Environment.ProcessPath` inside a
  `dotnet test` process is the test host's own binary, never under a user profile on this sandbox. Both
  overrides default to the real value exactly as before; production behavior is unchanged.
- **`tools/install-local-tool` hardcodes Linux/macOS's `DefaultToolDir` values** (`/opt/dbdatasync`,
  `/usr/local/dbdatasync`) rather than deriving them from a build of the CLI itself — a shell script
  can't reference a C# constant, and duplicating two literal path strings the constant itself
  documents was judged simpler than, say, having the script build and run the CLI just to ask it its
  own default.
- **`--global` confirmed as `tools/install-local-tool`'s default**, per the plan's own leaning — the
  manual smoke test above is exactly the zero-friction loop it exists for.

## What's explicitly out of scope / not built

- A native OS installer (`.msi`/`.deb`/`.rpm`/Homebrew) — a much larger, separate phase.
- Package acquisition or self-update — `dotnet tool install|update --tool-path` does all of it; `tool
  install` never downloads anything, only wires up what's already there.
- The container — it runs an absolute `dotnet /app/...`; there is no `PATH` question for it.
- Changing `serve`/`service`'s executable resolution — still `Environment.ProcessPath`; this phase only
  changes what that path typically *is*, and whether a person can also type a bare `dbdatasync`.

## Open questions — resolved, or explicitly still open

1. **SELinux/AppArmor** for executing `<DefaultToolDir>/dbdatasync` as a `nologin` service user — **not
   verified**; this sandbox has neither enforcing SELinux nor a stock RHEL 9/Ubuntu 24.04 to check
   against. Left as a real, documented risk rather than guessed at: `tool install` does not run
   `restorecon`, and `docs/install.md` carries no manual `chcon` line, because neither was confirmed
   necessary or sufficient. Whoever next runs this on a hardened RHEL box should check and, if needed,
   add both — the plan doc's own phrasing already anticipated this might not get resolved in one pass.
2. **`DOTNET_ROOT` derivation** — resolved: verified empirically on this sandbox (see above); the
   three-level walk-up plus the "confirm a real `dotnet` executable is actually there" guard is what
   shipped, matching the leaning in the original plan.
3. **`docs/` location** — resolved: `docs/install.md` stands alone; `docs/getting-started.md` was and
   remains unwritten.
4. **`tools/install-local-tool` default target** — resolved: `--global`, confirmed via a real
   pack/install/run/uninstall smoke test rather than left as an assumption.
