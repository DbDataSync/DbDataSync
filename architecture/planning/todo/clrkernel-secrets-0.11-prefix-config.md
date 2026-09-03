# ClrKernel.Core.Secrets 0.9.2 → 0.11.0: a configurable prefix, and what it means for an existing install

**Status: draft — real open questions, not resolved. Depends on the concurrent DataSync→DbDataSync
rename; do not assign an implementation phase number until that lands (see note at the end).**

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

## The real, unresolved question: what happens to secrets already stored under the old prefix

This is the part that isn't a metrics figure or a cached timestamp — it's a password. Every existing
install has real credentials sitting in an OS keyring (or its environment-variable fallback) under
`datasync:connection:<name>` and `datasync:config:<key>` keys, or under `CLRKERNEL_SECRET_...`
environment variables built from them. Changing the prefix without a migration path means every one of
those installs loses access to its own stored credentials on upgrade — not a degraded feature, a hard
failure to connect to every configured source and target at once.

This needs one of:

- **A one-time migration** that reads every secret under the old prefix and rewrites it under the new
  one, run explicitly (a CLI command, likely — matching how `datasync secret set` already exists as an
  explicit, operator-run action rather than something automatic) rather than silently at process start.
- **Dual-read, single-write**: `SecretStore` (or `SecretRefs`) reads the new prefix first, falls back to
  the old one if not found, and only ever writes under the new one — so nothing breaks immediately, and
  secrets migrate naturally as they're rewritten, with an explicit migration command still worth having
  for anyone who wants to close out the transition deliberately rather than let it happen implicitly.
- **A hard cutover with a clear, upfront failure** — matching the tone phase 91 just took for the
  metadata cache: fail loud, name exactly which secret refs are missing and why, tell the operator to
  run the migration. Consistent with this session's recent preference for explicit, operator-driven
  transitions over silent ones — but a **materially higher-stakes** version of it, since the earlier
  case degrades a run; this one can take down every configured connection in an install at once. Worth
  explicit confirmation before assuming this is the wanted shape, given the stakes are a step up from
  where that preference was last stated.

## What's genuinely unverified

The exact 0.11.0 API for setting a custom prefix — a `SecretStore` constructor parameter, a separate
configuration call, environment-variable-only vs. also covering the "various password providers" the
package abstracts (Windows Credential Manager, macOS Keychain, Linux libsecret, presumably — confirm
which ones this package actually wraps before assuming "various" means all of them uniformly) — isn't
something to guess at from outside the package. Pull the actual 0.11.0 release and read its public API/
changelog before writing an implementation phase doc; this planning doc identifies the right questions
and the right files, not the package's exact new surface.

## What this update should not do

- Pick a specific new prefix string in this doc — that's the rename effort's decision, not this one's.
- Silently migrate or drop secrets on upgrade without an explicit, confirmed transition plan — see above.
- Touch the unrelated `DataSync:*`/`DataSync__*` configuration-section prefix — a different concern,
  possibly the rename's to handle separately, not this package upgrade's.

## Open questions, to resolve before an implementation phase doc exists

1. What does 0.11.0's actual API look like for setting the prefix — confirm against the real package.
2. What should the new prefix be — depends on where the DataSync→DbDataSync rename lands.
3. Migration strategy for secrets already stored under the old prefix — one-time explicit migration,
   dual-read-single-write, or a confirmed hard cutover with clear failure messaging. This is the
   question this doc most needs a real answer to before implementation starts.
4. Does "various password providers" mean this package wraps more than the OS-native keyring + env-var
   fallback already known about — confirm the actual provider list in 0.11.0.

**Next step**: do not assign a phase number yet — the user has a concurrent agent renaming the project
from DataSync to DbDataSync, and this plan's prefix choice and file paths (`SecretRefs.cs` lives under
`DataSync.Core`) both depend on how that lands. Once it's landed and the open questions above are
answered, this becomes ready for an implementation phase doc, numbered against whatever
`implementation/todo/`/`done/` looks like at that point.
