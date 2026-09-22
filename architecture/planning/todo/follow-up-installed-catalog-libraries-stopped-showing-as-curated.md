# The library-id divergence fix broke the "curated" badge for exactly the id shape it introduced

**Status: open.** Found 2026-09-22 in CI run `35780881069` (job `106925769608`, commit `9b6eb2c`,
"A library's id is always its real package id — fix the actual divergence, not just its symptom"),
the run that verified that very fix. Filed per `architecture/implementation/README.md`'s "Follow-up
work gets its own doc, not a paragraph." Not one of the eight items in
[the CI flake catalogue](follow-up-what-ci-should-do-about-a-known-flake.md) — this is a real product
regression the retry mechanics happened to obscure, not test-harness noise.

## What failed

`playwright` → `tests/admin-drivers-libraries.spec.ts` — three failures in one run, but only one of
them is the actual bug:

```
2) [chromium] › admin: Drivers and Libraries › Libraries tab lists the installed library as
   resolving, curated, and used by the descriptor

   Error: expect(locator).toBeVisible() failed
   Locator: getByTestId('admin-library-row-MySqlConnector').getByTestId('admin-library-curated-MySqlConnector')
   Expected: visible
   Timeout: 5000ms
   Error: element(s) not found

     62 |     await expect(row).toBeVisible()
     63 |     await expect(row.getByTestId(`admin-library-resolves-${KNOWN_DRIVER_LIBRARY}`)).toContainText('resolves')
   > 64 |     await expect(row.getByTestId(`admin-library-curated-${KNOWN_DRIVER_LIBRARY}`)).toBeVisible()
```

The row for the just-installed `MySqlConnector` library exists and correctly reports "resolves" — only
the "vetted"/curated badge is missing. That is the real, first failure in the run (test 3 of the
`describe.serial` block). Everything before it passed.

## The other two "failures" in the same run are an artifact of the first one, not separate bugs

`admin-drivers-libraries.spec.ts` uses `test.describe.serial`, and `playwright.config.ts` sets
`retries: 2` on CI. When test 3 fails, Playwright retries the **whole serial block from test 1** — but
tests 1 and 2 already ran for real on the first attempt and their side effects (installing the
`mysql.generic` driver and its library) are not undone before the retry. So on retry #1, test 1's own
first assertion —

```
await expect(page.getByTestId(`admin-known-driver-${KNOWN_DRIVER_ID}`)).toBeVisible()
```

— now fails, correctly: `mysql.generic` really is already installed (from the first attempt), and the
catalog rightly stops offering it a second time (the same test's own later assertion, line 32-33, says
so explicitly: *"Already installed now, so the catalog panel stops offering to add a second copy"*).
Retry #2 fails the same way for the same reason. The run's summary reads as three distinct failures;
there is exactly one bug, and the other two are the retry mechanism re-running a test whose
precondition its own predecessor's side effects already invalidated.

**This is worth fixing on its own, separately from the curated-badge bug**, because it is actively
misleading: a human (or an agent) reading only the last failure in the log — test 1, "no such element"
on the catalog row — is pointed at completely the wrong place. The real defect, and its actual
assertion, is buried in the middle of the run as failure 3 of 3, immediately followed and screened off
by two runs' worth of `[WebServer]` request logging from the retries.

## The cause, in one function

`LibrariesService.List()` computes the `Curated` flag like this:

```csharp
// src/DbDataSync.Api/Services/LibrariesService.cs:40
KnownLibraries.TryGetById(m.Id) is not null,
```

and `KnownLibraries.TryGetById` matches only the catalog's own short id, never the package id:

```csharp
// src/DbDataSync.Libraries/KnownLibraries.cs:106-107
public static LibraryCatalogEntry? TryGetById(string id) =>
    All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
```

Before `9b6eb2c`, `DriversController.InstallFromCatalog` installed a catalog pick under the catalog's
short id (`"mysql-connector"`), so `TryGetById("mysql-connector")` found the entry and `Curated` came
back `true`. `9b6eb2c` deliberately changed that: every catalog install now resolves to the entry's real
`PackageId` (`"MySqlConnector"`) first, exactly as `follow-up-library-install-paths-disagree-on-the-
resulting-library-id.md` prescribed, so the installed library's id, the registry key, and the
`driver.yaml`'s `library:` field all agree. `LibrariesService.List()` was never updated to match — it
still only recognizes the catalog-id shape, so a library installed the *new*, correct way now reads as
uncurated.

**This is the identical ambiguity `LibraryRegistry` already has a fix for, just not applied here.**
`LibraryRegistry.NotInstalledMessage` — added the same week, for the "did you mean" message
(`ed8d32a`) — already looks up both shapes:

```csharp
// src/DbDataSync.Libraries/LibraryRegistry.cs:143-144
var entry = KnownLibraries.TryGetById(id)
    ?? KnownLibraries.All.FirstOrDefault(e => string.Equals(e.PackageId, id, StringComparison.OrdinalIgnoreCase));
```

`LibrariesService.cs:40` is the one caller of `KnownLibraries.TryGetById` that never got the same
fallback — not because it's a different problem, but because the id-divergence fix's own verification
(the `driver-authoring.spec.ts` flake) never exercised the Libraries tab's curated badge, only the
install paths themselves.

## Fix shape

Add the same fallback `KnownLibraries` itself should probably own, rather than leaving each caller to
reinvent it: a `TryGetByIdOrPackageId(string id)` (or similarly named) method that tries `TryGetById`
first and falls back to matching `PackageId`, the same two-step `LibraryRegistry.NotInstalledMessage`
already does inline. Then:

- `LibrariesService.cs:40`'s `Curated` computation calls the new method instead of the bare
  `TryGetById` — this is the actual bug fix.
- `LibraryRegistry.cs:143-144` can call the same method instead of repeating the `??` fallback inline,
  which turns two independent implementations of "does this id name a known library, either shape" into
  one, so a third caller doesn't get the same gap a third time.

Separately, worth its own small fix: make `test.describe.serial` blocks whose tests have real,
non-idempotent side effects (an install, a file write) either tolerate being re-entered from the top on
retry, or — more simply — read the *first* failure in a serial block's retry history as the one that
matters, which is a Playwright reporting question, not something this repo's test code can fix alone.
At minimum, a comment on `admin-drivers-libraries.spec.ts`'s `describe.serial` noting that a failure
here can cascade into two misleading retry failures would have saved the time this doc's own
investigation spent separating the real bug from its own echo.

## How to verify when closed

- `Libraries tab lists the installed library as resolving, curated, and used by the descriptor` passes
  on the first attempt, with no retries needed for this spec file.
- A unit or API-level test asserts `LibrariesService.List()` reports `Curated: true` for a library
  installed under its `PackageId` (the current, correct install shape) — not just the pre-`9b6eb2c`
  catalog-id shape, which is all today's coverage exercises.
- `KnownLibraries.TryGetById` gains no new callers duplicating the `?? PackageId` fallback inline; both
  existing ones (`LibrariesService`, `LibraryRegistry`) go through one shared method.
