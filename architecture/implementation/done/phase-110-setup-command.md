# Phase 110 — the interactive `dbdatasync setup` command

**Status**: Done.
**Plan reference**: `architecture/planning/todo/app-and-service-setup.md`. Independent of phase 109;
109c (`ProviderCommand`/`ProviderInstaller`) and 109d (`DriverCommand`/`DriverTemplates`) were reused
as-is by the drivers step, and 109f's `StateEngineIds`/`StateDatabase.FromOptions` by the state
database step.

## What this built

Three things in `src/DbDataSync.Cli`, plus a small addition to `BootstrapInvite`:

- **`Prompt.cs`** — `IPromptIo` (the seam over `Console`), `ConsolePromptIo` (the real
  implementation), and `Prompt` itself: `Text`/`YesNo`/`Choice<T>`/`MultiChoice<T>`/`Secret`/
  `HardConfirm`, exactly the shapes the plan doc sketched.
- **`DoctorCommand.cs`** — `dbdatasync doctor`: builds the same `IConfiguration` chain the running
  host builds (config file → environment → `--DbDataSync:*`), without `DbDataSync.Api`/Kestrel, and
  runs six checks (`RepoCheck`, `StateStoreCheck`, `ProvidersAndDriversCheck`, `AuthCheck`,
  `BindingCheck`, `FirstAdminCheck`). `--json` for a machine reader; exit 1 if anything is `Fail`.
- **`SetupCommand.cs`** — `dbdatasync setup`: a walk-through for a fresh root, or a review screen for
  an existing one, over the same `Prompt`.
- **`ExistingSetup.cs`** — the one predicate both of the above need: is there already a real
  configuration at this root.
- **`BootstrapInvite.cs`** — now also writes `<RepoRoot>/FIRST-RUN.txt` alongside the console log line,
  and removes it once a user exists — the one way in for a process with no console at all (a Windows
  service).

### The walk-through

Eight steps, matching the plan doc's outline: root (`ServeCommand.Prepare`, already idempotent and
shared) → console URL → state database (SQLite, or a server engine with `SetValue` +
`SecretStore.Store` for the password, plus a real connect attempt reported rather than trusted) →
additional drivers (`MultiChoice` over the built-ins plus MySQL/MariaDB and "other") → authentication
(passkeys, Windows groups, or none, guarded by `HardConfirm`) → Windows-only service/certificate
(printed commands, not orchestrated — see Decisions) → a commit → an offer to start.

### The review screen

Runs `DoctorCommand`'s exact check list in-process (`RunChecksAsync`/`FormatResult`, `internal` so
there is only one copy) and offers **print effective configuration** (every `DbDataSync:*` key the
merged configuration resolves to, redacting anything that looks like an embedded credential),
**reissue the first-run invite** (only when the first-admin check is not `Ok`; calls
`InviteCommand.Run`), **start DbDataSync**, or **exit**.

## What this phase does not build — deviations from the plan doc

The plan doc's own "how to verify" section sketched a **Reconfigure** menu (state database ·
authentication · drivers · service · certificate, each re-entering that one walk-through step in
isolation) as part of the review screen. This was not built. Every walk-through step already writes
through the same `DbDataSyncConfigFile.SetValue`/`SetListValue` calls an operator can run directly, and
`dbdatasync driver install`/`dbdatasync secret set` already exist as standalone commands for the
pieces that change most often — so the review screen's real gap was **visibility** (what does the
current config actually say and is it healthy), which "print effective configuration" plus `doctor`
answers, not a second guided-editing surface duplicating the walk-through's own steps. Building
`Reconfigure` well means each step accepting and displaying a pre-filled current value rather than
always starting from a hardcoded default — meaningfully more work than the rest of the review screen
combined, for a case (re-editing one setting) an operator can already reach two other ways. Left as a
follow-up if real usage shows the gap matters; noting it here rather than silently dropping it.

Two more deliberate reductions, both already flagged as open questions in the plan doc:

- **The state database step never calls `ProviderInstaller`.** Before phase 109g removes
  `Microsoft.Data.SqlClient`/`Npgsql` as hard references from `DbDataSync.State.csproj`, there is
  nothing for that call to restore — both packages are already on the build. The plan doc's own
  `YesNo("Install the provider now?")` prompt is skipped entirely rather than asked and then done
  nothing with it silently.
- **Windows-only service and certificate steps only print the command to run**, never invoke
  `ServiceCommand`/`CertCommand` themselves. Both need real elevation and real Windows APIs
  (`sc.exe`, the certificate store) this Linux sandbox cannot exercise or verify at all — `setup`
  printing a command it cannot itself test is honest about what it's actually done; `setup` silently
  calling into code with zero coverage here would not be.

Neither reduction removes anything `doctor` would otherwise catch: a state connection that doesn't
open, or a service that was never registered, both show up as a `Fail`/pointer the next time `doctor`
runs.

## Decisions

- **A seam for provider installation, not for `ServeCommand`/`InviteCommand`/`DoctorCommand`
  themselves.** `SetupCommand.RunAsync` takes an `installProvider` delegate (defaulting to
  `ProviderInstaller.InstallAsync`) purely so `SetupCommandTests`' MySQL-driver-step test doesn't shell
  out to `dotnet publish`. Every other call `SetupCommand` makes — `ServeCommand.Prepare`,
  `StateDatabase.FromOptions`, `DoctorCommand.BuildContext`/`RunChecksAsync`, `InviteCommand.Run` — is
  real, because each of those is either already fast and local (`Prepare`, `BuildContext`) or is itself
  exercised end-to-end by its own test suite (`InviteCommand`, `DoctorCommand`), so faking it here
  would only be testing that a fake was called correctly.
- **`Prompt`'s required-input loop had a real infinite-loop bug, found and fixed before writing any
  test against it.** `Text`/`YesNo`/`Choice`/`MultiChoice` all used to treat a `null` from
  `IPromptIo.ReadLine()` (genuine end of input — a closed stdin, or a scripted test's answer list
  running out) identically to an empty string (a bare Enter press), via `string.IsNullOrEmpty`. For any
  *required* prompt with no default, that meant: exhausted input → `ReadLine()` returns `null` forever
  → the loop reprints "a value is required" and asks again, forever. Fixed by a private `RequireLine`
  helper that throws `InvalidOperationException` specifically on `null`, called from every loop except
  `HardConfirm` (which already treats `null` as "typed nothing, therefore cancel" — a real answer, not
  an absence of one). `PromptTests.Text_NoDefault_ExhaustedInput_ThrowsRatherThanLoopingForever` pins
  this.
- **`DoctorContext.TryOpenStateDatabase`'s (and `SetupCommand`'s own) catch clause was too narrow for
  a real server engine.** Both originally caught only `InvalidOperationException`/`IOException`/
  `UnauthorizedAccessException` — which covers `StateDatabase.FromOptions`'s own thrown
  `InvalidOperationException` (missing connection string) but not a real ADO.NET connection failure
  (`Microsoft.Data.SqlClient.SqlException`, `Npgsql.NpgsqlException`), both of which derive from
  `System.Data.Common.DbException` and were previously left to propagate uncaught out of `doctor` and
  `setup` alike — a check meant to report "can't connect" would instead have crashed the whole command.
  Found by `SetupCommandTests`' server-engine test hitting exactly this against a real (deliberately
  unreachable) endpoint; fixed by adding `DbException` to both catch clauses.
- **A real control-flow bug in `SetupCommand.RunAsync`**: a fresh walk-through fell through into the
  review screen's `Prompt.Choice("What next?", ...)` immediately afterward, with no script left to
  answer it — caught by the same first test run, before either was committed. `WalkThroughAsync` now
  returns its own exit code directly rather than falling through.
- **`FIRST-RUN.txt` is written and removed in two places**, not one: `BootstrapInvite.StartAsync`
  (the only place that runs on every process start, service included) and `InvitesController
  .CompleteRegistration` (so a long-running host doesn't wait for its *next* restart to notice the
  first admin signed up and remove a file that is now a stale, unredeemable invite sitting on disk).
  Both delete failures are best-effort (`IOException`/`UnauthorizedAccessException` caught and
  logged/ignored) — a permissions problem here should never fail a startup or a registration that has
  already fully succeeded.
- **`setup`'s own finish step does not mint a bootstrap invite itself.** An earlier draft had it open
  the state database directly and mint one the way `InviteCommand` does, printing the URL immediately.
  Dropped: `BootstrapInvite.StartAsync` already mints a fresh one (deliberately replacing, not reusing,
  any existing one) the moment the host actually starts, so a `setup`-minted invite would either be
  thrown away unused (if the operator starts DbDataSync afterward, the common case) or be the only
  copy (if they don't) with no way for `BootstrapInvite` to know about it. `setup` instead names where
  the real one will land (`FIRST-RUN.txt`) and, when the operator says yes to "Start DbDataSync now?",
  runs `ServeCommand.RunAsync` in-process — the same path that mints it for real.

## How it was verified

- `dotnet build DbDataSync.slnx` clean; `DbDataSync.Cli.csproj` still has zero `PackageReference`s.
- Full `Category!=Integration` suite green across the whole solution (Cli.Tests: 40, up from the
  pre-phase baseline of 0 for this area; Api.Tests: 398 passed, 23 skipped Windows-only).
- Full `Category=Integration` suite green for the projects this phase's changes reach:
  `DbDataSync.State.Tests` (45), `DbDataSync.Cli.Tests` (1 — the existing `InviteCommand` MsSql test),
  `DbDataSync.Api.Tests` (43), all against the real SQL Server/Postgres/MySQL Docker containers already
  running in this environment.
- **`PromptTests`** (16 tests) — every helper against a `ScriptedPromptIo`: default-on-blank,
  choice/multi-choice by number, `HardConfirm` returns whatever was typed (not the expected phrase) on
  a mismatch, `Secret` reads a real char-at-a-time stream including backspace, and the exhausted-input
  case that used to loop forever now throws.
- **`DoctorCommandTests`** (4 tests) — a fresh SQLite-configured repo passes Repo/State
  store/Providers/Auth; a connection naming an uninstalled driver fails `ProvidersAndDriversCheck` with
  the exact `dbdatasync driver install` pointer text; a passkey relying-party id that is a URL fails
  `AuthCheck` with `PasskeyOptions.Problem()`'s own message; `--json` output parses and names every
  check. `BindingCheck` is deliberately excluded from every assertion — nothing in this suite runs a
  real host, so it always reports "did not answer," and asserting around that would only be asserting
  that nothing is listening on port 5080.
- **`SetupCommandTests`** (4 tests) — a fresh temp root through SQLite + passkeys/localhost + no
  drivers writes the expected config keys, commits (`dbdatasync setup` alongside the starter commit),
  and prints completion text without starting a real server; running `setup` again on that same root
  goes straight to the review screen and "print effective configuration" shows the URL (and never a
  literal `Password=`); a non-interactive `IPromptIo` exits 1 naming `doctor`; a SQL Server state-engine
  walkthrough stores the password in the secret store, writes the connection string (credential-free)
  to the config file, and drives the MySQL driver step through the faked `installProvider` — asserting
  the fake's own call, not a real `dotnet publish`.
- **`BootstrapInviteTests`** (2 new tests, 7 total) — `FIRST-RUN.txt` is written with the invite code on
  mint, and removed once a user exists.
- Not done: a manual end-to-end run against a real console (this phase's author has no way to drive
  an interactive terminal in this sandbox) and the Windows-only service/certificate paths, which no
  environment reachable here can execute at all — both are exactly the reductions called out above.

## Open questions from the plan doc — resolved

1. **The "already configured" predicate** — implemented as leaned: `ExistingSetup.DetectedAt` is any
   uncommented `DbDataSync:*` key, or a `state.db` on disk.
2. **How much of `ServiceCommand`/`CertCommand` to extract** — resolved by not calling into either at
   all; see the printed-command reduction above.
3. **`FIRST-RUN.txt` location** — repo root, as leaned. No `.gitignore` entry was added for it in this
   phase — `ServeCommand.Prepare`'s starter commit doesn't currently write a `.gitignore` at all, and
   adding one is a small enough independent change that bundling it here felt like scope creep; a
   `state.db*`/`FIRST-RUN.txt` `.gitignore` is worth a one-line follow-up whenever `Prepare` next
   changes for another reason.
4. **Does `setup` supersede `serve`'s first-run bootstrap?** — coexist, as leaned; `serve` still does
   exactly what it always did, `setup` is the guided path in front of it.
5. **Elevation UX on Windows** — printed commands, as leaned.
6. **`doctor` as its own command** — yes, as leaned; `setup`'s review screen calls its check list
   in-process rather than shelling back out to itself.
