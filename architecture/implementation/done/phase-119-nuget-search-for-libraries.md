# Phase 119 — NuGet search for libraries

**Status**: Done.
**Plan reference**: `architecture/planning/done/drivers-and-libraries-in-the-web-ui.md`
§*NuGet search*, Decision 4. Depended on phase 118 (the Libraries screen this adds a box to). Does not
depend on 120 — search stands alone, producing a copyable install command until 120 adds the button.

## What this built

### API — `GET /api/libraries/search?q=<term>`

- New `LibrarySearchService` (`DbDataSync.Api/Services`): a plain `HttpClient` GET to
  `https://azuresearch-usnc.nuget.org/query?q=<term>&prerelease=false`, reshaped into
  `{ status, results: [{ id, description, latestVersion, versions, totalDownloads, verified }] | null }`.
  No `NuGet.Protocol`, no dependency resolution.
- `HttpClient` via a named `IHttpClientFactory` client (`LibrarySearchService.HttpClientName`), a
  5-second timeout, and a `DbDataSync-LibrarySearch/1.0` user agent — registered in `DbDataSyncHost.cs`.
- New config key **`DbDataSync:NuGetSearchEnabled`** (default `true`) on `ApiOptions`, documented in
  CONFIG.md and added to `AdminConfigService`'s key table (so it shows up on Admin → Configuration like
  every other key, editable there too).
- Added to `LibrariesController` (not a new controller — the plan doc's own §*NuGet search* frames this
  as part of the Libraries surface). `[Authorize(Policies.Admin)]`.
- **Three response states, not two**: `"ok"` (search ran), `"disabled"` (`NuGetSearchEnabled` is
  false — the endpoint answers `503` without making the call), `"unavailable"` (the call was made but
  the upstream returned a non-2xx or the request timed out/failed — still a `200`, never a `500`).

### SPA — the Libraries screen

- `AdminLibrariesPage.tsx` gained a `LibraryFindPanel`: quick-add chips from `useKnownLibraries()`
  (filtered to curated libraries not already installed) above a NuGet search box, or — when a silent
  on-mount probe shows search doesn't work — a manual package-id/version entry in its place.
- A search result row shows id, description, download count (`1.2M`/`3.4K`-style formatting), a
  "verified" pill, a "vetted" pill for a curated id, and a required version `<select>`.
- Any path (a chip, a search result + version, or manual entry) converges on one `InstallCommand`
  block: the copyable `dbdatasync config library install <id> --version <v>` command, plus — for a
  non-curated id — the "you are choosing to trust this package's code" note the plan doc calls for.
  There is no Install button; copying is the only action (120 adds the button).
- `api/client.ts` + `api/hooks.ts`: `useSearchLibraries()`, a mutation (not a cached query — the same
  term typed twice should still hit the live index, not a stale cache entry).

## How it was verified

- New `tests/DbDataSync.Api.Tests/LibrarySearchTests.cs`: a stubbed `HttpMessageHandler`
  (`StubNuGetSearchHandler`, wired in via a new `LibrarySearchApiFactory`) returns a captured NuGet
  search payload — the endpoint reshapes ids, version lists, download counts and the verified flag
  correctly. `NuGetSearchEnabled=false` → `503` with `status: "disabled"`, and the handler is never
  called (asserted directly). An upstream `500` and a simulated timeout (`TaskCanceledException` from
  the handler) both → `status: "unavailable"`, `200`, never `500`.
- New `LibrarySearchIntegrationTests` (`Category=Integration`) hits the real NuGet index for
  `MySqlConnector` (the parent doc's own canary) — decided, per the doc's own open question, to keep
  this rather than rely solely on the stub, since this sandbox has real outbound network access and the
  stub can't catch a real schema drift in the NuGet v3 search response.
- New Playwright `library-search.spec.ts`: searching for `MySqlConnector` against the **real** index
  finds it, shows no install command until a version is picked, and shows one with no trust warning
  once one is (MySqlConnector is curated); a quick-add chip for `npgsql` (curated, not seeded by the
  fixture repo) pre-fills its package id and needs a typed-in version. The `NuGetSearchEnabled=false`
  SPA path is **not** exercised in Playwright — see Decisions.
- Full `Category!=Integration` .NET suite green; `npm run build`/`npm run lint` clean.

## Decisions made

- **A `string?` query parameter, not `string`.** `[FromQuery] string q` tripped `[ApiController]`'s
  automatic "non-nullable reference type parameter is implicitly required" model validation, so the
  on-mount availability probe (an empty `q`, by design — see below) got a `400` instead of ever reaching
  `LibrarySearchService`. Found by the first Playwright run against the real endpoint (the stubbed unit
  tests never called with an empty `q`, so they didn't catch it). Fixed by making the parameter
  nullable and defaulting to `""` in the method body.
- **An empty-query probe on mount** decides whether the SPA shows the search box or the manual-entry
  fallback, rather than only discovering "search doesn't work" after an operator types something and
  gets refused — not explicitly asked for by the plan doc, but the natural reading of "when search is
  disabled or the call fails, the box is replaced by manual entry": an operator shouldn't have to
  trigger the failure themselves to learn that.
- **The `disabled`-deployment SPA path is not covered by Playwright.** This suite's fixture stands up
  exactly one API configuration per run (see phase 118's own Decisions on why the scratch repo has to be
  seeded before the API process starts) — flipping `NuGetSearchEnabled` per test would need a second,
  differently-configured API instance, which is out of proportion to what this one client-side branch
  (`status !== 'ok'` → show manual entry) needs. It's covered from the other side instead:
  `LibrarySearchTests` proves the API answers `"disabled"` correctly, and `library-search.spec.ts`'s
  "enabled" path exercises the same `searchWorks` boolean that branch depends on.
- **A `versionLocked` flag, not "is `version` non-empty."** The first draft used `!version` to decide
  whether `InstallCommand` should render its own editable version box — which meant the box vanished
  the instant an operator typed its first character into it, since `version` stopped being empty right
  then. Caught by writing the quick-add-chip Playwright test, not by the unit-level checks (nothing
  else drives a keystroke-by-keystroke re-render). Fixed by tracking whether a version came from a
  definite source (a search result's `<select>`, or manual entry's own field before "Use") separately
  from the version string itself.

## What's explicitly out of scope / not built

- Any install — no `POST`. The search proxy is the only new network path, and it's a `GET`.
- Restoring or downloading a package.
- Caching search results server-side — the query is cheap and results aren't reused across requests.
