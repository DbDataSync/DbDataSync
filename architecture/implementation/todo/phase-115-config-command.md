# Phase 115 — a `config` command; fold `check`/`cert`/`secret`/`provider`/`driver` under it (planned)

**Status**: Planned, not started
**Plan reference**: none dedicated — this phase originates from a direct conversation about the CLI's
command surface, not from `architecture/planning/todo/app-and-service-setup.md` (which still describes
the pre-this-phase shape; see *Documentation to update alongside* below).

## Why

Phase 110 built `dbdatasync doctor` as its own top-level verb — a non-interactive readiness check
(repo, state store, providers/drivers, auth, binding, first admin) that `setup`'s review screen also
runs in-process. The checks are wanted; `doctor` as a name and as a *top-level* command is not, and
the CLI's top-level surface has grown large enough (11 verbs today: `serve`, `service`, `cert`,
`invite`, `health`, `secret`, `provider`, `driver`, `doctor`, `setup`, `version`) to be worth trimming
generally, not just fixing one name.

Decision: leave `setup` exactly as it is today — a purely interactive walk-through/review experience,
no `--check` flag. Add a new top-level `config` command and move every configuration-related command
underneath it: `check` (replacing `doctor`), `cert`, `secret`, `provider`, `driver`. `health` is a
candidate for nesting under `serve`/`service` instead, but that is a separate decision, deliberately
not made here. `invite` stays top-level for visibility (it's the thing an admin reaches for most
often after initial setup). `version` is untouched.

Resulting top-level surface: `serve`, `service`, `health`, `invite`, `config`, `setup`, `version` — 7
verbs, with `config` fanning out to five nested groups (two of which, `cert` and `driver`, are
themselves multi-subcommand today and stay that way one level deeper).

## What this builds

### 1. Extract the check engine out of `DoctorCommand.cs` into shared, neutral infrastructure

`src/DbDataSync.Cli/DoctorCommand.cs` currently mixes two things: the check engine (`CheckStatus`,
`CheckResult`, `IReadinessCheck`, `DoctorContext`, the six check classes — `RepoCheck`,
`StateStoreCheck`, `ProvidersAndDriversCheck`, `AuthCheck`, `BindingCheck`, `FirstAdminCheck` — and
`BuildContext`/`RunChecksAsync`/`FormatResult`) and a CLI entry point (`DoctorCommand.RunAsync(string[]
args)` with `--json`). The engine has two callers after this phase (`SetupCommand.ReviewAsync`'s
in-process review screen, and the new `config check`), so it should not be named after either.

- Rename the file → `src/DbDataSync.Cli/ReadinessChecks.cs`. `DoctorCommand` → `ReadinessChecks`;
  `DoctorContext` → `ReadinessContext`. Drop the old `DoctorCommand.RunAsync(string[] args)`/`--json`
  entry point — that logic moves into `ConfigCommand` (below), unchanged in behavior. Everything else
  moves over as-is and becomes `internal` (currently `public` on some types) — nothing outside
  `DbDataSync.Cli` needs any of it, and `DbDataSync.Cli.csproj` already declares
  `[InternalsVisibleTo("DbDataSync.Cli.Tests")]`.
- `src/DbDataSync.Cli/SetupCommand.cs`'s `ReviewAsync` — repoint its three call sites at
  `ReadinessChecks.*`. Its own prompt/comment text that names `` `dbdatasync doctor` `` (the
  state-database step has two mentions) becomes `` `dbdatasync config check` ``.

### 2. New `src/DbDataSync.Cli/ConfigCommand.cs`

```csharp
public static class ConfigCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 1; }

        var rest = args[1..];
        return args[0].ToLowerInvariant() switch
        {
            "check" => await CheckAsync(rest),
            "cert" => CertCommand.Run(rest),
            "secret" => SecretCommand.Run(rest),
            "provider" => await ProviderCommand.RunAsync(rest),
            "driver" => await DriverCommand.RunAsync(rest),
            var other => Unknown(other),
        };
    }

    private static async Task<int> CheckAsync(string[] args)
    {
        var asJson = CliOptions.Has(args, "--json");
        var context = ReadinessChecks.BuildContext(args);
        var results = await ReadinessChecks.RunChecksAsync(context);

        if (asJson)
            Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        else
            foreach (var result in results)
            foreach (var line in ReadinessChecks.FormatResult(result))
                Console.WriteLine(line);

        return results.Any(r => r.Status == CheckStatus.Fail) ? 1 : 0;
    }
    // Unknown(...) / PrintUsage() follow the same shape as CertCommand/ProviderCommand/DriverCommand.
}
```

`CertCommand.Run`/`SecretCommand.Run`/`ProviderCommand.RunAsync`/`DriverCommand.RunAsync` need no
signature or behavior changes — each already takes "everything after my own verb," which is exactly
what `rest` is once `ConfigCommand` strips its own leading verb (`cert`/`secret`/`provider`/`driver`)
the same way `Program.cs` strips `config`. `config check` writes straight to `Console`, matching
every other non-`setup` command — `ConfigCommand`, unlike `SetupCommand`, has no `IPromptIo` seam
since nothing under it is interactive.

### 3. `Program.cs` dispatch

Remove the `"doctor"`, `"cert"`, `"secret"`, `"provider"`, and `"driver"` cases; add
`"config" => await ConfigCommand.RunAsync(rest),`. `dbdatasync cert ...` / `dbdatasync secret ...` /
`dbdatasync provider ...` / `dbdatasync driver ...` / `dbdatasync doctor` all become "Unknown
command" — a deliberate, user-facing compatibility break. Call it out explicitly in the commit
message and the retrospective; anything scripting these directly (CI, docs, an admin's own notes)
needs the `config ` prefix inserted.

### 4. `Help.cs`

Remove the standalone `cert`/`secret`/`provider`/`driver`/`doctor` blocks; replace with one `config`
block listing all five nested groups and their one-line summaries (same summaries as today, `config `
prefixed). Keep `setup`'s block as-is other than any stray `doctor` mention (there should be none —
confirm when implementing).

### 5. Sweep every hardcoded CLI-invocation string

Grepped and enumerated below — this is the part most likely to be under-scoped if rushed, since
several of these live outside `DbDataSync.Cli` entirely.

**Runtime-visible strings inside `DbDataSync.Cli` (must change — users see these):**

- `CertCommand.cs` — `PrintUsage()`'s eight lines; `Status()`'s "Run 'dbdatasync cert
  new-self-signed'/'enroll'/'bind'" message; `Templates()`'s "'dbdatasync cert enroll'" message;
  `Retrieve()`'s and `Bind()`'s usage lines; `PrintIssued()`'s "Run 'dbdatasync cert bind
  --thumbprint...'" message.
- `SecretCommand.cs` — `PrintUsage()`, `Set()`'s and `Remove()`'s usage messages.
- `ProviderCommand.cs` — `PrintUsage()`'s four lines, `InstallAsync`'s and `Uninstall`'s usage
  messages.
- `DriverCommand.cs` — `PrintUsage()`'s four lines, `InstallCompiledAsync`'s/`InstallDescriptorAsync`'s
  usage messages, `Uninstall`'s "`dbdatasync provider uninstall` separately" note.
- `SetupCommand.cs` — the three printed cert commands in the Windows-only certificate step
  (`self-signed`/`enroll`/`bind`), and the "For any other engine, run `dbdatasync driver install
  ...`" line in the drivers step.
- `src/DbDataSync.Core/Config/DbDataSyncConfigFile.cs` — the starter YAML's comment
  (`# dbdatasync secret set dbdatasync:config:stateConnectionString "Password=..."`), written into
  every fresh repo's `dbdatasync.config.yaml` by `WriteStarter`.
- `ReadinessChecks.cs` (post-rename) — `StateStoreCheck`'s and `ProvidersAndDriversCheck`'s fix
  suggestions (`` `dbdatasync secret set ...` ``, `` `dbdatasync provider sync` ``, `` `dbdatasync
  driver install ...` ``).

**Runtime-visible strings *outside* `DbDataSync.Cli` — easy to miss, must also change:**

- `src/DbDataSync.Providers/ProviderRegistry.cs` — `GetFactory`'s thrown message: `"Provider '{id}' is
  not installed. Install it with \`dbdatasync provider install {id}\`."` — this fires from the actual
  replication runtime (API/TaskRunner), not just the CLI. Its test,
  `tests/DbDataSync.Providers.Tests/ProviderLoadTests.cs` (`Assert.Contains("dbdatasync provider
  install", ...)`), needs the matching update.
- `src/DbDataSync.Api/Services/ParameterCheck.cs` — `"Unknown driver '{input.DriverType}'. Install it
  with \`dbdatasync driver install <package>\`."` — same situation. Its test,
  `tests/DbDataSync.Api.Tests/ParameterCheckTests.cs`, needs the matching update.

**Doc-comments only (lower priority, still worth doing in the same pass, purely mechanical, no
behavior):** roughly two dozen `///`/`//` mentions of `dbdatasync cert`/`dbdatasync secret`/
`dbdatasync provider`/`dbdatasync driver` across `DbDataSync.Api/Services/`
(`AdminConfigService.cs`, `AdminCertificateService.cs`, `CertificateExpiryService.cs`),
`DbDataSync.Api/Controllers/` (`AdminCertificateController.cs`, `AdminConfigController.cs`),
`DbDataSync.Certificates/*.cs` (most files in that project), `DbDataSync.Core/Secrets/SecretRefs.cs`,
`DbDataSync.Drivers.Descriptor/TypeMapEntryYamlConverter.cs`, and a handful of test-file doc-comments
(`SecretCommandTests.cs`, `InviteCommandTests.cs`, `CliTestSecretsFile.cs`,
`ProviderCommandTests.cs`, `DescriptorDriverApiFactory.cs`, `CompiledDriverLoaderTests.cs`). Grep for
the four command-name strings and update each hit; none affect behavior.

### 6. Tests

- Rename `tests/DbDataSync.Cli.Tests/DoctorCommandTests.cs` → `ReadinessChecksTests.cs`; update its
  four tests to call `ReadinessChecks.BuildContext`/`RunChecksAsync` (renamed types, same
  assertions — including the one asserting `"dbdatasync driver install"` in a fix message, now
  `"dbdatasync config driver install"`). Its `--json` test currently drives `DoctorCommand.RunAsync`
  directly via a `Console.Out`-redirect helper (`RunDoctor`) — repoint that one test at
  `ConfigCommand.RunAsync(["check", "--repo", root, "--json"])` instead, still via a Console redirect
  (no `IPromptIo` seam here), proving the real CLI path end to end.
- New `tests/DbDataSync.Cli.Tests/ConfigCommandTests.cs` — no-subcommand usage/exit-1; unknown
  subcommand; each of `config cert ...`/`config secret ...`/`config provider ...`/`config driver ...`
  correctly forwards to the existing command classes (thin routing tests — full behavior for each is
  already covered by their own suites: `SecretCommandTests.cs` on this project, and
  `DbDataSync.Providers.Tests`/`DbDataSync.Drivers.*.Tests` elsewhere; `cert` has no existing CLI test
  at all, being Windows-only and untestable in this sandbox, so nothing to move there).
- `tests/DbDataSync.Cli.Tests/SetupCommandTests.cs` — no behavior change expected; re-run after the
  `ReadinessChecks` rename to confirm `ReviewAsync`'s check rendering still works.
- `tests/DbDataSync.Core.Tests/DbDataSyncConfigFileTests.cs` and
  `tests/DbDataSync.Cli.Tests/ServeCommandPrepareTests.cs` — both assert the starter file's exact
  comment text; update both to `dbdatasync config secret set ...`.
- `tests/DbDataSync.Providers.Tests/ProviderLoadTests.cs` and
  `tests/DbDataSync.Api.Tests/ParameterCheckTests.cs` — update the two runtime-message assertions
  named above.

### 7. Documentation to update alongside

- `architecture/planning/todo/app-and-service-setup.md` — still describes `doctor` as its own
  command and predates this restructuring in other ways already (per phase 110's own retrospective).
  Reword its `doctor` mentions to `config check`.
- `architecture/implementation/todo/phase-112-machine-wide-data-directory.md` — mentions `serve`,
  `setup`, `doctor` resolving a shared default location, and references `DoctorCommandTests` in its
  own test plan. Update both to the new names.
- `architecture/planning/todo/linux-tls-without-a-reverse-proxy.md` and
  `architecture/implementation/todo/phase-113-tls-bring-your-own-certificate.md` /
  `phase-114-tls-acme.md` — all three describe and extend `dbdatasync cert ...` directly (114 in
  particular plans a `doctor` ACME check). **Sequencing note**: these three are already
  fully-specified, not-yet-implemented plans written against the current flat `cert`/`doctor`
  surface. Whichever of {phase 115, phases 113/114} implements first should update the *other's*
  doc to match rather than silently drift — flagging here so neither implementer is surprised.
- Do **not** edit `architecture/implementation/done/phase-110-setup-command.md` — historical record;
  this phase's own retrospective records the supersession instead.

## What this phase does not build

- Any change to `setup`'s own interactive behavior — untouched.
- Nesting `health` under `serve`/`service` — a separate decision, not made here.
- Changing `version` — untouched.
- Anything in phases 111/112/113/114 (systemd service, machine-wide data directory, TLS-from-a-file,
  TLS-ACME) beyond the doc-consistency updates named above.

## How to verify when built

- `dotnet build DbDataSync.slnx` clean.
- Full `dotnet test --filter "Category!=Integration"` green solution-wide (this phase's changes reach
  `DbDataSync.Cli`, `DbDataSync.Providers`, and `DbDataSync.Api`).
- `dbdatasync config check` (and `--json`) against a real temp repo produces identical output/exit
  codes to what `dbdatasync doctor` used to.
- `dbdatasync config cert ...` / `config secret ...` / `config provider ...` / `config driver ...`
  behave identically to the old top-level commands — same flags, same output, one level deeper.
- `dbdatasync cert`, `dbdatasync secret`, `dbdatasync provider`, `dbdatasync driver`, and `dbdatasync
  doctor` all now print "Unknown command."
- `dbdatasync setup` end to end (interactive walk-through + review screen) still works unchanged.
