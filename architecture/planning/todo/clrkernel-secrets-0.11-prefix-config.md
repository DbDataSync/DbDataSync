# ClrKernel.Core.Secrets 0.9.2 → 0.11.0: a configurable prefix, and what it means for an existing install

**Status: resolved, except the package's own API surface, which can't be verified from outside it. New
prefix and migration stance both decided below. Still depends on the concurrent DataSync→DbDataSync
rename landing in this checkout before a phase number is assigned — see note at the end.**

## What's there today, confirmed by reading the code

`ClrKernel.Core.Secrets` 0.9.2 is referenced in one project (`DataSync.Core.csproj:10`) but consumed
widely — `DataSync.Cli`, `DataSync.TaskRunner`, `DataSync.Api`, `DataSync.State`,
`DataSync.Drivers.Abstractions` all `using ClrKernel.Core.Secrets`. `SecretStore` is constructed in
exactly two places: `DataSyncHost.cs:78` (the API process, registered as a DI singleton) and
`RunExecutor`'s sibling `TaskRunner/Program.cs:21` (the runner, a separate process, its own instance).
Both currently call `new SecretStore(true)` with no prefix argument.

Two independent hardcoded naming conventions exist today, and only one of them is this package's:

- **`SecretRefs.cs`** (`DataSync.Core/Secrets/`) — this app's own naming scheme for
  `SecretStore` keys, namespaced `"datasync:"` because "the OS-native credential store is shared
  machine-wide with other apps that may use SecretStore too" (`SecretRefs.cs:4-5`). Two forms:
  `ForConnection` → `datasync:connection:<name>`, `ForAppSetting` → `datasync:config:<key>` (the one,
  fixed ref documented for `StateConnectionString`'s password — `SecretRefs.cs:11-19`).
- **`EnvironmentVariableFor`** (`SecretRefs.cs:28-32`) — the env var `SecretStore` itself falls back to
  when no OS keyring is available, built from the ref sanitized under a `CLRKERNEL_SECRET_` prefix.
  This one *is* the package's own convention, not this app's — the exact thing 0.11.0's new
  configurable-prefix feature presumably lets a host override.
- **Unrelated, and out of scope for this doc**: `DataSync:*`/`DataSync__*` — this app's own ASP.NET Core
  configuration-section naming (`InviteCommand.cs:38,44` reads `DataSync__StateEngine`/
  `DataSync:StateConnectionString`), nothing to do with `ClrKernel.Core.Secrets`. The rename may touch
  this too, but it's a separate concern from this package upgrade — don't conflate the two in scoping.

## Why this matters right now

The concurrent DataSync→DbDataSync rename is presumably the reason this upgrade is being planned now:
`SecretRefs.cs`'s `"datasync:"` namespace is exactly the kind of string a rename would otherwise require
hand-editing, and 0.11.0's configurable prefix is the mechanism to do it properly instead. Coordinate
with that effort rather than picking a new prefix unilaterally here — the new prefix should be whatever
the rename settles the product's name on, not a guess made in this doc.

## Resolved: the new prefix, and no migration

**New prefix: `DbDataSync`**, matching the rename. Applied with the same casing convention each existing
consumer already uses rather than pasted in verbatim everywhere: `SecretRefs.cs`'s key namespace stays
lowercase-with-colons (`dbdatasync:connection:<name>`, `dbdatasync:config:<key>`), and the env var
fallback keeps whatever casing the package's own convention uses today (`CLRKERNEL_SECRET_` was already
uppercase regardless of the ref's own casing, since `EnvironmentVariableFor` uppercases the whole
sanitized string — confirm 0.11.0's configurable version preserves that rather than taking the prefix
case-sensitively as given).

**No migration.** Confirmed explicitly: secrets stored under the old `datasync:`/`CLRKERNEL_SECRET_...`
names are not carried forward. An install upgrading past this change re-enters its credentials under the
new prefix — the same `datasync secret set`-equivalent action already exists for setting a secret in the
first place, so there is no new UI or command needed for this, just the expectation that it happens once
per install, at the same time as everything else the rename touches. Nothing in this change needs to
special-case a stale old-prefix secret in the codebase; it simply won't be found, and every path that
looks a secret up already has to handle "secret is missing" as a normal case (a fresh install has never
set any). Confirm that existing missing-secret handling produces a clear enough message on its own —
if it doesn't already say plainly which ref it was looking for, that's worth a small fix alongside this,
not a new migration mechanism.

## What's genuinely unverified

The exact 0.11.0 API for setting a custom prefix — a `SecretStore` constructor parameter, a separate
configuration call, environment-variable-only vs. also covering the "various password providers" the
package abstracts (Windows Credential Manager, macOS Keychain, Linux libsecret, presumably — confirm
which ones this package actually wraps before assuming "various" means all of them uniformly) — isn't
something to guess at from outside the package. Pull the actual 0.11.0 release and read its public API/
changelog before writing an implementation phase doc; this planning doc identifies the right questions
and the right files, not the package's exact new surface.

## What this update should not do

- Build any migration mechanism for old-prefix secrets — explicitly decided against.
- Touch the unrelated `DataSync:*`/`DataSync__*` configuration-section prefix — a different concern,
  possibly the rename's to handle separately, not this package upgrade's.

## Open questions, to resolve before an implementation phase doc exists

1. What does 0.11.0's actual API look like for setting the prefix — a `SecretStore` constructor
   parameter, a separate configuration call, environment-variable-only vs. also covering whichever
   "various password providers" the package abstracts (confirm the actual provider list, don't assume
   it means every OS-native store uniformly). Not verifiable from outside the package — pull the real
   0.11.0 release and read it before writing an implementation phase doc.
2. Whether `SecretRefs.cs` and its file path/namespace (`DataSync.Core/Secrets/`) themselves get renamed
   as part of the broader rename effort, or only the string values inside them change — affects whether
   this phase touches file paths or just literals. Check the state of the rename once it's landed here
   rather than assuming either way.

**Next step**: do not assign a phase number yet — the concurrent DataSync→DbDataSync rename (a separate
session, already underway per the user) hasn't landed in this checkout, and this change's file paths
(`SecretRefs.cs` lives under `DataSync.Core`) depend on how it does. Once it's landed and question 1
above is answered against the real package, this is ready for an implementation phase doc, numbered
against whatever `implementation/todo/`/`done/` looks like at that point.
