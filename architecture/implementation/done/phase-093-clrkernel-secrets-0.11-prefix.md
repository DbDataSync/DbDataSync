# Phase 93 — ClrKernel.Core.Secrets 0.9.2 → 0.11.0, and a configured `DbDataSync` prefix

**Status**: Complete.
**Plan reference**: `architecture/planning/done/clrkernel-secrets-0.11-prefix-config.md`; this doc's own
prior draft (see git history) recorded the verified 0.11.0 API surface, re-confirmed independently
during implementation (below) before writing any code.

## Independent API verification

Downloaded `clrkernel.core.secrets.0.11.0.nupkg` from nuget.org, unzipped it, and read
`lib/net8.0/ClrKernel.Core.Secrets.xml` (full XML doc comments ship in the package) plus reflected the
shipped `.dll` directly with PowerShell to enumerate real constructors/methods rather than trust prose.
Confirmed everything this doc's draft claimed, and found one thing worth recording precisely because it
wasn't guessed:

- `SecretStore(string prefix, bool cacheLocally)` and the old `SecretStore(bool)` (defaulting to
  `SecretPrefix.Default`, `"ClrKernel"`) both exist and are public. A third constructor,
  `SecretStore(string, bool, bool)` (adding `buildDefaultChain`), exists in the shipped assembly but
  reflects as **non-public** — so it was never a real option for this app regardless; the doc's plan to
  ignore it was correct for an extra reason beyond "not needed."
- `SecretStore.EnvName(key)`, `SecretStore.ForProviders(providers)` and its prefixed overload
  `ForProviders(prefix, providers)`, `Prefix`, `CanStore`, and the new `CanPersist` are all public and
  exactly as documented.
- Reflected `SecretNotFoundException` directly: constructing a prefixed, empty-provider store and
  calling `Resolve` on a missing key produces `"No secret found for '<ref>'. Looked in: . Nothing here
  can store one: set the <PREFIX>_SECRET_<REF> environment variable, or give this machine a credential
  store."` — the package's *own* not-found message already names both the ref and the exact env var
  name, correctly reflecting whatever prefix the store was built with. This mattered for "How to
  verify"/item 6 below: nowhere in this app wraps or rewrites that message, so it was a no-op to check,
  not something to fix.
- Confirmed the doubled-prefix composition empirically rather than asserting it: a store built with
  prefix `"DbDataSync"` resolving ref `dbdatasync:connection:foo` (from `SecretRefs.ForConnection`)
  computes `EnvName` as `DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_FOO`. Expected and not a bug — see
  `SecretStorePrefixTests.EnvName_ForAConnectionSecretRef_ComposesBothPrefixesWithoutConflatingThem`.

## What was built

1. **Package bump**: `src/DbDataSync.Core/DbDataSync.Core.csproj` — `ClrKernel.Core.Secrets` `0.9.2` →
   `0.11.0`.
2. **Every `SecretStore` construction site now passes `"DbDataSync"`.** The doc's search predicted
   exactly two sites (`DbDataSyncHost.cs`, `TaskRunner/Program.cs`); grepping `new SecretStore(` at
   implementation time found **four** — the other two, `DbDataSync.Cli/InviteCommand.cs` and
   `DbDataSync.Cli/SecretCommand.cs`, are CLI commands that landed in phase 79, after the plan doc's own
   search was done. All four now read `new SecretStore("DbDataSync", true)`.
3. **`SecretRefs.EnvironmentVariableFor` retired.** Every caller found by grep, not just
   `ConnectionsController.GetCredentialSource`:
   - `ConnectionsController.GetCredentialSource` — the controller now takes `SecretStore secrets` as an
     added constructor parameter (a DI singleton, confirmed injectable — no caller lacked one) and calls
     `secrets.EnvName(secretRef)`.
   - Seven call sites across the Api.Tests integration-test suite (`BackfillIntegrationTests`,
     `ChangeCounterSourceIntegrationTests`, `ConcurrentRunsIntegrationTests`, `CrossInstanceEndToEndTests`,
     `PreviewIntegrationTests`, `RunLifecycleIntegrationTests`), all of which set an environment variable
     so the spawned TaskRunner can resolve a test connection's credential. Each now resolves the exact
     same `SecretStore` singleton the host under test uses (via `factory.Services.GetRequiredService
     <SecretStore>()`) and calls `.EnvName(...)` on it, rather than reconstructing the name by hand.
   - No caller was found with no `SecretStore` reachable — the "say so rather than inventing a
     workaround" case in the plan did not arise.
4. **Secret-ref namespacing left untouched, composition confirmed rather than assumed** — see "Independent
   API verification" above.
5. **No migration mechanism** — not built, as planned.
6. **"Secret not found" messaging** — checked every surfacing point:
   - `DriverConnectionFactory.OpenAsync` and `RunExecutor.OpenConnectionAsync` call
     `secretStore.Resolve(...)` directly and let `SecretNotFoundException` propagate unwrapped — the
     package's own message (verified above) already names the ref and env var, so these needed no
     change.
   - `ConfigRepository.SaveConnection` had a real gap: when SqlAuth was chosen with no password and no
     existing secret, the thrown `ConfigValidationException` said only *"No password was provided and no
     existing credential is stored for connection '{name}'."* — naming the connection, not the secret ref
     or the environment variable an operator would actually need to set. Fixed to also name both
     (`_secrets.EnvName(secretRef)`), covered by a new test,
     `ConfigRepositoryTests.SaveConnection_SqlAuthWithoutPasswordOrExistingSecret_MessageNamesRefAndEnvVar`.

### Bugs found beyond the plan's scope, fixed as part of this phase

Two latent bugs surfaced only because this phase made the store's prefix load-bearing for the first
time; both are squarely "the upgrade breaking something silently" rather than new scope, so both were
fixed here rather than deferred:

- **`TestApiFactory.cs` and `AuthenticatedApiFactory.cs`** (Api.Tests) swapped in
  `SecretStore.ForProviders([new InMemorySecretProvider()])` — no prefix, defaulting to
  `SecretPrefix.Default` ("ClrKernel") — while the real composition root now builds `"DbDataSync"`. Every
  test running against either factory would have computed env-var names under the *wrong* prefix from
  here forward. Both now pass `"DbDataSync"` explicitly (`SecretStore.ForProviders("DbDataSync", [...])`).
- **`tools/DbDataSync.DevHarness/AppProcesses.cs`** hand-rolled its own copy of the exact folding logic
  `SecretRefs.EnvironmentVariableFor` used to do, hardcoded to `CLRKERNEL_SECRET_`, to preset the
  environment variable the harness's spawned API/TaskRunner processes read their scenario credentials
  from. Left alone, `dbdatasync-dev-harness up` would have silently broken for any SqlAuth scenario the
  moment the package bump landed — the spawned process would look for `DBDATASYNC_SECRET_*` and find
  nothing, since the harness had set `CLRKERNEL_SECRET_*`. Replaced the hand-rolled folding with a
  prefix-only `SecretStore.ForProviders("DbDataSync", [])` used solely to compute the name via the real
  `EnvName`, so this can never drift from the app's actual prefix again.
- Two Playwright fixtures had the same hardcoded name baked in for the same reason (presetting an env var
  for a spawned process to find): `tests/DbDataSync.Web.Tests/playwright.config.ts`'s `secretEnv` and
  `tests/DbDataSync.Web.Tests/tests/golden-path.spec.ts`'s assertion on the `credential-env-var` field.
  Both updated to `DBDATASYNC_SECRET_*`.
- `README.md` and `CONFIG.md` documented the old `CLRKERNEL_SECRET_*` fallback name and didn't mention
  the store's prefix being configured at all; both updated, and CONFIG.md's "Secrets" section now
  explains the prefix/ref-namespacing distinction so a reader doesn't have to rediscover it the way this
  phase's own doc had to spell out.

## Tests added or extended

- **New**: `tests/DbDataSync.Core.Tests/SecretStorePrefixTests.cs` — four tests: both real construction
  sites' literal expression (`new SecretStore("DbDataSync", true)`) reports `Prefix == "DbDataSync"`;
  `EnvName` starts with `DBDATASYNC_SECRET_` and contains no `CLRKERNEL` (an explicit assertion, not one
  that would pass by accident); the two-prefix composition case; and the `ForProviders` overload tests
  use carries the prefix too.
- **Extended**: `ConfigRepositoryTests` (+1, the message-content test above);
  `AdminConfigServiceTests` (+1, `SetStateConnectionSecret` end-to-end through a `"DbDataSync"`-prefixed
  store, asserting both `Resolve` and the exact `EnvName`); `AdminConfigControllerTests` (+1, the same
  through the real HTTP endpoint and the DI-registered `SecretStore`); `SecretCommandTests` (+1 — the
  CLI's own store resolves what it wrote, and a *differently*-prefixed store built with the package's
  unconfigured default cannot see it, proving the two are genuinely different provider namespaces, not
  just a naming label); `ConnectionTestIntegrationTests`'s existing `credential-source` assertion updated
  to the new name.

## Verification

- **Build**: full-solution `dotnet build` clean (0 warnings, 0 errors), including
  `tools/DbDataSync.DevHarness` explicitly (not part of the default solution build target, built and
  checked separately).
- **Unit/non-HTTP tests**: everything added or extended above passes — `SecretStorePrefixTests` 4/4,
  `SecretCommandTests` 5/5 (real OS-credential-store round trip on this Windows machine, not mocked),
  `AdminConfigServiceTests` 12/12.
- **Full suite, `Category!=Integration` and `Category=Integration`**: run and every failure traced to
  one of three pre-existing, environmental causes, none introduced by this phase — confirmed by running
  the identical test classes against unmodified `main` (`git stash`) and getting identical failure
  counts before restoring this phase's changes:
  1. **No live SQL Server/Docker in this sandbox** — every `Category=Integration` test across every
     project fails with a SQL connection error (e.g. `InviteCommandTests`: `"A network-related or
     instance-specific error occurred while establishing a connection to SQL Server"`). Same for the
     handful of `Category!=Integration` tests that still touch a real server.
  2. **`Negotiate` + `TestServer`, a pre-existing documented gap in this specific Windows sandbox** —
     `DbDataSyncHost.Build` registers `AddNegotiate()` unconditionally whenever `OperatingSystem.IsWindows()`
     (true here), and `Microsoft.AspNetCore.Authentication.Negotiate` breaks `WebApplicationFactory`'s
     `TestServer` in this environment regardless of the request's own auth requirements — this is the
     exact gap `AdminConfigServiceTests`' own class doc comment already names, citing phase 79's
     retrospective. It explains the large `Category!=Integration` Api.Tests failure count (hundreds of
     500s across `TestApiFactory`- and `AuthenticatedApiFactory`-backed tests, none touching secrets)
     — reproduced identically against unmodified `main`.
  3. **A known libgit2-on-Windows quirk**: `Directory.Delete` on a temp git repo's object files throws
     `UnauthorizedAccessException` because libgit2 writes them read-only — the same issue
     `AdminConfigServiceTests`' `Dispose` already works around with a documented `File.SetAttributes`
     loop; `ConfigRepositoryTests` and some `TaskRunner.Tests`/`State.Tests` classes don't have that
     workaround and fail on cleanup accordingly. Confirmed pre-existing by running `ConfigRepositoryTests`
     in isolation against unmodified `main`.

  None of the three categories overlaps with anything this phase touched; every test that doesn't hit a
  real database or a live `TestServer` passes.

## Decisions and real findings

- **`CanPersist` vs `CanStore` — a real finding, disclosed and not acted on, per the plan's explicit
  instruction**: `SecretCommand.Set` prints `"Stored '<ref>'."` and `AdminConfigController.SetSecret`
  returns `204 No Content` — both imply the write survived, based on `Store()` succeeding (which only
  needs `CanStore`, true even with nothing but the in-memory cache available). Neither checks the new
  `CanPersist` first. On a machine with no OS keychain and no `FileSecretProvider` configured, both
  today report success for a secret that will vanish the moment the process exits. This is exactly the
  gap the plan called out as a `CanPersist`-shaped finding for later, not this migration's job — no
  UI/CLI change was made here.
- **`FileSecretProvider` / `AdoptFileSecrets()`**: confirmed unused anywhere in this app, as planned; not
  adopted.
- **No migration of secrets stored under the old un-prefixed naming**: confirmed as the plan intended —
  an install upgrading past this change re-enters credentials under the new prefix the same way it would
  set one for the first time.

## Follow-ups this phase deliberately did not build

- A `CanPersist`-aware message for `dbdatasync secret set` and the admin config secret endpoint (the
  finding above).
- Anything using `FileSecretProvider`/`AdoptFileSecrets()`.
- A migration path off the old `CLRKERNEL_SECRET_*`/unprefixed naming.
