# Phase 119 — NuGet search for libraries (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*NuGet search*, Decision 4. Depends on phase 118 (the Libraries screen this adds a box to). Does
not depend on 120 — search stands alone, producing a copyable install command until 120 adds the
button.

## Why

An operator adding an engine DbDataSync wasn't built with has to already know the exact NuGet
package id and an available version. A search box against the public NuGet index removes that —
without pulling a NuGet client or package-resolution logic into the host (the parent doc,
`nuget-loaded-drivers.md` §*Acquisition*, rejected a runtime NuGet client; a read-only search-index
query is a different thing).

## What this phase builds

### API — `GET /api/libraries/search?q=<term>`

- A plain `HttpClient` GET to `https://azuresearch-usnc.nuget.org/query?q=<term>&prerelease=false`
  (the NuGet v3 search service), response reshaped to
  `[{ id, description, latestVersion, versions: [string…], totalDownloads, verified }]`. No
  `NuGet.Protocol`, no dependency resolution — a search-index read only.
- `HttpClient` via `IHttpClientFactory`, a named client with a short timeout (a few seconds) and a
  DbDataSync user-agent.
- `[Authorize(Policies.Admin)]`.
- New config key **`DbDataSync:NuGetSearchEnabled`** (default `true`), documented in `CONFIG.md`
  alongside the other `DbDataSync:*` keys and surfaced on the Admin → Configuration screen like the
  rest. When `false`, the endpoint returns `503`/a structured "disabled" body without making the
  call.
- On a failed or timed-out upstream call: a structured "search unavailable" response, not a 500 —
  the UI degrades to manual entry.

### SPA — the Libraries screen

- A search box on `AdminLibrariesPage`. Results as rows: id, description, download count, a
  "verified publisher" mark, and a **required** version `<select>` (populated from `versions` —
  the "pinned, never latest" rule holds in the UI).
- Above the box, curated quick-add chips from `useKnownLibraries()` — picking one pre-fills the
  package id + factory type.
- A selected result + version yields a **copyable command**:
  `dbdatasync config library install <id> --version <v>` (the one-click Install button is phase
  120). For a non-curated result, the copy block carries the "you are choosing to trust this
  package's code" note that phase 120's confirmation dialog will make modal.
- When search is disabled or the call fails: the box is replaced by a manual "package id + version"
  entry that produces the same copyable command.

## How to verify when built

- New `tests/DbDataSync.Api.Tests/LibrarySearchTests.cs`: a stubbed `HttpMessageHandler` returns a
  captured NuGet search payload; the endpoint reshapes it correctly (ids, version lists, verified
  flag). `NuGetSearchEnabled=false` → the disabled response, and the handler is never called. An
  upstream 500 / timeout → the "unavailable" response, not a 500 from us.
- Optional `Category=Integration` test hitting the real index for one well-known package
  (`MySqlConnector`) — decide at build time whether the flakiness is worth it; the stub is the
  real coverage.
- Playwright: search box present when enabled, absent (manual entry shown) when
  `NuGetSearchEnabled=false`; a result row requires a version before the command appears.
- `npm run build` / `lint` clean.

## What this phase does not build

- Any install — no `POST`. The proxy is the only new network path, and it's a `GET`.
- Restoring or downloading a package.
- Caching search results server-side (the query is cheap and the UI can debounce).

## Open questions to resolve during implementation

- Whether to also surface the NuGet package's own "prefix reserved" / owner info to strengthen the
  curated-vs-found signal. Leaning: show `verified` and download count; that's enough for v1.
- Whether `q` should be passed straight through or constrained (e.g. always append
  `packagetype=dependency`). Leaning: pass through, let the operator search freely; the trust
  gate is at install, not search.
