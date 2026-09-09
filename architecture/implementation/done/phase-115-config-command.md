# Phase 115 — a `config` command; fold `check`/`cert`/`secret`/`provider`/`driver` under it

**Status**: Done.
**Plan reference**: none dedicated — originated from a direct conversation about the CLI's command
surface, not from `architecture/planning/todo/app-and-service-setup.md` (which described the
pre-this-phase shape; updated alongside this phase — see below).

## What this built

Top-level CLI surface went from 11 verbs to 7: `serve`, `service`, `health`, `invite`, `config`,
`setup`, `version`. `config` fans out to five nested groups: `check` (new — `doctor`'s old
non-interactive entry point), `cert`, `secret`, `provider`, `driver` (the latter four: same
implementations, reached one level down). `setup` is untouched — still purely interactive, no
`--check` flag; its review screen calls the same check engine in-process, same as before.

### The check engine, pulled out of `doctor` into neutral infrastructure

`src/DbDataSync.Cli/DoctorCommand.cs` → `src/DbDataSync.Cli/ReadinessChecks.cs`. `DoctorCommand` →
`ReadinessChecks`; `DoctorContext` → `ReadinessContext`. The old `DoctorCommand.RunAsync(string[]
args)`/`--json` CLI entry point is gone from this file — that logic now lives in
`ConfigCommand.CheckAsync`. Everything else (`CheckStatus`, `CheckResult`, `IReadinessCheck`, the six
check classes, `Checks`, `RunChecksAsync`, `FormatResult`, `BuildContext`) moved over unchanged in
behavior and became `internal` (was `public`) — nothing outside `DbDataSync.Cli` needs any of it, and
the test project already has `[InternalsVisibleTo("DbDataSync.Cli.Tests")]`.

### `src/DbDataSync.Cli/ConfigCommand.cs` (new)

```csharp
public static class ConfigCommand
{
    public static async Task<int> RunAsync(string[] args) =>
        args.Length == 0 ? Usage() : args[0].ToLowerInvariant() switch
        {
            "check" => await CheckAsync(rest),
            "cert" => CertCommand.Run(rest),
            "secret" => SecretCommand.Run(rest),
            "provider" => await ProviderCommand.RunAsync(rest),
            "driver" => await DriverCommand.RunAsync(rest),
            var other => Unknown(other),
        };
}
```

`CertCommand.Run`/`SecretCommand.Run`/`ProviderCommand.RunAsync`/`DriverCommand.RunAsync` needed no
changes to their own logic — each already took "everything after my own verb," which is exactly what
`ConfigCommand` hands them after stripping its own leading verb the same way `Program.cs` used to
strip `cert`/`secret`/`provider`/`driver` directly. `config check` writes straight to `Console`,
matching every other non-`setup` command — no `IPromptIo` seam, since nothing here is interactive.

### The sweep

Every hardcoded `dbdatasync cert/secret/provider/driver` string got a `config ` inserted, across:

- `CertCommand.cs`, `SecretCommand.cs`, `ProviderCommand.cs`, `DriverCommand.cs` — usage text and
  in-command messages.
- `SetupCommand.cs` — the printed cert commands in the Windows-only certificate step, the "install
  this driver manually" pointer, and the non-interactive refusal message (now points at
  `dbdatasync config check`, not the old `dbdatasync doctor`).
- `DbDataSyncConfigFile.cs`'s starter YAML comment, written into every fresh repo.
- `ReadinessChecks.cs`'s own fix suggestions (`StateStoreCheck`, `ProvidersAndDriversCheck`).
- Two runtime messages **outside** `DbDataSync.Cli` entirely, found only by grepping rather than
  assuming the blast radius stopped at the CLI project: `DbDataSync.Providers/ProviderRegistry.cs`'s
  `GetFactory` (fires from the real replication runtime) and `DbDataSync.Api/Services/
  ParameterCheck.cs`'s unknown-driver message. Both had a test asserting the exact old string
  (`ProviderLoadTests.cs`, `ParameterCheckTests.cs`) — both updated to match.
- ~25 doc-comment-only (`///`/`//`) mentions across `DbDataSync.Api`, `DbDataSync.Certificates`,
  `DbDataSync.Core`, `DbDataSync.Drivers.Descriptor`, and several test files — including three that
  turned out to be real runtime strings on closer inspection, not just comments:
  `CertificateExpiryService.cs` (two log/notification messages) and `AdcsEnrollment.cs` (one thrown
  message) all named the bare `dbdatasync cert ...` shape and got the same fix.

### Tests

- `DoctorCommandTests.cs` → `ReadinessChecksTests.cs`, updated to call `ReadinessChecks.*`. Its
  `--json` test now drives `ConfigCommand.RunAsync(["check", "--repo", root, "--json"])` instead of
  calling the (now-gone) `DoctorCommand.RunAsync` directly — proving the real CLI dispatch path, not
  just the check engine.
- New `ConfigCommandTests.cs` — no-subcommand usage/exit-1, unknown subcommand, and one routing test
  per nested group (`check`, `secret`, `provider`, `driver`, `cert`) proving each reaches its existing
  command class.
- `SetupCommandTests.cs` — the non-interactive refusal test renamed and its assertion updated to
  `dbdatasync config check`.
- `ServeCommandPrepareTests.cs` / `DbDataSyncConfigFileTests.cs` — updated the starter-file text
  assertion.
- `ProviderLoadTests.cs` / `ParameterCheckTests.cs` — updated the two out-of-CLI-project runtime
  message assertions.

## Decisions and things found along the way

- **A pre-existing test-fixture race, surfaced by adding a third Console-redirecting test class.**
  `SecretCommandTests` and the renamed `ReadinessChecksTests` (né `DoctorCommandTests`) both already
  redirected the process-wide `Console.Out`/`Console.Error` to capture CLI output, and
  `SecretCommandTests` also writes through a single shared, file-backed secret store
  (`CliTestSecretsFile`). xUnit runs test classes in different collections by default, so these two
  ran in parallel — apparently without incident before this phase, since it was only ever two classes
  doing this. Adding `ConfigCommandTests` as a third pushed the odds over the edge: a run failed with
  a `FileNotFoundException` from `FileSecretProvider`'s temp-file swap (two writers racing) and
  garbled/empty captured output in two unrelated tests (two readers of a `Console.Out` that a third
  thread had just reassigned). Fixed the same way three other test projects in this repo already
  solved the identical class of problem (real-database-locking races in `DbDataSync.Api.Tests`,
  `DbDataSync.TaskRunner.Tests`, `DbDataSync.Drivers.{MsSql,Postgres}.Tests`): a new
  `tests/DbDataSync.Cli.Tests/AssemblyInfo.cs` with `[assembly: CollectionBehavior
  (DisableTestParallelization = true)]`. This is a pre-existing fragility this phase's own new test
  happened to be the one to trip, not something phase 115 introduced — worth fixing here rather than
  leaving `ConfigCommandTests` flaky and blaming it as "a bad test."
- **`ReadinessChecks`'s types are now `internal`, not `public`.** They were `public` on
  `DoctorCommand` because that class was itself the CLI's public entry point. Once the check engine
  has no public entry point of its own — only `ConfigCommand` and `SetupCommand`, both in the same
  assembly, ever touch it — there's no reason for `CheckResult`/`CheckStatus`/the check classes to be
  visible outside `DbDataSync.Cli`. Tightened rather than left `public` out of inertia.
- **`config`'s own usage text lists all five nested groups**, matching the existing pattern
  `cert`/`provider`/`driver` already used for their own no-args usage screens — `dbdatasync config
  <name>` with nothing further still shows that group's own detailed flags via its own existing
  `PrintUsage`, unchanged.

## Compatibility breaks (deliberate, called out per the plan)

`dbdatasync cert ...`, `dbdatasync secret ...`, `dbdatasync provider ...`, `dbdatasync driver ...`,
and `dbdatasync doctor` all now print "Unknown command" — anything scripting these directly (CI, an
admin's own notes, `docs/`) needs the `config ` prefix inserted. No deprecation shim or hidden alias
was added; the plan didn't ask for one, and this is CLI-only, in-repo tooling with no external
consumer contract to preserve.

## What this phase does not build

- Any change to `setup`'s own interactive behavior.
- Nesting `health` under `serve`/`service` — a separate decision, not made here.
- Changing `version`.
- The `docs/getting-started.md` rewrite the still-`todo` `app-and-service-setup.md` describes — a
  separate, smaller task once the surface it describes is what actually ships (which, as of this
  phase, it now is).

## How it was verified

- `dotnet build DbDataSync.slnx` clean.
- Full `dotnet test --filter "Category!=Integration"` green across the whole solution (Cli.Tests: 47,
  up from 40 before this phase; every other project unaffected).
- Full `dotnet test --filter "Category=Integration"` green across the whole solution, against the
  real SQL Server/Postgres/MySQL Docker containers already running in this environment.
- Confirmed `dbdatasync config check` (and `--json`) against a real temp repo produces identical
  output/exit codes to what `dbdatasync doctor` produced before this phase — proven directly by
  `ReadinessChecksTests`, which exercises both the engine and, in its one end-to-end test, the real
  `ConfigCommand` dispatch.
- Confirmed `dbdatasync cert`, `dbdatasync secret`, `dbdatasync provider`, `dbdatasync driver`, and
  `dbdatasync doctor` all now print "Unknown command."
