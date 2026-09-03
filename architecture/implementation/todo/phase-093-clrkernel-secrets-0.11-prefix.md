# Phase 93 — ClrKernel.Core.Secrets 0.9.2 → 0.11.0, and a configured `DbDataSync` prefix

**Status**: Planned, not started.
**Plan reference**: `architecture/planning/done/clrkernel-secrets-0.11-prefix-config.md` — written as
`clrkernel-secrets-0.11-prefix-config.md`, resolved except for the package's own real API, which that
doc explicitly deferred to implementation time ("pull the actual 0.11.0 release and read its public
API/changelog before writing an implementation phase doc"). That's been done for this doc — see below.

## The real 0.11.0 API (verified by downloading the package and reading its shipped XML docs)

`ClrKernel.Core.Secrets` 0.11.0 is on nuget.org. Pulled and reflected directly (its `.xml` doc file
ships in the package) rather than guessed at. The relevant surface:

- **`SecretStore` gained a `prefix` constructor parameter**: `SecretStore(string prefix, bool
  cacheLocally)` (and a three-arg overload adding `buildDefaultChain`), alongside the old
  parameterless-prefix `SecretStore(bool)` which still exists and defaults to `SecretPrefix.Default`
  ("ClrKernel" — i.e., if this migration didn't pass a prefix explicitly, the app would keep resolving
  through ClrKernel's own default naming, not silently break, but would be branded wrong).
  `SecretStore.ForProviders(...)` gained the same prefixed overload.
- **The prefix drives every provider's naming uniformly**, not just the environment-variable fallback
  this app's own `SecretRefs.EnvironmentVariableFor` currently hand-rolls: `WindowsCredentialSecretProvider`
  stores under target name `<prefix>:<key>` (`ClrKernel:<key>` by default), `KeychainSecretProvider`/
  `LibSecretSecretProvider` use the prefix as the service name, and `EnvironmentSecretProvider` derives
  `<PREFIX>_SECRET_<KEY>` via `SecretPrefix.ToEnvironmentToken` (upper-cased, non-alphanumerics → `_`,
  same folding this app's own code already does by hand).
- **`SecretStore.EnvName(string key)`** is a new public instance method — "the one place a caller should
  build such a name," per its own doc comment. This makes `SecretRefs.EnvironmentVariableFor` (this
  app's hand-rolled duplicate of the same folding logic, hardcoded to `CLRKERNEL_SECRET_`) redundant now
  that the package does it itself, correctly scoped to whatever prefix the store was actually built
  with. Removing the duplicate is squarely inside "coordinate with the real API rather than guess" —
  it's not a new feature, it's deleting logic the upgrade makes unnecessary.
- **`SecretStore.CanPersist`** is new: `CanStore` is true whenever *any* provider can store — including
  the in-memory cache, which always can — so it was never safe to tell an operator "saved" off `CanStore`
  alone. `CanPersist` is specifically "would survive a restart." Worth checking whether anything today
  (the `datasync secret set` CLI command from phase 79, the admin config screen's secret-set endpoint
  from phase 81) implies persistence more strongly than `CanStore` actually promises — if so, note it,
  but **do not build new UI/messaging around this as part of this phase**; that's a real finding for a
  follow-up, not this migration's job.
- **`FileSecretProvider`** and **`SecretStore.AdoptFileSecrets()`** are new (a plaintext-file-backed
  provider for a container with no OS keychore, and a one-shot mover off it). Not used by this app today
  and not needed for this migration — noted for completeness, not in scope.

## What to build

1. **Bump the package reference**: `src/DbDataSync.Core/DbDataSync.Core.csproj` — `ClrKernel.Core.Secrets`
   `0.9.2` → `0.11.0`.
2. **Pass `"DbDataSync"` as the prefix at every `SecretStore` construction site.** There are exactly two
   today (find them by searching for `new SecretStore(` post-phase-92 — expect one in the API host
   composition root, one in the TaskRunner's entry point, matching what
   `clrkernel-secrets-0.11-prefix-config.md` already found at the pre-rename paths
   `DataSyncHost.cs:78`/`TaskRunner/Program.cs:21`). Both currently call `new SecretStore(true)` with no
   prefix — change to `new SecretStore("DbDataSync", true)`.
3. **Retire `SecretRefs.EnvironmentVariableFor`** (`src/DbDataSync.Core/Secrets/SecretRefs.cs`) in favor
   of `SecretStore.EnvName(key)` on whatever `SecretStore` instance is already reachable at each call
   site (it's a DI singleton in the API host — confirm it's injectable everywhere this is currently
   called, starting with `ConnectionsController.GetCredentialSource`, and trace every other caller
   rather than assuming that's the only one). If a genuine caller has no `SecretStore` instance in scope
   and truly cannot get one, that's a real finding — say so rather than inventing a static workaround
   that silently re-hardcodes the prefix.
4. **The secret ref prefixes themselves** (`SecretRefs.ForConnection` → `dbdatasync:connection:<name>`,
   `SecretRefs.ForAppSetting` → `dbdatasync:config:<key>`) **were already updated by phase 92** — this
   phase does not need to touch those strings again, only confirm they read correctly given the new
   `SecretStore` prefix is a *separate* concept (the provider-naming prefix, e.g. the env var/keychain
   service name) from the secret-ref namespacing convention (the `dbdatasync:` string inside the ref
   itself, which is this app's own scheme, not the package's). Don't conflate the two — verify they
   compose correctly (a ref like `dbdatasync:connection:foo` resolved through a store whose *provider*
   prefix is also `DbDataSync` ends up as env var `DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_FOO`, which is
   correct, if faintly repetitive — that repetition is inherent to the two prefixes serving different
   purposes and is not a bug to fix here).
5. **No migration of already-stored secrets**, confirmed by the planning doc. An install upgrading past
   this change re-enters credentials under the new prefix (Windows Credential Manager target name,
   keychain service, or env var) the same way it would set one for the first time — no new command, no
   special-casing a stale old-prefix secret anywhere in the codebase.
6. **Check "secret not found" messaging** wherever `SecretStore.Resolve`/`TryResolve` failures surface
   to an operator (`DriverConnectionFactory`, `ConnectionsController`, anywhere else that resolves a
   secret ref) says plainly which ref/env var it was looking for. If it already does, this is a no-op;
   if it doesn't, fix the message as a small, disclosed part of this phase — not a new mechanism, per
   the planning doc's explicit allowance.

## What this phase should not do

- No migration mechanism for secrets stored under the old `datasync:`/un-prefixed `CLRKERNEL_SECRET_*`
  naming.
- No new UI, CLI surface, or messaging built around `CanPersist`, `FileSecretProvider`, or
  `AdoptFileSecrets()` — noted as real capabilities the upgrade brings, not this phase's job to adopt.
- No further touching of `DbDataSync:*`/`DbDataSync__*` configuration-section naming — unrelated to this
  package, already handled by phase 92.

## How to verify

- Full-solution `dotnet build` clean after the package bump — a major-version-feeling jump (0.9→0.11)
  in a real dependency is exactly where a removed/changed member breaks the build, which is the cheapest
  way to find one.
- A test (or an extension of an existing `SecretRefs`/`ConnectionsController` test) confirming
  `SecretStore.EnvName` is now used and reports the *new* prefix's naming (`DBDATASYNC_SECRET_...`), not
  the old hardcoded `CLRKERNEL_SECRET_...` — the two happen to share a provider-name convention today
  only because nothing had overridden it yet, so this needs an explicit assertion, not an unchanged one
  passing by accident.
- A test that `new SecretStore("DbDataSync", true)` at both construction sites actually resolves and
  stores under the new prefix (`SecretStore.Prefix` reports it; `ProviderNames`/behavior otherwise
  unchanged from before the bump).
- Full test suite (`Category!=Integration` and `Category=Integration`) green, or every failure traced
  to a pre-existing cause the same way every prior phase this session has done it.
- `datasync secret set`/the admin config screen's secret endpoint still work end to end against the new
  prefix (an integration test if one already exists for either path from phases 79/81; extend it rather
  than skip verifying this because it "should just work").
