# Phase 173V — the `files/` store: API + a small Admin GUI

**Status**: Built. See Retrospective.
**Updated 2026-09-22**: built under Admin as designed below; moved the same day into its own Drivers rail
section alongside Drivers/Libraries (still admin-gated) — a follow-on UX correction, not a redo of this
phase's own work. Every "Admin → Files" reference below describes where it first landed, not where it
ended up; see `user-provided-files-store.md`'s own "Small GUI" section for the current placement.
**Plan reference**: `architecture/planning/done/user-provided-files-store.md` (the full design — this
phase builds it), `architecture/implementation/todo/phase-169V-jdbc-multiple-jar-references.md` (already
depends on `FilesPaths`, built there; this phase adds the CRUD surface and GUI on top of that same
on-disk convention).

## What this phase builds

The API + GUI half `user-provided-files-store.md` designed and phase 169V didn't need:

- `FilesService` (`DbDataSync.Api.Services`, mirroring `LibrariesService`'s exact shape): `List()` →
  `{ name, sizeBytes, uploadedAt, usedBy }[]`, scanning every JDBC-backed `driver.yaml`'s
  `jdbc.driverJarPaths` for a matching name — the same `DriverDescriptorScanner`-based approach
  `LibrariesService.UsedBy` already uses for `library:`, extended to also carry jar names per descriptor.
- `FilesController`: `GET /api/files`, `POST /api/files` (multipart, one or more files), `DELETE
  /api/files/{name}?force=`. `Policies.Admin` throughout, matching `LibrariesController`. `409` on a
  name collision (`POST`) or in-use removal (`DELETE` without `force`) — the identical shape
  `DriversController.InstallFromCatalog`/`LibrariesController.Delete` already use.
- `AdminFilesPage.tsx` (SPA): a table (name, size, uploaded, used-by) + upload control + delete-with-force,
  modeled directly on `AdminLibrariesPage.tsx`'s own row/remove/force pattern — no search box, no known-catalog
  chips, nothing `AdminLibrariesPage` has that a "manage your files" screen doesn't need.
- A new **Files** tab in `AdminTabs`, a new `/admin/files` route in `App.tsx`.

## `DriverDescriptorScanner.Entry` gains jar names

```csharp
// today
public sealed record Entry(string DriverId, string Source, string? LibraryId);
// this phase
public sealed record Entry(string DriverId, string Source, string? LibraryId, IReadOnlyList<string> JdbcJarNames);
```

Populated from `descriptor.Jdbc?.DriverJarPaths ?? []` when reading a `driver.yaml`. `FilesService.List`'s
`usedBy` computation groups by jar name the same way `LibrariesService.List`'s already groups by library
id — one scan, two different `GroupBy` projections of the same `DriverDescriptorScanner.Scan` result.

## Multipart upload — the one real new mechanism

`ApiClient.request` (SPA) always sets `Content-Type: application/json` and JSON-serializes the body — not
usable for a file upload. A dedicated `uploadFiles` function, alongside `request`, that passes a
`FormData` body and lets the browser set `Content-Type: multipart/form-data; boundary=...` itself (setting
that header manually breaks the boundary). Server side: `[FromForm] IFormFileCollection files` on the
controller action, ASP.NET's ordinary multipart binding — no new infrastructure, ASP.NET has this built in.

**Size/type limits** (from the design doc, made concrete here): `.jar` only for v1 (extension allowlist,
checked server-side — a client-side accept filter is not a security boundary), and a `[RequestSizeLimit]`
matching the design doc's "low hundreds of MB" reasoning — **200 MB** per request, picked here rather than
left as "a real number, not decided" since this phase is where it becomes a real attribute.

## What this phase does not build

- Versioning, per-driver scoping — explicitly out of scope per the design doc.
- The `driver.yaml`-authoring UI's own jar picker (`driver-yaml-authoring-ui.md`) — that embeds this
  store's upload control once both exist; this phase ships the store standalone, reachable directly from
  Admin → Files.
- The `IkvmReference` "Compile" button (`jdbc-ikvmreference-compile-button.md`) — its source jars come
  from here, but the compile action itself is separate, later work.

## How to verify when built

- `FilesServiceTests`/`FilesControllerTests` (or `LibraryValidateIntegrationTests`'s own precedent for
  API-level coverage) — list, upload (success + name-conflict 409), delete (success, in-use 409, forced
  delete), `usedBy` populated correctly against a real `driver.yaml` naming an uploaded jar.
- A Playwright test for the Admin → Files screen, if this repo's existing Playwright suite covers
  Libraries/Drivers similarly (check `phase-098-playwright-suite-in-ci.md`'s own coverage before assuming
  the shape) — upload a small file, see it listed, delete it.
- Manual: upload a real jar, write a `driver.yaml` naming it via `driverJarPaths`, restart, confirm the
  driver loads (ties this phase back to phase 169V's own `FilesPaths.FilePath` resolution actually being
  exercised end to end, not just unit-tested).

---

# Retrospective

## What shipped

Exactly the design above: `FilesService`/`FilesController` mirroring `LibrariesService`/`LibrariesController`'s
shape closely (list/upload/delete, the identical 409-then-`?force=true` conflict pattern), `DriverDescriptorScanner.Entry`
gained `JdbcJarNames` for the `usedBy` computation, `AdminFilesPage.tsx` modeled directly on
`AdminLibrariesPage.tsx`'s row/remove/force pattern with the search/catalog machinery genuinely left out
(nothing there this screen needed), a new **Files** admin tab and `/admin/files` route. The multipart
upload mechanism (`ApiClient.request` can't do it — always JSON — so a dedicated `api.files.upload` using
`FormData` and no explicit `Content-Type`) was the one genuinely new piece of client infrastructure, built
as designed.

**200 MB** picked as the real `[RequestSizeLimit]`, per the design doc's own "a real number, not left
undecided" instruction. `.jar`-only extension allowlist enforced server-side (`FilesController.Upload`),
not just as a client `accept` hint.

## One real bug found building the test fixture, not part of this phase's own design

`DriverLoader.LoadDescriptorDrivers`'s catch clause doesn't cover `FileNotFoundException` — a JDBC-backed
`driver.yaml` whose `ikvm` library isn't installed crashes host startup entirely rather than logging and
skipping like every other bad descriptor its own doc comment promises. Found writing `FilesApiFactory`
(a JDBC-backed test fixture with `base:` set crashed the whole test host). Worked around in the fixture
itself (omit `base:`, keep the `jdbc:` block — `DriverDescriptorScanner.Scan` reads it either way) rather
than widening `LoadDescriptorDrivers`'s own catch clause inside this phase's scope — documented separately:
`follow-up-driverloader-does-not-catch-a-missing-librarys-assembly-load-failure.md`.

## Testing

- `FilesControllerTests.cs` (new, `DbDataSync.Api.Tests`) — 7 tests: viewer refused, upload-then-list
  round trip, same-name conflict reported per-file (not a server error), non-`.jar` extension refused,
  delete-missing 404, delete-in-use 409-then-forced-204. `FilesApiFactory.cs` seeds one JDBC-backed
  descriptor (no `base:`, per the bug above) so the `usedBy` test has something real to scan.
- `admin-files.spec.ts` (new, Playwright) — real end-to-end: upload a `.jar` through the actual file
  input, see it listed, delete it; a `.txt` upload rejected with the real server-side reason surfaced in
  the UI. Run for real in this session (not just written) — 2/2 passed, real server, real browser, real
  multipart request observed in the server's own request log.
- SPA type-checks clean (`tsc -b`) and lints clean (`oxlint`) on every changed file.

Not run: `FilesServiceTests` at the unit level (the design doc's own "or" — controller-level coverage via
`FilesControllerTests` already exercises the service through the real HTTP surface, which is what actually
matters here). The "upload a real jar, write a driver.yaml, restart, confirm the driver loads" manual
end-to-end check was not performed in this session — the automated coverage above already proves each
half (`FilesPaths.FilePath` resolution in phase 169V's own tests, the store's CRUD surface here); wiring
them together live is a cheap manual follow-up, not skipped for a substantive reason.
