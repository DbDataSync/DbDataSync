# CLI, `setup`, and API parity

## The principle (settled 2026-09-16, widened 2026-09-16)

Every CLI flag should eventually exist inside `dbdatasync setup` too, and vice versa — the CLI is for
one-off or scripted tasks, `setup` is for interactive versions of those same tasks. Widened the same day
to a third surface: as many of the same underlying code paths as possible should also be shared with the
Admin web UI's own API services, to reduce the chance of diverging implementations and behaviors. Three
front ends — a terminal command, a TUI, an HTTP API behind a web console — over the same underlying
operations, never three places that can independently drift.

## This is not a new intent — it's an existing one the code has already drifted from

Two docs already say this, for the CLI/setup pair, and neither is new:

- `SetupCommand.cs`'s own doc comment: "this exists to save a first-time operator from having to know
  `docs/configuration.md` by heart, **not to add a configuration path nothing else uses**."
- `architecture/planning/todo/app-and-service-setup.md` (still unagreed): "setup is a guided front over
  them, never a second source of truth."

Audited directly against the real code below: that promise is currently false in several concrete
places, across all three surfaces, not just the CLI/setup pair.

## The gap, audited directly

### Setup-only — reachable only through the interactive TUI, or by hand-editing YAML

| Capability | Why |
| --- | --- |
| ~~Persisting `DbDataSync:Url` into `dbdatasync.config.yaml`~~ **Partially closed, phase 164** | `service install --url` now seeds `App:Url` into the file once (if unset) instead of only baking it into the service unit — see phase 164's own doc. `serve`/`health`/`invite` still treat `--url` as a runtime override only. |
| ~~Setting `DbDataSync:StateEngine` / `DbDataSync:StateConnectionString`~~ **Closed, phase 164** | `config set State:Engine`/`config set State:ConnectionString` now work — phase 164 confirmed `AdminConfigService`'s catalog (which `ConfigValueCommand` already read from) can address any key depth, not just the flat top level this doc assumed. |
| The entire authentication surface — passkey relying-party id/mode, the Windows admin/viewer group names/mode, network-trust fallback | **Mostly closed, phase 164**: `config set Auth:Windows:AdminGroup`/`Auth:Windows:Mode`/`Auth:Passkeys:RelyingPartyId`/`Auth:Passkeys:Mode`/`Auth:Network:Admin`/`Auth:Network:Viewer` all work now — no dedicated `config auth` subcommand exists, but the general `config set`/`get` reaches the whole surface. What's still missing: an invite role picker equivalent and a guided walkthrough, which is what a real `config auth` subcommand would add beyond raw key/value access. |
| "Print effective configuration" (merged file + env + command line, secrets redacted) | No `dbdatasync config print`/`dump`/equivalent exists. `config check --json` reports check *results*, not raw configuration. |

### CLI-only — reachable only by hand-typing the command

Some of this is correctly CLI-only (`tool install`/`uninstall` has to work before an interactive tool
can even launch; `internal build-catalog-cache` is deliberately build-only). The rest is a real gap:
`service uninstall`/`status`, `health`, `invite --role Viewer` (setup's "Reissue invite" button hardcodes
`Admin`), `config check --json`, most of `config cert` (`list`, `renew`, `retrieve`, `status --account`,
`bind --location`/`--allow-invalid`, `new-self-signed --days`, `enroll --ca`), `config secret` for any
ref beyond the one hardcoded state-connection-string field, and the general form of `config
library`/`config driver` (setup only special-cases two specific installs — see below).

### The same underlying operation, independently re-orchestrated up to three times

This is the sharper finding, and the reason the principle widened to include the API rather than staying
a two-surface concern. It is **not** that the API duplicates low-level logic the CLI already has —
confirmed directly, the opposite is true at that layer: `DriversController`'s own `POST
/api/drivers/from-catalog` endpoint calls exactly the same primitives `DriverCommand`'s CLI path does —
`LibraryInstaller.InstallOrDeferAsync`, `KnownDrivers.TryGetById`/`Render`, `DriverDescriptorReader.Read`/`ToSpec` —
all of which already live in surface-agnostic projects (`DbDataSync.Libraries`, the descriptor-driver
project), not CLI-specific code. **The primitives are already well shared.**

What's duplicated is the layer just above them — the specific *sequence* in which one user-facing
operation ("install this driver, from this catalog entry, installing its library if needed") calls those
primitives. Three separate, independently-written orchestrations of that same sequence exist today:

1. `DriverCommand.InstallDescriptorAsync` (CLI)
2. `DriversController`'s own inline sequence behind `from-catalog` (API)
3. `SetupSteps.InstallMySqlDriverAsync` (setup TUI) — narrower (hardcoded to one driver), but the same
   shape, confirmed calling `LibraryInstaller.InstallAsync` directly rather than through either of the
   other two.

The state-database/MsSql-Postgres library auto-install (`StateDatabaseTab`/`SetupSteps.ApplyStateDatabaseAsync`,
also calling `LibraryInstaller.InstallAsync` directly) is the same pattern again. If any one of these
three re-derivations of "how to install a descriptor driver" changes, the other two don't — exactly the
drift this principle exists to prevent, already present in the one place checked closely enough to
confirm it. **Not asserted as exhaustive** — this is one concrete, verified example, not a full audit of
every API controller against every CLI command; that audit is real, remaining work before an
implementation phase, not assumed complete here.

## Why the gap exists

Not a moral failing — none of the three surfaces has ever had a shared *operation* layer to converge on,
only a shared *primitives* layer. Confirmed directly: CLI flag parsing is centralized (`CliOptions.Read`/`Has`
is the only flag-parsing code in the CLI project). `SetupSteps.cs` already separates effects from
prompting — a genuinely good seam, its own doc comment calling it "the UI-free half of every setup
section" — but its methods take typed parameters, not `string[] args`, and there's no equivalent seam on
the API side either: a controller action takes an ASP.NET request/response shape, not a plain, reusable
method signature either. Three call trees — `Program.cs → XyzCommand.Run(string[] args)`,
`SetupScreen → XyzTab → SetupSteps.ApplyXyz(typed values)`, and `XyzController.Post(RequestDto) → ActionResult` —
each independently hand-written, converging only on the shared primitives underneath, never on the
operation itself.

## Design direction (leaning, staged — not agreed)

A single "one definition generates all three surfaces" framework is a real, nontrivial new abstraction
layer — not a free refactor — and building it before closing the concrete gap above would delay real
value for a speculative one. Leaning toward closing the gap with today's architecture first:

1. **Close the setup-only gap** by adding the missing CLI surface: a `config auth` subcommand family (the
   single largest gap — a whole configuration domain with zero CLI reach), a way to set
   `StateEngine`/`StateConnectionString` from the command line, and a `config print`/`config effective`-shaped
   command for what "Print config" already does in the TUI.
2. **Close the CLI-only gap** by widening existing setup tabs to their command's full surface: the
   Drivers tab handling any driver (not just MySQL), the Certificate tab covering `renew`/`retrieve`/`list`/
   the currently-hardcoded `--template`, arbitrary secret management, and an invite role picker.
3. **Extract a UI-free "operation" layer for each duplicated case**, starting with the confirmed one:
   one method — "install this descriptor driver, from this catalog id or explicit params" — living in a
   surface-agnostic project (alongside `LibraryInstaller`, not inside `DbDataSync.Cli` or `DbDataSync.Api`),
   that `DriverCommand`, `DriversController`, and `SetupSteps` all call into instead of each assembling the
   same sequence by hand. Repeat for the state-database library auto-install case. This is the layer that
   actually needed adding — not a CLI/TUI-only concern, since the API's own duplication is real too.
4. **A genuinely shared step/flag/endpoint definition** — generating or validating a CLI flag set, a TUI
   screen, *and* an API contract from one source — stays a real, aspirational future direction, explicitly
   not committed to here.

## What this does not do

- **Does not mean literally every flag needs a matching interactive widget or API endpoint.** A purely
  scripting-shaped flag (`config check --json`) doesn't have a meaningful interactive or HTTP-response
  equivalent beyond what already exists. The principle is "every underlying *operation* reachable the
  same way everywhere," not "every flag becomes a checkbox and a route."
- **Does not commit to building the shared-definition framework** (design-direction item 4) as part of
  this plan.
- **Does not touch `config check`/the readiness engine** — already correctly shared across the CLI and
  the TUI today (the API's own readiness surface, if any, wasn't checked as part of this pass).
- **Does not claim a complete audit of API-vs-CLI duplication.** One case is confirmed in detail; the rest
  is a real gap in this doc's own research, named rather than glossed over.

## Confirmed by manual testing, 2026-09-21

Manually driving `dbdatasync setup` and `dbdatasync config set` on a real Windows box independently hit
exactly the setup-only gap above: several settings changeable in the TUI have no `config set` key at all,
so `AdminConfigService.Writable`'s catalog is the actual boundary, not just a documentation claim. No new
information beyond what's already written above — recorded here as a real-world confirmation, not a new
finding.

## Open questions (UNDECIDED)

- **Does "every flag" mean every flag, or every operation?** Leaning: every operation, with pure
  output-format/scripting flags as a deliberate, named exception — worth confirming explicitly.
- **Scope and priority of the `config auth` subcommand family** — the single largest concrete gap found,
  probably deserving its own design pass before folding into a general implementation phase.
- **A full audit of API controllers against CLI commands** for the same class of duplication found in
  `DriversController` — not done here, and probably the right next research step before writing an
  implementation phase for design-direction item 3.
- **Invite role picker in setup** — small, but real; worth confirming it's wanted.
- **Whether the shared-definition framework (item 4) is ever worth building**, versus stages 1–3 plus a
  documented review habit on every future change. Not resolved here.
