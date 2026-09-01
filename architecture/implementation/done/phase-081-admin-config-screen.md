# Phase 81 — an admin screen for datasync.config.yaml

**Status**: Complete.
**Plan reference**: none upstream in `architecture/planning/` — resolved directly through clarifying
questions in conversation on 2026-09-01, recorded here rather than in a separate planning doc since there
was no unresolved rough thought preceding it. Depends on phase 79 (`datasync.config.yaml` itself, the
secret-store convention, and the resolver), which had already landed.

## What was built

**A new "Admin" section in the SPA**, `src/DataSync.Web/src/pages/AdminConfigPage.tsx`, routed at
`/admin/config` (`src/DataSync.Web/src/App.tsx`). One screen, one table: every `DataSync:*` key
CONFIG.md documents, its live effective value, where that value comes from, and — for the keys
`datasync.config.yaml`'s writer can actually address — a way to edit it in place or adopt it. Reached
from a new gear icon in `AppShell`'s rail (`src/DataSync.Web/src/components/AppShell.tsx`,
`GearIcon` added to `icons.tsx`), hidden for a Viewer the same way `useIsAdmin` already hides every
other affordance a Viewer cannot use — the API's `[Authorize(Policies.Admin)]` is what actually enforces
this; the hidden nav item and the page's own "this screen is for administrators" fallback are only about
not offering a destination that would 403 on arrival.

**Backend**: `GET/PUT /api/admin/config`, `PUT /api/admin/config/{key}/secret`
(`src/DataSync.Api/Controllers/AdminConfigController.cs`, thin, delegating to
`src/DataSync.Api/Services/AdminConfigService.cs`). The key is the full `DataSync:*` colon-separated
path, URL-encoded (`DataSync%3AStateEngine`) — the same `encodeURIComponent` convention every other
by-name route in this SPA already uses for a value that might contain special characters.

`AdminConfigService` holds a fixed catalog of the 17 keys CONFIG.md documents under `DataSync:*`
(`RepoRoot` through `Auth:Passkeys:Origins`) and, per key:

- **Resolves its source** by walking `((IConfigurationRoot)configuration).Providers` and asking each
  `provider.TryGet(key, ...)`, remembering the last one that answers — the same last-registered-wins
  order `IConfigurationRoot` itself resolves ties by (confirmed against .NET's own `ConfigurationRoot`
  indexer, which walks its provider list in reverse and returns the first hit). The winning provider's
  *type* maps to a label: a new `DataSyncConfigFileProvider`
  (`src/DataSync.Api/Configuration/DataSyncConfigFileSource.cs`) → `"file"`,
  `CommandLineConfigurationProvider` → `"command line"`, `EnvironmentVariablesConfigurationProvider` →
  `"environment variable"`, `JsonConfigurationProvider` → `"appsettings.json"`, no provider at all →
  `"default"`.
- **`DataSyncConfigFileProvider` is a new, dedicated provider type**, not the framework's own
  `MemoryConfigurationProvider` that `DataSyncHost.InsertConfigFile` used before this phase. The
  distinction matters: a test's own `ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(...))`
  uses the framework type too, and would have been indistinguishable from the real file by shape alone —
  this phase needed real, unambiguous attribution, not a guess.
- **For a "default"-sourced key, the displayed value comes off the same live options objects the rest
  of the process already resolves** — `ApiOptions`, `AuthOptions`, `PasskeyOptions` (all three already
  DI singletons) — rather than a second copy of each default that could drift from theirs.
- **For a "file"-sourced key, `ToEntry` re-reads `datasync.config.yaml` directly from disk** rather than
  trusting the provider's boot-time snapshot. `IConfigurationRoot` loads every provider once at startup
  and nothing here hot-reloads it — which is correct for what the *running process* actually uses (the
  restart banner says so) — but would otherwise make a Save through this very screen look like it
  silently did nothing, since the table would keep showing the pre-save value until a restart. Re-reading
  the file is cheap and makes "what's on disk right now" and "what this screen shows after Save" agree,
  without claiming the change is live anywhere else.
- **`StateConnectionString` is masked when it isn't file-sourced and contains a credential.**
  `ConfigValidation.ContainsEmbeddedCredential` (new, non-throwing sibling of the existing
  `RejectEmbeddedCredential` in `src/DataSync.Core/Config/ConfigValidation.cs` — both now share one
  `FindCredentialKeySegment` detector so they can never disagree about what counts as a credential) is
  the check; when it's true the API sends `value: null, masked: true` and the raw string never leaves
  the server. A file-sourced value is never checked at all — phase 79 already guarantees the file itself
  never carries one — matching `ConnectionsController.GetCredentialSource`'s existing precedent of never
  sending a secret, only naming its source.

`PUT /api/admin/config/{key}` writes through `DataSyncConfigFile.SetValue` (unchanged from phase 79) and
commits the file via `GitCommitService`, attributed to `CurrentUser.Author` — the same attribution every
other config write in this app already uses. **Only the flat, top-level `DataSync:<Key>` keys are
writable.** `SetValue`'s own doc comment already said its text-editing writer "does not handle arbitrary
YAML nesting on the write side," and phase 79's retrospective judged that shape "sufficient for... phase
81's admin screen" rather than extending it — a signal taken at face value here rather than re-litigated.
`DataSync:Auth:*`, `DataSync:Auth:Passkeys:*` (one and two levels deeper than `SetValue` can address) and
`DataSync:Auth:Passkeys:Origins` (an array; `SetValue` only ever writes one scalar per key) are shown —
real value, real source, same as everything else — but `Editable` and `CanAdopt` are always false for
them, and `PUT` refuses them with `404`. This is a real, deliberate scope line, not an oversight; see
"A real decision," below.

**A second writer to the repo root needed `GitCommitService` to stop being constructed inline.** Before
this phase, `DataSyncHost.Build` did `new GitCommitService(options.RepoRoot)` directly inside
`ConfigRepository`'s own registration lambda — fine while `ConfigRepository` was the only writer.
`GitCommitService`'s own doc comment is explicit that its in-process write lock is only safe when every
writer to a given repo root *shares one instance* ("registered as a DI singleton... an in-process lock
around the write path is sufficient"); a second, separately-constructed instance for
`AdminConfigService` would have raced against it. `GitCommitService` is now its own DI singleton
(`DataSyncHost.cs`), and `ConfigRepository`'s registration takes it from DI instead of constructing its
own — a correctness fix this phase's own second-writer shape made necessary, not a refactor for its own
sake.

**`PUT /api/admin/config/{key}/secret`** is a two-line pass-through to `SecretStore.Store` under the
fixed `SecretRefs.ForAppSetting("stateConnectionString")` ref — the same store `datasync secret set`
writes to, confirmed by a test that sets it through the endpoint and reads it back through
`SecretStore.TryResolve` directly.

**A restart-required banner** (`AdminConfigPage.tsx`) appears after any successful save or adopt this
session — component state, reset on a hard reload, which is a fine proxy for "this session" given the
thing it is warning about (the running process hasn't restarted) is itself cleared by an actual restart.

**Two small drive-by fixes discovered while building the catalog:**

- `DataSync:ChangeCheckRetentionDays` is a real, code-read `DataSync:*` key (`ApiOptions`,
  `RunPruningService`) that CONFIG.md's own `DataSync:*` table never documented. Added as a row there —
  the screen is specified as showing "every key CONFIG.md documents," so the gap had to be closed for
  the screen to be honest about what it's showing, and it's a one-line fix to a table that already
  covers its two siblings (`RunRetentionDays`, `RunRetentionMaxPerMapping`).
- `ConfigValidation.RejectEmbeddedCredential` and the new `ContainsEmbeddedCredential` now share one
  private `FindCredentialKeySegment` helper instead of `RejectEmbeddedCredential` keeping its own inline
  loop — extracted so the throwing and non-throwing checks are provably the same detector, per the
  method's own existing doc comment about not letting a second copy drift from the first.

## A real decision: Config History was not widened to cover this file

Carried over from phase 79's retrospective: `GitCommitService.GetHistory` filters by a relative path
*prefix* (`config/replications/<name>`), and `datasync.config.yaml` sits at the repo root, one level
above `config/` — git-tracked and diffable at the command line, but invisible to the Config History tab's
`config/`-scoped queries. Whether this phase should give the file its own small history view was left
as this phase's call to make.

**Decision: no — out of scope for phase 81, and not silently dropped either.** Two reasons converged:

1. Widening `GetHistory` correctly is not free. Its prefix match is `path.StartsWith(prefix.TrimEnd('/')
   + "/")` — passing `"datasync.config.yaml"` as the prefix would normalize to
   `"datasync.config.yaml/"`, which a changed path of exactly `"datasync.config.yaml"` (no trailing
   segment) never starts with. It needs a real code change (matching the bare path too, not just a
   prefix of it), not just a new call site.
2. **`architecture/implementation/todo/phase-035-config-history-diff-and-revert.md` already exists,
   already planned, and is already queued in this repo's build order directly beneath phases 81–83** —
   it is specifically about widening Config History with diff and revert (`GET
   .../history/{sha}/diff`, `POST .../history/{sha}/revert`), built on the same `GitCommitService` this
   phase touches. That is the natural home for "and also cover `datasync.config.yaml` at the repo root,"
   not a bespoke single-file history view bolted onto an admin settings screen that is otherwise about
   current values, not history.

So: this screen shows current effective values only. `datasync.config.yaml` remains git-diffable only at
the command line (`git log -- datasync.config.yaml` from the repo root) until phase 35 is built — at
which point widening its scope to include the repo-root file, with the small `GetHistory` fix noted
above, is a natural, low-cost addition to what that phase is already doing. Noted here, in phase 35's own
doc, and in the build-order table's existing note so a future implementer of 35 does not have to
rediscover it independently.

## Decisions made

- **Only flat, top-level `DataSync:<Key>` keys are editable/adoptable**; nested `Auth:*`/`Passkeys:*`
  keys are shown (real value, real source) but never writable here — forced by `DataSyncConfigFile.SetValue`'s
  own documented limits, not a shortcut taken silently. See "What was built," above.
- **`GetHistory`/Config History is not widened in this phase** — deferred to the already-planned,
  already-queued phase 35, which is purpose-built for exactly this. See "A real decision," above.
- **A new `DataSyncConfigFileProvider` type, not the framework's `MemoryConfigurationProvider`**, so
  source attribution is unambiguous rather than a type-shape guess that a test's own in-memory
  configuration override could collide with.
- **`GET` re-reads the file from disk for a file-sourced key** rather than trusting the (boot-time,
  otherwise stale) `IConfigurationRoot` snapshot, so a Save is visible immediately in the table without
  implying it is live anywhere else — the restart banner still says it is not.
- **`GitCommitService` promoted to its own DI singleton**, and `ConfigRepository`'s registration updated
  to take it from DI — required correctness, not a stylistic refactor, once a second writer to the same
  repo root existed.
- **`ChangeCheckRetentionDays` added to CONFIG.md's `DataSync:*` table** — a real gap found while
  building the catalog this screen is specified to mirror exactly.
- **The restart-required banner is plain component state**, not persisted across a hard reload — a
  hard reload is a reasonable proxy for "this session" given a real process restart is the only thing
  that actually clears the underlying condition.

## What this phase did not build

- Editing anything outside `DataSync:*` (Kestrel config, logging levels) — out of scope per the original
  doc, unchanged.
- Any live-reload of a changed setting — `ApiOptions`/`AuthOptions`/`PasskeyOptions` stay singletons
  resolved once at startup; the restart banner is the whole story here.
- The TLS certificate management screen — a separate, unrelated, larger planning item
  (`architecture/planning/todo/windows-tls-certificate-management.md`, now split into phases 82/83 in the
  build order) that this phase's Admin nav entry may eventually share space with, but that is a UI
  placement decision for when 82/83 are built, not a reason to couple the two.
- `datasync service install` — untouched, as it was in phase 79.
- Config History/diff/revert for `datasync.config.yaml` — see "A real decision," above; left for phase 35.
- A Playwright scenario in `tests/DataSync.Web.Tests/tests/golden-path.spec.ts` — considered and
  deliberately not added; see "How it was verified."

## How it was verified

- **`dotnet build DataSync.slnx`**: clean, 0 errors, only the same pre-existing warnings this repo
  already carries (none new).
- **`npx tsc --noEmit -p tsconfig.json`** (`src/DataSync.Web`): clean.
- **`npm run lint`** (oxlint, `src/DataSync.Web`): clean — the five warnings it reports are all
  pre-existing, in files this phase did not touch.
- **`tests/DataSync.Api.Tests/AdminConfigServiceTests.cs`** (new, 11 tests, **all passing**):
  `AdminConfigService` built directly — real `ApiOptions.FromConfiguration`/`AuthOptions.FromConfiguration`/
  `PasskeyOptions.FromConfiguration`, a real `GitCommitService` against a temp repo, a real
  `DataSyncConfigFileProvider`/`EnvironmentVariablesConfigurationProvider`/`CommandLineConfigurationProvider`
  — with **no ASP.NET Core host at all**. Covers: default-sourced (no provider, and the
  `ApiOptions`-backed default value shown correctly); file-sourced round-trip through `Set` including the
  git commit it produces; environment-variable-sourced read-only + adopt; command-line-sourced labeling;
  a nested `Auth:*` key never writable even though it can be file-*sourced*-looking; an unknown key
  returns null; `StateConnectionString` never masked when file-sourced; masked and not adoptable when an
  environment variable carries a raw `Password=`; shown and adoptable when an environment variable's
  connection string has no credential in it; the secret endpoint's helper stores under the fixed ref and
  refuses every other key.
- **`tests/DataSync.Api.Tests/AdminConfigControllerTests.cs`** (new, 10 tests, HTTP-level via
  `WebApplicationFactory`/`AuthenticatedApiFactory`, covering the same ground end-to-end plus the actual
  role check): **all 10 fail in this sandbox**, for the identical, pre-existing, already-documented
  reason phase 79's retrospective named for ~174 other tests in this project —
  `System.NotSupportedException: Negotiate authentication requires a server that supports
  IConnectionItemsFeature like Kestrel`, thrown by `TestServer` the moment any authenticated request
  reaches `AuthenticationMiddleware` on a Windows sandbox with Negotiate registered
  (`DataSyncHost.Build`'s `if (OperatingSystem.IsWindows()) authentication.AddNegotiate();`). Confirmed
  **not a regression from this phase**: `UserManagementTests` (pre-existing, untouched) and
  `ConnectionsControllerTests` (pre-existing, untouched) fail with the exact same exception when run in
  this sandbox today. This is a Windows-sandbox-and-`TestServer`-specific gap — Negotiate is never
  registered on the Linux hosts CI actually runs on, so these tests are written correctly and will pass
  there; they cannot be made to pass in *this* environment without touching test infrastructure this
  phase does not own (the same call phase 79 made about its own environment-specific gaps).
  `AdminConfigServiceTests`, above, is what actually verifies this phase's logic in this sandbox.
- **Full-suite comparison against the documented phase-79 baseline**, same repo, same sandbox:
  - `DataSync.Api.Tests`: 258 total (237 baseline + 21 new) / 74 passed (63 baseline + 11 new
    `AdminConfigServiceTests`) / 184 failed (174 baseline, unchanged + 10 new
    `AdminConfigControllerTests`, all the Negotiate/`TestServer` gap above). The baseline's own 174
    failures are exactly unchanged — no regression.
  - `DataSync.Core.Tests`: 167 total / 132 passed / 35 failed — identical to phase 79's own reported
    numbers (all 35 in `ConfigRepositoryTests`' pre-existing libgit2-read-only-object teardown quirk on
    Windows, untouched by this phase).
  - `DataSync.State.Tests`: 178 total / 151 passed / 27 failed — identical to phase 79's own reported
    numbers (all 27 in `CrossEngineStateTests`, needs live MsSql/Postgres, none reachable here).
  - `DataSync.Cli.Tests`: 16 total / 15 passed / 1 failed — identical to phase 79's own reported numbers
    (`InviteCommandTests`, needs live MsSql, none reachable here).
  - This phase's own new tests use the same `GitTempDirectory`-style workaround phase 79 introduced for
    its own new tests (`File.SetAttributes(..., Normal)` before `Directory.Delete`), so they tear down
    cleanly despite the same libgit2-on-Windows quirk that leaves `ConfigRepositoryTests` failing at
    teardown.
- **Playwright** (`tests/DataSync.Web.Tests`): considered and **not added**. The project has exactly one
  spec file, `golden-path.spec.ts`, structured as a single long `test.describe.serial` narrative
  ("define, configure, and run a replication end-to-end"), and its `globalSetup` unconditionally stands
  up a real SQL Server database before *any* test in the project runs — regardless of which spec file
  is added, `globalSetup` cannot succeed without live SQL Server/Docker, neither of which is available in
  this sandbox (the same gap phase 79's retrospective noted for its own MsSql-backed tests). A new admin-
  config scenario would therefore be unverifiable here no matter where it was added, and — since the
  admin config screen touches no database at all — it does not fit naturally into a narrative that is
  entirely about a source/target replication. Given both the poor fit and the inability to verify it in
  this environment, adding one was judged out of proportion for this phase rather than done unverified.

## Files touched

Backend: `CONFIG.md`; `src/DataSync.Core/Config/ConfigValidation.cs`;
`src/DataSync.Api/Configuration/DataSyncConfigFileSource.cs` (new);
`src/DataSync.Api/DataSyncHost.cs`; `src/DataSync.Api/Services/AdminConfigService.cs` (new);
`src/DataSync.Api/Controllers/AdminConfigController.cs` (new);
`tests/DataSync.Api.Tests/AdminConfigServiceTests.cs` (new);
`tests/DataSync.Api.Tests/AdminConfigControllerTests.cs` (new).

Frontend: `src/DataSync.Web/src/api/types.ts`; `src/DataSync.Web/src/api/client.ts`;
`src/DataSync.Web/src/api/hooks.ts`; `src/DataSync.Web/src/pages/AdminConfigPage.tsx` (new);
`src/DataSync.Web/src/App.tsx`; `src/DataSync.Web/src/components/AppShell.tsx`;
`src/DataSync.Web/src/components/icons.tsx`.
