# CLI and `setup` parity

## The principle (settled 2026-09-16)

Every CLI flag should eventually exist inside `dbdatasync setup` too, and vice versa. The CLI is for
one-off or scripted tasks; `setup` is for interactive versions of those same tasks — two front ends over
the same underlying operations, never two places that can drift apart.

## This is not a new intent — it's an existing one the code has already drifted from

Two docs already say exactly this, and neither is new:

- `SetupCommand.cs`'s own doc comment: "Everything it writes goes through the same
  `DbDataSyncConfigFile` and `SecretStore` calls a scripted deployment already uses; this exists to save
  a first-time operator from having to know `docs/configuration.md` by heart, **not to add a
  configuration path nothing else uses**."
- `architecture/planning/todo/app-and-service-setup.md` (still unagreed, predates the current Terminal.Gui
  TUI): "It writes nothing you cannot also write by hand; it is a guided front over the config file and
  the existing subcommands... setup is a guided front over them, never a second source of truth."

Audited directly against the real code (not estimated) below: **that promise is currently false in at
least four concrete places.** This doc isn't proposing a new principle — it's naming the gap between
what the project already committed to and what's actually built, and proposing how to close it.

## The gap, audited directly

### Setup-only — reachable only through the interactive TUI, or by hand-editing YAML

| Capability | Why |
| --- | --- |
| Persisting `DbDataSync:Url` into `dbdatasync.config.yaml` | Every CLI command's `--url` (`serve`, `service install`, `health`, `invite`) is a runtime override only — none of them writes it back to the config file. |
| Setting `DbDataSync:StateEngine` / `DbDataSync:StateConnectionString` | Confirmed absent by `InviteCommand.cs`'s own comment: "there is no dedicated `--state-engine`/`--state-connection-string` flag; this command's own surface is deliberately small." |
| The entire authentication surface — passkey relying-party id/origins, the Windows admin/viewer group names, disabling auth entirely | There is no `config auth` subcommand anywhere in `Program.cs`'s dispatch tree. Zero CLI reach, not a partial gap. |
| "Print effective configuration" (merged file + env + command line, secrets redacted) | No `dbdatasync config print`/`dump`/equivalent exists. `config check --json` reports check *results*, not raw configuration. |

### CLI-only — reachable only by hand-typing the command

Some of this is correctly CLI-only (`tool install`/`uninstall` has to work before an interactive tool
can even launch; `internal build-catalog-cache` is deliberately build-only). The rest is a real gap:
`service uninstall`/`status`, `health`, `invite --role Viewer` (setup's "Reissue invite" button hardcodes
`Admin`), `config check --json`, most of `config cert` (`list`, `renew`, `retrieve`, `status --account`,
`bind --location`/`--allow-invalid`, `new-self-signed --days`, `enroll --ca`), `config secret` for any
ref beyond the one hardcoded state-connection-string field, and the general form of `config
library`/`config driver` (setup only special-cases two specific installs — see below).

### Same operation, two separate implementations — a real, already-manifesting cost

Not a gap in either surface, but the exact risk a shared-definition design would remove, found already
happening:

- **MySQL driver install**: `SetupSteps.InstallMySqlDriverAsync` re-implements most of what
  `DriverCommand.InstallDescriptorAsync` already does (install the library, render the `mysql.generic`
  template, write `driver.yaml`) as its own separate code path, rather than calling into `DriverCommand`.
- **SQL Server/PostgreSQL library auto-install** in `StateDatabaseTab`/`SetupSteps.ApplyStateDatabaseAsync`
  similarly calls `LibraryInstaller.InstallAsync` directly rather than through `LibraryCommand`.

If either command's own install logic changes, these have to be updated by hand in two places — the kind
of drift this whole principle exists to prevent, and it's already present, not hypothetical.

## Why the gap exists

Not a moral failing — the two surfaces genuinely have no shared foundation to drift apart *from* yet.
Confirmed directly: flag parsing is centralized (`CliOptions.Read`/`Has` is the *only* flag-parsing code
in the whole CLI project — a real, if small, existing foundation). `SetupSteps.cs` already separates
*effects* (write config, install a library, apply auth) from *prompting* — a genuinely good, deliberate
seam, its own doc comment calling it "the UI-free half of every setup section." But that seam takes
typed parameters (`ApplyAuthentication(string root, string choice, string? relyingPartyId, ...)`), not
`string[] args` — there is no analogous "UI-free half" on the flag-parsing side that a TUI screen and a
CLI command could both consult. The two call trees — `Program.cs → XyzCommand.Run(string[] args) →
CliOptions.Read` and `SetupScreen → XyzTab (widget state) → SetupSteps.ApplyXyz(typed values)` — are
independently hand-written and only converge at the very bottom, on the same
`DbDataSyncConfigFile.SetValue`/`SecretStore.Store`/`GitCommitService.CommitChanges` calls.

## Design direction (leaning, staged — not agreed)

A single "one definition generates both surfaces" framework is a real, nontrivial new abstraction layer
— not a free refactor of what exists — and building it first, before closing the concrete gap above,
would delay the actual value for a speculative one. Leaning toward closing the gap with today's
architecture first, and treating a shared-definition framework as a separate, later decision:

1. **Close the setup-only gap** by adding the missing CLI surface: a `config auth` subcommand family
   (the biggest single item — an entire configuration domain with zero CLI reach today), a way to set
   `StateEngine`/`StateConnectionString` from the command line (new flags on an existing command, or a
   small new one), and a `config print`/`config effective`-shaped command for what "Print config" already
   does in the TUI.
2. **Close the CLI-only gap** by widening existing setup tabs to their command's full surface rather than
   today's narrow special-cases: the Drivers tab handling any driver (not just MySQL), the Certificate
   tab covering `renew`/`retrieve`/`list`/the currently-hardcoded `--template`, a way to manage arbitrary
   secrets (not just the one state-connection-string ref), and an invite role picker.
3. **De-duplicate the two already-existing double-implementations** (MySQL driver install, MsSql/Postgres
   library auto-install) by having `SetupSteps` call into `DriverCommand`/`LibraryCommand`'s own logic (or
   a shared inner method both call through) instead of maintaining a second copy — natural to do
   alongside stage 1/2's own work on those same code paths.
4. **A genuinely shared step/flag definition** — generating or validating both a CLI flag set and a TUI
   screen from one source, so future drift becomes structurally impossible rather than something to
   remember — stays a real, aspirational future direction, explicitly *not* committed to here. The
   existing `CliOptions`/`SetupSteps` seams make it plausible eventually, not free.

## What this does not do

- **Does not mean literally every flag needs a matching interactive widget.** A purely scripting-shaped
  flag — `config check --json`, say — doesn't have a meaningful "interactive version"; a human already
  sees the readiness sidebar's own rendering of the identical check engine. The principle is "every
  underlying *operation* reachable both ways," not "every flag becomes a checkbox" — see the open
  question below, since this reading needs confirming, not assuming.
- **Does not commit to building the shared-definition framework** (design-direction item 4) as part of
  this plan — named as a real future direction, not scoped or scheduled here.
- **Does not touch `config check`/the readiness engine** — already correctly shared between both surfaces
  today, the one part of this system that already lives up to the principle.

## Open questions (UNDECIDED)

- **Does "every flag" mean every flag, or every operation?** Leaning: every operation, with pure
  output-format/scripting flags (`--json`, and similarly-shaped future ones) as a deliberate, named
  exception rather than a gap — but this changes scope enough to want it confirmed explicitly rather than
  assumed.
- **Scope and priority of the `config auth` subcommand family** — the single largest concrete gap found
  (an entire configuration domain, zero existing CLI surface), and probably deserves its own more detailed
  design pass before being folded into a general "close the gap" implementation phase.
- **Invite role picker in setup** — small, but real; worth confirming it's wanted rather than assuming
  setup's Admin-only default is deliberate (a first admin is arguably always the point of running setup
  at all, which would make this lower priority, not skipped).
- **Whether design-direction item 4 (a shared step/flag definition) is ever worth building**, versus
  relying on stages 1–3 plus a documented review habit ("does this new CLI feature also need a setup
  path, or vice versa?") on every future change. Not resolved here, and not blocking the nearer-term work.
