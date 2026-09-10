# Phase 121 — a runtime-only image with a pre-built catalog cache

**Status**: Done.
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*Follow-on work* → *A runtime-only image + a pre-built catalog cache*. Depends on phase 117 (the
catalogs) and phase 120 (the SDK-based default image and the install endpoints) — both confirmed
already in `done/` before starting.

## What this built

### `KnownLibraries.LibraryCatalogEntry.PinnedVersion` (new field)

The plan doc assumed each catalog entry already had a pinned version to build a cache at; it didn't —
adding one was necessary infrastructure this phase resolved rather than a design gap to stop on. Picked
from versions already proven to restore and work elsewhere in this repo where one existed (MySqlConnector
2.4.0, Microsoft.Data.SqlClient 7.0.2, Npgsql 9.0.3, Microsoft.Data.Sqlite 10.0.11 — all already used by
`DbDataSync.Drivers.*`/`DbDataSync.State`'s own `.csproj`s or this arc's tests); the remaining three
(Oracle.ManagedDataAccess.Core, System.Data.Odbc, FirebirdSql.Data.FirebirdClient) had no prior reference
in this repo, so each was verified restorable for real (`dotnet publish`, net10.0, linux-x64) before
picking 23.9.1 / 10.0.0 / 10.3.1.

### `SdkAvailability` (new, `DbDataSync.Libraries`)

`HasSdk()` — cached, real answer for this process. Same walk-up phase 123's `SystemdService.
ResolveDotnetRoot()` already verified empirically: three `Parent` steps up from `RuntimeEnvironment.
GetRuntimeDirectory()` is the dotnet root; an `sdk/` directory there, non-empty, means the SDK is
present. `HasSdk(string? dotnetRootOverride)` is the pure, testable form — a test builds a real temporary
directory shaped like a runtime-only or an SDK install rather than needing two actual dotnet
installations to prove both branches.

### `LibraryInstaller.InstallOrDeferAsync` (new)

The single place every caller (CLI, both API controllers) now routes a library install through:

- **SDK present** (`hasSdkOverride ?? SdkAvailability.HasSdk()`): exactly `InstallAsync` — a real
  restore, phase 122's reflection-assist included. Unchanged behavior for every deployment except the
  new runtime-only image.
- **No SDK, package + version match a `KnownLibraries` entry's `PinnedVersion` exactly**: copies from
  the in-image cache (`LibraryInstaller.DefaultCacheRoot`, `/app/library-cache`, or an override) instead
  of restoring. No network, no SDK.
- **No SDK, no cache hit**: `factoryType` must already be known (explicit or a `KnownLibraries` guess —
  reflection-assist can't run without a restore to scan) or this throws immediately. Otherwise only
  `library.json` is written; the library is left `PendingRestore`.

`SyncAsync` needed **no change** to "complete a pending restore" as the plan doc asked: it already
restores into `lib/` unconditionally, whether or not one was there before, so running it against a
pending manifest (real `factoryType`, no `lib/` yet) restores for the first time exactly like re-syncing
an ordinary library.

### `dbdatasync internal build-catalog-cache <out-dir>` (new, hidden CLI command)

Restores every `KnownLibraries` entry at its `PinnedVersion` into `<out-dir>`, using
`LibraryInstaller.InstallAsync` directly — the cache is written in exactly the same `libraries/<id>/`
shape a repo's own directory has, so `InstallOrDeferAsync`'s read side reuses the identical
`LibraryPaths` helpers with no special-casing. Absent from `Help.Print()` and every user-facing doc —
the Dockerfile is the only caller.

### `POST /api/libraries` and `POST /api/drivers/from-catalog`

Both now call `InstallOrDeferAsync` instead of `InstallAsync`. `from-catalog`'s own addition: on a
`PendingRestore` outcome (a version that doesn't match the cache's pinned one, on a host with no SDK),
the library is still written and registered (so it shows up correctly on the Libraries screen) but the
driver descriptor is **not** written — refusing with a clear 400 rather than leaving a descriptor
pointing at a library that cannot resolve yet.

### `LibrarySummary.PendingRestore` (new field) + the Libraries admin screen

`LibrariesService.List()` derives it from disk (`library.json` present, `lib/` absent) rather than
storing it — no new state to keep in sync with reality. The admin screen's status column renders
"pending restore" (with a title naming the fix) in place of the resolves/does-not-resolve dot when set;
`config library list` (CLI) does the same.

### The Dockerfile — three stages now, not two

- `build` (unchanged, plus one new `RUN`): after publishing the CLI, runs `internal
  build-catalog-cache /app/library-cache` while the SDK and the just-published binary are both still
  present in this stage.
- **`runtime`** (new, named, `mcr.microsoft.com/dotnet/aspnet:10.0`): the slim image — same `ENV`/
  `VOLUME`/`WORKDIR`/`EXPOSE`/`HEALTHCHECK`/`ENTRYPOINT` as the default, `COPY`s the same `/app/`
  (published binary + catalog cache) from `build`. Built with `docker build --target runtime -t
  dbdatasync:<v>-runtime .` — deliberately not the last stage in the file (see below).
- **The default stage** (`mcr.microsoft.com/dotnet/sdk:10.0`, unchanged in substance) stays the true
  last stage in the file on purpose, with a comment saying why: `docker build .` with no `--target`
  resolves to whichever stage is positionally last, and phase 120 already decided the SDK image is the
  default — moving `runtime` after it would silently flip that the next time someone edits the file.
- The catalog cache is copied into **both** final images, resolving the plan doc's own open question
  ("worth shipping in the SDK image too?") as yes: measured total size across all seven entries is a
  few megabytes (Oracle's the largest at ~6MB), so there's no real cost to giving the default image the
  same no-network fast path for a catalog install.

### `.github/workflows/ci.yml` — the `package` job

Now also builds `--target runtime`, asserts `dotnet --list-sdks` is empty on it (`docker run --rm
--entrypoint dotnet ... --list-sdks`), starts it, and waits on the same health-check loop the default
image already gets. Still gated to a `release/v*` tag push, same as the rest of that job.

### Docs

`src/DbDataSync.Cli/README.md`'s container section gained a paragraph on the runtime-only alternative
and what it can and can't install with no SDK.

## How it was verified

- **`SdkAvailabilityTests`** (5 tests): the real sandbox reports `true`; a hand-built temp directory
  shaped like an SDK install (`sdk/10.0.100/`) reports `true`; one shaped like a runtime-only install
  (`shared/Microsoft.AspNetCore.App/...`, no `sdk/`) reports `false`; an *empty* `sdk/` directory (no
  version subfolders) also reports `false`; a nonexistent root reports `false`.
- **`LibraryInstallOrDeferTests`** (5 tests, `hasSdkOverride` driving every branch since this sandbox
  always has the real SDK): SDK present → behaves exactly like `InstallAsync`; no SDK + the pinned
  catalog version → copies from a real one-entry cache (built once in `InitializeAsync` via a real
  restore) and the resulting factory really resolves through `LibraryRegistry`; no SDK + a *different*
  version of a catalog package → `PendingRestore`, manifest written, no `lib/`; no SDK + a non-catalog
  package → `PendingRestore`; no SDK + no cache hit + no `factoryType` → throws naming "no SDK".
- **`InternalCommandTests`** (2 tests): `build-catalog-cache` restores all seven real `KnownLibraries`
  entries at their pinned versions — manifest, factory type, and package version all match, and each
  restored at least one real `.dll` — the phase doc's own "build-time assertion" that the cache matches
  the catalog; `mysql-connector`'s cached entry resolves as a real factory through `LibraryRegistry`.
- **`LibraryInstallTests.PendingRestore_IsReportedOnGetLibraries`** (API): a manifest written directly
  to disk with no `lib/` is reported `pendingRestore: true, resolves: false` by `GET /api/libraries`.
- **Real Docker builds, both images, this sandbox's own Docker**:
  - `docker build -t dbdatasync:phase121-default .` — succeeds, including the new catalog-cache
    `RUN` step (all seven entries restored during the build, ~14s).
  - `docker build --target runtime -t dbdatasync:phase121-runtime .` — succeeds, reusing the cached
    `build` stage layers.
  - `docker run --rm --entrypoint dotnet dbdatasync:phase121-runtime --list-sdks` → empty output
    (exit 0); the same against `dbdatasync:phase121-default` → lists `10.0.401`.
  - **End-to-end on the running runtime-only container** (`DbDataSync__Auth__Disabled=true` for a
    scriptable smoke test): `POST /api/drivers/from-catalog {"knownDriverId":"mysql.generic","version":
    "2.4.0"}` succeeds with no network reachable for NuGet and no SDK in the container — `GET
    /api/libraries` afterward shows `resolves: true, pendingRestore: false`. Attached the container to
    the real `mysql:9` container's Docker network and created a real connection through the API
    (`PUT /api/connections/...`) pointed at it; `POST .../test` returned `succeeded: true, serverVersion:
    "9.7.2"` — a real round trip through the cache-copied `MySqlConnector.dll`, never restored via
    `dotnet publish` in this container at all. Restarted the container: `GET /api/drivers` still shows
    `mysql.generic` as `source: "descriptor"`, and the library still resolves — "the driver loads after
    a restart" holds. (A second connection test after restart hit an unrelated, pre-existing limitation
    of the in-memory secret store used for this ad-hoc manual test — restarting the process loses an
    in-memory-only secret regardless of this phase; not a library/driver-loading problem, and not
    something a real deployment's persistent secret store would hit.)
- `dotnet build DbDataSync.slnx -c Release` clean. `dotnet test` on `DbDataSync.Cli.Tests` (110,
  up from 108 before this phase) and the targeted `DbDataSync.Libraries.Tests`/`DbDataSync.Api.Tests`
  suites all green.
- `npm run build` / `npm run lint` on `DbDataSync.Web` clean; no new findings from the
  `AdminLibrariesPage.tsx`/`types.ts` changes.
- Cleaned up every test container and the two locally-tagged images (`phase121-default`,
  `phase121-runtime`) afterward — nothing was left running on the shared Docker daemon beyond this
  verification.

## Decisions made

- **`PinnedVersion` added to `LibraryCatalogEntry`** — the plan doc's design implicitly required this
  ("at its catalog-pinned version") without ever introducing it; resolved as necessary infrastructure,
  not a design ambiguity to stop on, the same way phase 117 resolved its own smaller catalog-id
  ambiguity.
- **The cache is a plain directory glob, reusing `LibraryInstaller`/`LibraryPaths` verbatim** — not a
  new archive format or manifest shape. `internal build-catalog-cache` and `InstallOrDeferAsync`'s
  read side agree by construction because they both go through the same helpers a normal repo's
  `libraries/` directory already uses.
- **The catalog cache ships in both final images** — the plan doc's own open question, resolved yes,
  backed by a real measurement (a few MB total) rather than a guess.
- **`from-catalog`'s `PendingRestore` outcome still writes and registers the library, but refuses the
  descriptor** — leaves the Libraries screen telling the truth about what's on disk without pretending
  the driver is usable yet. An operator who fixes the pending library (pinned version, or `config
  library sync` elsewhere) and retries from-catalog will find the library already "installed" (skipping
  the install branch) and proceed straight to writing the descriptor — a deliberate, minimal recovery
  path rather than new retry-tracking state.
- **`InstallOrDeferAsync` takes a `hasSdkOverride` parameter** — the same test-only-seam pattern phase
  123 established for `Environment.ProcessPath`, needed here because this sandbox always has the real
  SDK and so could never naturally exercise the no-SDK branches otherwise.
- **The `runtime` Dockerfile stage sits *before* the default SDK stage, not after** — Docker's implicit
  default target is whichever stage is positionally last; keeping the SDK stage last (with a comment
  explaining why) is what stops a future edit from silently flipping phase 120's "SDK is the default"
  decision.

## What this does not build

- Dropping the SDK from the *default* image — phase 120's decision stands unchanged; `runtime` is an
  alternative, built only when asked for.
- A cache for compiled-driver packages or arbitrary non-catalog libraries — exactly the curated set,
  as planned.
- Multi-version caching — one pinned version per catalog entry, as planned.
- Any change to `docker-compose.app.yml` — it still builds the (default) context with no `--target`,
  unaffected by this phase; a runtime-only compose variant wasn't asked for.

## Open questions — resolved

1. **Image naming** — resolved: `-runtime` suffix on the alternative; the default keeps its existing
   unsuffixed tag, per phase 120's own commitment.
2. **Whether the catalog cache is worth shipping in the SDK image too** — resolved yes (see above).
3. **Size of the full catalog closure** — resolved: small (single-digit MB per entry, Oracle largest at
   ~6MB), measured for real rather than guessed; no reason to cache only a subset.
