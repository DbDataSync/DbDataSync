# Phase 81 — an admin screen for datasync.config.yaml

**Status**: Planned, not started. Depends on phase 79 (`datasync.config.yaml` itself, the secret-store
convention, and the resolver), which has landed — see `architecture/implementation/done/phase-079-standardized-config-file.md`.
**Plan reference**: none upstream in `architecture/planning/` — resolved directly through clarifying
questions in conversation on 2026-09-01, recorded here rather than in a separate planning doc since there
was no unresolved rough thought preceding it.

**Carried over from phase 79's retrospective**: `GitCommitService.GetHistory` filters by a relative path
*prefix* (e.g. `config/replications/<name>`), and `datasync.config.yaml` sits one level above `config/`
— it's git-tracked and diffable at the command line, but does **not** currently surface in the Config
History UI's `config/`-scoped queries. Whether this screen needs its own history/diff view for the file
(reusing `GitCommitService.GetHistory` with `datasync.config.yaml` as the path), or whether that's better
left to widening phase 35's scope separately, is this phase's call to make — not assumed here, but not to
be silently skipped either now that it's known.

## What this phase will build

**A new "Admin" section in the SPA** — nothing like it exists today (no admin/settings route at all;
`grep`ing the SPA for one turns up nothing). Gated the same way the rest of the app gates by role
(`Auth:AdminGroup`/`Policies.Viewer`-style checks already used elsewhere), visible only to Admins.

**One screen listing every `DataSync:*` key `CONFIG.md` documents**, each row showing:

- The key and its current *effective* value.
- **Where that value came from**: `datasync.config.yaml`, an environment variable, a CLI argument, or
  the built-in default. ASP.NET Core's configuration system already tracks this per-key (each provider
  in `IConfigurationRoot.Providers` can be asked whether it supplies a given key) — reading it back out
  for display is new, but the information already exists rather than needing to be reconstructed.
- **A value whose source is the file is editable in place.** Saving writes back to
  `datasync.config.yaml` through the same path phase 79 defines (git-committed, attributed to the
  signed-in admin the same way every other config write is).
- **A value whose source is anything else (env var, CLI flag, default) is read-only**, labeled with its
  source, plus an **"Adopt into file"** action: writes the current effective value into
  `datasync.config.yaml`, making it the editable, file-sourced value from then on. This does not change
  what's *actually running* until a restart — same as every other edit here, see below — so the row's
  label should make clear the adopted value won't take effect until then, not imply it just did.
- **A credential-bearing key (`StateConnectionString`) never shows the secret**, regardless of source.
  If it's file-sourced, the connection string minus its credential is shown (nothing sensitive is ever
  in the file in the first place — see phase 79) and a separate control lets the admin set/replace the
  secret, which writes through `SecretStore` the same way `datasync secret set` does — this screen and
  the CLI command are two doors onto the same store, not two stores. If it's sourced from an environment
  variable or CLI argument that happens to carry a raw connection string with a password in it (an admin
  running the tool the *old* way, before adopting this feature), that value is masked before it ever
  reaches the browser — the API redacts it server-side, the same instinct
  `ConnectionsController.GetCredentialSource` already applies (never sends the actual secret, only which
  ref it resolves through) extended to a raw value the app doesn't control the shape of.

**A "restart required" banner** whenever a save (or an adopt) has happened this session and the running
process hasn't restarted since — `ApiOptions` is a singleton resolved once at startup (`DataSyncHost.cs`),
so nothing here takes effect live, and a screen that let an admin believe otherwise would be worse than
not having the screen.

## API surface

- `GET /api/admin/config` — every documented key, its effective value (masked where it's a credential,
  omitted/redacted where the value itself might be sensitive text the app doesn't parse), its source, and
  whether it's editable.
- `PUT /api/admin/config/{key}` — writes one key into `datasync.config.yaml` (file-sourced keys only;
  a non-file-sourced key reaches this via "adopt" first, which is the same call).
- `PUT /api/admin/config/{key}/secret` — routes to `SecretStore.Store` under that key's standardized ref,
  for the one or two keys that need it (just `StateConnectionString` today).
- All three `[Authorize(Policies.Admin)]` — a stricter gate than the `Viewer` policy most read endpoints
  use elsewhere, since this screen can reveal *which* settings exist and where they're sourced from even
  where it can't reveal a secret's value.

## How it will be verified

- A file-sourced value round-trips: set via the screen, confirm it lands in `datasync.config.yaml`
  correctly, confirm the API reports it back with source `"file"`.
- An env-var-sourced value is shown read-only with its source labeled, and "Adopt into file" moves it
  into the file and flips its reported source.
- `StateConnectionString`'s secret is never present in any API response body, file-sourced or not,
  including one deliberately seeded with a raw `Password=` value in an environment variable for the test.
- The restart-required banner appears after a save and would be expected to persist (this is a UI-state
  assertion, not something that survives an actual process restart in a test).
- A Viewer-role session gets 403 from all three endpoints.

## Decisions made

- File-sourced values are editable in place; everything else is read-only with an explicit adopt action
  — resolved in conversation rather than guessed, since silently offering to "adopt" every value would
  have been the easy wrong default (it changes which source wins after a restart, which is not nothing).
- Credential masking applies to *any* source, not just the file — the file can't carry one after phase 79,
  but an env var or CLI arg still can, and the screen must not become the first place that ever displays
  one in the browser.
- This is a new phase, not folded into phase 79, because it's a distinct vertical slice (SPA route +
  new API surface) with its own dependency on 79 already having landed — sequencing this after 79 in the
  build order is what phase 79's "what this phase will not build" section already says.

## What this phase will not build

- Editing settings that don't come through `DataSync:*` at all (Kestrel's own config, logging levels,
  etc.) — scoped to exactly what `CONFIG.md` documents today.
- Any live-reload of a changed setting. Covered above; a restart is always required.
- The TLS certificate management screen raised alongside this one — that's an unrelated, much larger,
  and currently unresolved planning item; see `architecture/planning/todo/windows-tls-certificate-management.md`.
  It may end up living in the same "Admin" section/nav entry this phase creates, but that's a UI
  placement decision for whenever that item is resolved, not a reason to couple the two builds.

## Open questions to resolve during implementation

- Exact shape of "the app doesn't control the shape of" values for masking non-file sources — `CONFIG.md`
  lists every key today, so in practice the only one that needs this is `StateConnectionString`; worth
  confirming no other current or near-term key is free-text enough to carry a credential unexpectedly.
