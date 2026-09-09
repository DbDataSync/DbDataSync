# Phase 110 — the interactive `dbdatasync setup` command (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/todo/app-and-service-setup.md`. Independent of phase 109,
but 109c (`ProviderCommand` / `ProviderInstaller`) and 109d (`DriverCommand` / `DriverTemplates`)
are done, so `setup`'s state-database and drivers steps have real commands to call.

## What this builds

Three things, in the CLI project (`src/DbDataSync.Cli`):

- **`dbdatasync setup`** — an interactive command: review an existing configuration or walk a fresh
  one through the settings for its platform.
- **`dbdatasync doctor`** — the non-interactive check engine `setup`'s review screen renders and CI
  can call headless.
- **A `Prompt` helper** — text-with-default / yes-no / single-choice / multi-choice / masked-secret
  over `Console`, with a seam so tests drive it from a scripted answer list. No package dependency
  (the CLI project still has none).

### `Prompt` — `src/DbDataSync.Cli/Prompt.cs`

```csharp
public interface IPromptIo   // the seam; the real impl wraps Console
{
    string? ReadLine();
    ConsoleKeyInfo ReadKey(bool intercept);
    void Write(string s);
    bool IsInteractive { get; }   // !Console.IsInputRedirected && a real console
}

public sealed class Prompt(IPromptIo io)
{
    string Text(string label, string? @default = null);
    bool YesNo(string label, bool @default);
    T Choice<T>(string label, IReadOnlyList<(T Value, string Label)> options, T @default);
    IReadOnlyList<T> MultiChoice<T>(string label, IReadOnlyList<(T Value, string Label)> options);
    string Secret(string label);              // no echo
    string HardConfirm(string label, string phrase);  // must type `phrase` exactly
}
```

Tests construct a `Prompt` over a `ScriptedPromptIo(["", "y", "2", ...])`.

### `dbdatasync doctor` — `src/DbDataSync.Cli/DoctorCommand.cs`

Read-only. Builds the same `IConfiguration` chain `DbDataSyncHost` builds (config file + env +
`--DbDataSync:*`) **without** `DbDataSyncHost.Build` / Kestrel, resolves the options objects it
already has (`ApiOptions.FromConfiguration`, `PasskeyOptions.FromConfiguration`,
`AuthOptions.FromConfiguration`, `CertificateOptions.FromConfiguration`), and runs a list of checks:

```csharp
public enum CheckStatus { Ok, Warn, Fail }
public sealed record CheckResult(string Name, CheckStatus Status, string Detail, string? Fix);
public interface IReadinessCheck { Task<CheckResult> RunAsync(DoctorContext ctx); }
```

The checks, and what each reuses:

| check | reuses |
| --- | --- |
| **Repo** — resolvable, valid git repo, `dbdatasync.config.yaml` parses | `DbDataSyncRoot.Resolve`, `DbDataSyncConfigFile.Read` |
| **State store** — SQLite dir writable, or MsSql/Postgres provider present + a connection opens with the spliced credential; schema version current or migratable | `StateDatabase.Factory`, `ProviderRegistry` (109c), `StateDatabase` version read |
| **Providers / drivers** (109c/d) — every manifest's closure restored (`sync` needed?); every `driverType` in a `connection.yaml` resolves | `ProviderInstaller` state, `DriverLoader` |
| **Auth** — a method configured or `Disabled` stated; `PasskeyOptions.Problem()` null; bound-URL host matches `RelyingPartyId`; HTTPS URL → cert valid, not near expiry | `PasskeyOptions.Problem()`, `CertificateOptions`, the `cert status` logic |
| **Binding** — console URL reachable from this host; warn if HTTP and not loopback | a plain `HttpClient` GET of `/api/health` (as `HealthCommand` does) |
| **First admin** — a user exists; if not, the current bootstrap invite | `UserStore`, `InviteStore` |

`RunAsync` prints each result (✓ green / ! yellow / ✗ red + the fix), returns `0` when no `Fail`,
`1` otherwise. `--json` for a machine reader (CI).

### `dbdatasync setup` — `src/DbDataSync.Cli/SetupCommand.cs`

`RunAsync(string[] args, IPromptIo io)` — `io` defaults to the real console; tests inject a script.

**Not interactive → refuse.** `if (!io.IsInteractive)` print *"setup is interactive — run
`dbdatasync doctor` to check a configuration, or edit `dbdatasync.config.yaml` (see CONFIG.md)"* and
return `1`.

#### Step 0 — find or choose the root

- `var candidate = DbDataSyncRoot.Resolve(args)`.
- `ExistingSetup.DetectedAt(candidate)` — a single predicate (see Open questions): an uncommented
  key in `dbdatasync.config.yaml`, **or** a state DB present, **or** the config repo has more than
  the one starter commit. True → **review screen** for `candidate`.
- Otherwise: `Prompt.Text("Config folder", CliOptions.DefaultRoot)`. Then
  `ExistingSetup.DetectedAt(chosen)` again → if true, print *"That folder already has a DbDataSync
  configuration."* and go to the **review screen**; else the **walk-through**.

#### Review screen

Runs `DoctorCommand`'s check list in-process, prints it, then a `Prompt.Choice` menu:

- **Reconfigure** → a second `Choice`: state database · authentication · drivers · service (Windows)
  · certificate (Windows). Each re-enters that walk-through step with the current values pre-filled
  and writes only what changed. Changing the passkey RP id or the state engine prints the
  consequence loudly first (existing passkeys stop working; state does not migrate).
- **Print effective configuration** → the merged `IConfiguration` dumped, values under a secret ref
  shown as `••••`.
- **Reissue the first-run invite** → offered only when the "first admin" check is `Fail`; calls the
  same path `InviteCommand` does.
- **Start DbDataSync** → `ServeCommand.RunAsync` in-process (foreground), or `sc start` the service.
- **Exit** → `0`.

#### Walk-through

Each step is a method (`ConfigureRoot`, `ConfigureUrl`, `ConfigureState`, `ConfigureDrivers`,
`ConfigureAuth`, `ConfigureService`, `ConfigureCert`) so the review screen can call one in isolation.
Every write goes through `DbDataSyncConfigFile.SetValue(root, "DbDataSync", key, value)` (already
exists) or a subcommand's extracted core.

1. **Root** — confirm the folder; `ServeCommand.Prepare(root)` (already `internal static` — share it).
2. **Console URL** — `Prompt.Text("Console URL", "http://localhost:5080")`; a `YesNo("Reachable at a
   hostname other than localhost?")` branch that prompts the host, forces `https`, and stashes the
   host for steps 5 and 7. Writes `DbDataSync:Url`.
3. **State database** — `Choice(SQLite | SQL Server | PostgreSQL)`.
   - SQLite → nothing (the default path is derived).
   - server → `Text` the credential-free connection string → `DbDataSyncConfigFile.SetValue(…,
     "StateEngine", …)` + `"StateConnectionString"`; `Secret("State database password")` →
     `SecretStore.Store(SecretRefs.ForAppSetting("stateConnectionString"), …)` (what `SecretCommand`
     does); `YesNo("Install the Microsoft.Data.SqlClient provider now?")` → `ProviderInstaller`
     (required post-109g, skipped while it is a hard reference); open a test connection and report.
4. **Common drivers** — `MultiChoice(SQL Server, PostgreSQL, DuckDB, MySQL/MariaDB, Oracle, other)`.
   Built-ins: no action. Each other: `DriverInstaller` + `DriverTemplates.Render("<engine>", …)` —
   i.e. exactly what `driver install --from <engine>` does today (109d).
5. **Authentication** — `Choice(Passkeys | Windows groups | None)`.
   - Passkeys → RP id defaults to `localhost` or the step-2 host; `SetValue` `Auth:Passkeys:RelyingPartyId`
     + `Origins`; construct a `PasskeyOptions` and refuse to finish if `Problem()` is non-null.
   - Windows → `Text` the admin and viewer group names → `Auth:AdminGroup` / `Auth:ViewerGroup`.
   - None → `HardConfirm("DbDataSync will accept every request.", "ALLOW")` → `Auth:Disabled=true`.
6. **Windows only — service** — `OperatingSystem.IsWindows()` gate. `YesNo("Register as a Windows
   service?")` → `Prompt.Text("Service account", "LocalSystem")`; if not elevated
   (`WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)` is
   false) do everything else and print the one `dbdatasync service install --repo … --account …`
   line to run elevated; else call the extracted `ServiceCommand` install core.
7. **Windows only — certificate** — `YesNo("Set up the TLS certificate?")` →
   `Choice(self-signed | enroll from template | bind existing thumbprint)` → the matching
   `CertCommand` path, bound to the step-2 URL's port.
8. **Finish** — commit `dbdatasync.config.yaml` (a `GitCommitService` commit like `Prepare`'s);
   summarise what was set; print the first-run invite URL and note `<repo>/FIRST-RUN.txt`;
   `YesNo("Start DbDataSync now?")`.

### First-run invite file — `src/DbDataSync.Api/Auth/BootstrapInvite.cs`

Small change riding along: when it mints the bootstrap invite, also write
`<RepoRoot>/FIRST-RUN.txt` (the URL + a one-paragraph explanation); delete it in the same branch
that already calls `invites.DeleteBootstrapInvites()` once a user exists. `RepoRoot` comes from
`ApiOptions`. A starter `.gitignore` entry keeps it out of git (Open questions).

## What this phase does not build

- The `docs/getting-started.md` rewrite and the reverse-proxy guide — documentation, a separate
  small task once `setup` exists to point at.
- Changing `serve`'s first-run console output (the framed block) — worthwhile but independent; can
  land with the docs.
- Any migration `--dry-run` for the state store — noted in the plan, not this phase.
- A non-interactive `setup` mode — deliberately; the config file plus the subcommands are that.

## How to verify when built

- `dotnet build` clean; the CLI project still has zero `PackageReference`s.
- **`PromptTests`** — each helper against a `ScriptedPromptIo`: default-on-empty, choice by number,
  multi-choice, `HardConfirm` rejects a wrong phrase.
- **`DoctorCommandTests`** — a temp repo with: a good SQLite config → exit 0, all `Ok`; a
  `StateEngine: MsSql` with no provider installed → a `Fail` naming `provider install`; a passkey RP
  id that is a URL → the `PasskeyOptions.Problem()` text; `--json` shape.
- **`SetupCommandTests`** — drive `RunAsync` with a `ScriptedPromptIo`:
  - fresh temp folder, SQLite + passkeys/localhost + no service → asserts `dbdatasync.config.yaml`
    contents, a starter commit, and a printed invite line;
  - the same folder again → detects the existing setup, lands on the review screen, "print effective
    configuration" masks the state password;
  - non-interactive `IPromptIo` → exits 1 with the doctor pointer;
  - `StateEngine` server path → asserts the secret was stored and `ProviderInstaller` was invoked
    (a fake).
- **`BootstrapInviteTests`** — `FIRST-RUN.txt` is written on mint and removed once a user exists.
- Manual, both platforms: a fresh `setup` to a running console + sign-in; `setup` again → review;
  on Windows, the service + self-signed-cert path end to end.

## Open questions

1. **The "already configured" predicate.** A fresh `serve` writes a fully-commented config and one
   starter commit, so "file exists" and "is a git repo" are both true on a never-used root. Candidates:
   an uncommented `DbDataSync:` key, a state DB on disk, or `> 1` commit. Leaning: *any uncommented
   key OR a state DB* — a single method `ExistingSetup.DetectedAt(root)` with its own tests.
2. **How much of `ServiceCommand` / `CertCommand` to extract** vs. `setup` synthesising an argv and
   calling `Run`. Leaning: extract the install/bind *work* into internal methods returning a result;
   leave the argv parsing in the command wrappers. Where extraction is disproportionate (cert
   enroll), synthesise argv and let the command print its own output — acceptable inside setup's flow.
3. **`FIRST-RUN.txt` location** — repo root (committed unless `.gitignore`d) or a sibling. Leaning:
   repo root; `ServeCommand.Prepare` / setup writes a `.gitignore` with `FIRST-RUN.txt` and
   `state.db*` on a fresh repo.
4. **Does `setup` supersede `serve`'s first-run bootstrap?** Leaning: coexist — `serve` stays the
   scripted / "I know what I'm doing" path; `setup` is what getting-started recommends and what the
   review screen needs.
5. **Elevation UX on Windows** — defer service/cert with printed commands (leaning) vs. offer a UAC
   relaunch. A relaunch loses the interactive console state mid-flow.
6. **`doctor` as its own command vs. `serve --check`.** Leaning: own command — CI and a service
   wrapper call it without Kestrel; `setup` calls the check list in-process.
