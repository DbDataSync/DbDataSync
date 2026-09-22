# Follow-up: three UX gaps in the well-known library picker

Found reviewing `LibraryFindPanel` (`src/DbDataSync.Web/src/components/LibraryFindPanel.tsx`) after it
shipped embedded in both `LibrariesPage` and `DriverEditPage` (`driver-yaml-authoring-ui.md`). Not live
bugs — the flow works — but three real, checked gaps in how a curated ("well-known") pick behaves.
Documented to fix later, not fixed here.

## 1. The curated chip's label buries the real package name inside the display name

`QuickAddChip` renders `entry.displayName` as one line of button text (`LibraryFindPanel.tsx:212-225`).
`KnownLibraries.All` (`src/DbDataSync.Libraries/KnownLibraries.cs`) deliberately writes that field as
`"MySQL / MariaDB (MySqlConnector)"`, `"SQL Server (Microsoft.Data.SqlClient)"`, etc. — human label plus
the real NuGet package id in parentheses, per that field's own doc comment ("`DisplayName`... not the bare
package id"). Today both halves render identically: same size, same weight, one line.

**Fix**: split `displayName` on the trailing `(...)`, render the human label as the chip's title and the
parenthesized part as a second line underneath in a smaller, grey/dim style — `.hint`/`.dim` already exist
as classes in this app (used elsewhere in this same file, e.g. `formatDownloads`'s "downloads" label) and
are the natural fit, not a new style. Needs a small parse helper (`KnownLibrarySummary.displayName` is one
string, not two fields) — cleaner to add that split in `KnownLibraries.cs` itself as a second field
(`PackageLabel`? ) than to regex it apart in the SPA, but either works; a backend split keeps the "what's
the human label vs. the package name" decision in one place instead of two different display names ever
drifting.

## 2. No known-good version is offered for a curated pick — the operator has to type one

Picking a `QuickAddChip` calls `pick({ id: entry.packageId, version: '', versionLocked: false,
factoryType: '' })` (`LibraryFindPanel.tsx:106`) — version always starts empty, and `versionLocked: false`
means `InstallCommand` renders a free-text input (`LibraryFindPanel.tsx:306-318`), same as a fully manual
entry. The operator has to already know (or go look up) a version that works.

**The known-good version already exists and isn't wired up.** `LibraryCatalogEntry.PinnedVersion`
(`KnownLibraries.cs:17-22`) is real, per-entry, and actively used server-side — it's the exact version
`internal build-catalog-cache` restores into the runtime image and the only version
`LibraryInstaller.InstallOrDeferAsync` can serve with no SDK present. It just never reaches the picker:
`GET /api/known-libraries` (`LibrariesController.cs:49-51`) projects `KnownLibrarySummary` with only four
fields (`Id`, `DisplayName`, `Description`, `PackageId`) — `PinnedVersion` is dropped at that DTO boundary,
mirroring the exact same gap `driver-yaml-authoring-ui.md`'s own Status line already flagged for
`GET /api/known-drivers` ("only 4 metadata fields").

**Two real options, not mutually exclusive:**
- Add `PinnedVersion` to `KnownLibrarySummary` and have `QuickAddChip`'s pick pre-fill `version` with it
  (`versionLocked: true` or a locked-but-editable state) — cheapest, and it's already the version proven
  to work (it's what the in-image cache restores).
- Let the operator pick from NuGet's real published versions instead of typing one — `SearchResultRow`
  already does this for a searched (non-curated) result (`result.versions`, a `<select>`,
  `LibraryFindPanel.tsx:257-270`), so the version-dropdown UI and the NuGet-backed data source
  (`useSearchLibraries`) both already exist; a curated pick just never calls into that path today because
  `QuickAddChip`'s `onPick` goes straight to `pick(...)` instead of a search.

Pre-filling the pinned version is the smaller change and covers the common case (a curated entry has one
known-tested version); a version dropdown on top of that is worth adding if operators actually want a
newer minor/patch than the pinned one — not clear that's needed until someone asks for it.

## 3. Installing gives no feedback beyond a static "Installing…" label

`InstallCommand`'s button text is `installing ? 'Installing…' : 'Install'` (`LibraryFindPanel.tsx:353`) —
no elapsed time, no spinner, no indication of what's actually happening underneath, for however long the
install takes.

**What's actually happening underneath, checked, not assumed**: `POST /api/libraries` → `LibraryInstaller`
shells out to `dotnet publish` (`LibraryInstaller.cs:220-232`) via `RunDotnetAsync`
(`LibraryInstaller.cs:235-258`), which redirects stdout/stderr and awaits `ReadToEndAsync` on both — the
full output is only available after the process exits. Today that output is discarded on success and
included in the thrown exception's message on failure (`LibraryInstaller.cs:228-232`) — a synchronous,
one-shot HTTP call with no intermediate signal, blocking however long `dotnet publish` takes (can be real
time for a package with a large dependency closure and a cold NuGet cache).

**No existing pattern in this app to reuse for this** — grepped for `EventSource`/SSE/streaming-response
usage across the API and SPA; the only `IAsyncEnumerable` usage in the codebase is internal data
streaming (readers/staging), nothing that streams process output or progress to the browser. This would be
new plumbing, not a small change:

- A minimal version that ships without touching the backend: replace the static label with a spinner plus
  a running elapsed-time counter (`setInterval` from `install.isPending`'s transition to `true`) — gives
  the operator "it's still working, N seconds so far" instead of a frozen button. Cheap, real value, no API
  change.
- The fuller version — actual `dotnet publish` output while it runs — needs the backend to stream rather
  than `ReadToEndAsync`-then-return: either Server-Sent Events on a new endpoint, or a
  start-then-poll-a-job-status shape (`POST /api/libraries` kicks off the install, returns a job id,
  `GET /api/libraries/install-jobs/{id}` returns buffered output-so-far + status). Real design work — which
  shape fits this app's existing patterns better isn't decided here.

Worth doing the cheap spinner+timer version regardless of whether the streaming version ever gets built —
they're not blocking each other.

## Where this applies

Both call sites of `LibraryFindPanel` get all three for free once fixed here — `LibrariesPage` and the
connection-library picker embedded in `DriverEditPage` (`driver-yaml-authoring-ui.md` §2) — it's the same
component, not duplicated.
