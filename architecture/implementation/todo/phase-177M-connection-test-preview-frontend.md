# Phase 177M — Show the resolved connection string and JDBC URI in the console

**Status**: Done, 2026-09-22. Types extended, `ConnectionTestCard` gained the disclosure, verified in a
real browser against a real MySQL server through `mysql.generic` (the only preview-capable driver
reachable in this test environment — every hand-written built-in doesn't implement
`IConnectionPreviewer`), redaction confirmed rendering, graceful absence confirmed for a non-preview
driver (MsSql, via the full `golden-path.spec.ts` run). The JDBC-specific case (`jdbcUri` populated, a
rejected-URL failure) has no browser-level proof — no fixture in this suite opens a real JDBC connection
through the console, the same gap phases 175M/176M already named.
**Plan reference**: `architecture/planning/done/jdbc-url-template-and-connection-testing.md` (full
rationale). **Depends on phase 176M** — this phase only renders fields `ConnectionTestReport` doesn't carry
yet.

## Why

`ConnectionTestCard`/`TestResult` (`src/DbDataSync.Web/src/pages/connection-edit/ConnectionTestCard.tsx:39-64`)
already renders `report.serverVersion`, `report.error`, and `report.libraryWarning` — the exact pattern a
new field slots into. Once phase 176M's `ResolvedConnectionString`/`JdbcUri`/`OutsideProperties` exist on
the wire, an operator testing a connection has no way to see them unless this screen shows them, and the
whole point of adding them server-side was for an operator to see what actually got resolved and attempted
— most of all on a failed test, which is exactly when this card is already showing `report.error`.

## What changes

- `src/DbDataSync.Web/src/api/types.ts`: extend the `ConnectionTestReport` type with
  `resolvedConnectionString?: string`, `jdbcUri?: string`, `outsideProperties?: Record<string, string>`,
  matching phase 176M's wire shape.
- `TestResult` (`ConnectionTestCard.tsx:39-64`) gains a new section, styled consistently with the existing
  `dim`/`hint` conventions already used for `serverVersion`/`libraryWarning` in the same component — a
  labeled, monospace display of the resolved connection string; `jdbcUri` shown only when present (a plain
  ADO.NET driver's report never carries one); `outsideProperties` shown as a compact key/value list only
  when non-empty. Collapsed/secondary by default (this is diagnostic detail, not the headline "reachable/
  unreachable" status the card leads with) — a `<details>` disclosure or an existing collapsible pattern
  already used elsewhere in this page, not a new always-open block competing with `report.error` for
  attention.
- No change to `ConnectionsPage.tsx`'s own use of `ConnectionTestReport` unless it already renders more than
  a pass/fail summary — check before assuming it needs the same treatment; this phase's primary target is
  the edit page's "Last test" card, where an operator is actively diagnosing a specific connection.

## What this does not do

- Does not add any new operator-facing input for a JDBC URL template, connection-string-keys mapping, or
  any other authoring surface — `architecture/planning/todo/driver-yaml-authoring-ui.md` owns that, and it's
  a `driver.yaml`-authoring concern, not a per-connection Test Connection concern.
- Does not add redaction logic client-side — phase 176M redacts before the response ever leaves the API;
  this phase only renders what it's given, trusting it's already safe to show.
- Does not add a new Playwright spec beyond extending whatever already covers the existing "Last test" card
  flow, if one exists — check `tests/` for existing connection-test E2E coverage before deciding whether a
  new spec or an extended assertion is the right shape.

## What changed from the design, found building it

- **No existing collapsible pattern to reuse** — checked, per the design's own instruction, before
  picking one: `connection-edit/` has exactly one component file (`ConnectionTestCard.tsx` itself) and
  this app has zero existing `<details>` usage anywhere. Used a plain native `<details>`/`<summary>` —
  the design's own named fallback, not a new bespoke toggle component.
- **`ConnectionsPage.tsx` confirmed unchanged, not assumed** — its `Reachability` component
  (`ConnectionsPage.tsx:84`) renders only a dot plus "reachable · Nms"/"unreachable", never
  `report.error` or anything richer, so the design's own conditional ("unless it already renders more
  than a pass/fail summary") resolved to no change there.
- **No existing E2E coverage of the "Last test" card allowed simple extension** — checked; the closest
  candidate (`golden-path.spec.ts`'s test 13) only ever exercises MsSql, which doesn't implement
  `IConnectionPreviewer` at all, so extending it could only prove graceful absence, not the fields
  actually rendering. Added a new, self-contained spec (`connection-test-preview.spec.ts`) instead — it
  installs `mysql.generic` itself (doesn't depend on `admin-drivers-libraries.spec.ts` having already run
  in the same invocation) and tests a real connection against the real MySQL container
  (`docker-compose.yml`, port 13306) to get a driver that actually implements the preview capability.

## How to verify

- A failed test against a connection whose URL the driver rejects (phase 175M's `acceptsURL` check) shows
  the resolved connection string/JDBC URI in the card, not just the bare error string — **not covered**:
  needs a real JDBC connection through the console, which has no fixture in this test suite (same gap
  phases 175M/176M already named).
- The redacted marker (not the real password) is what renders, confirmed against a connection using
  `AuthMode.SqlAuth` with a real credential configured — `connection-test-preview.spec.ts`, against a real
  MySQL server, screenshot at `screenshots/connection-test-preview/resolved-connection-details.png`.
- `outsideProperties` renders nothing (no empty section, no stray heading) when the map is empty or absent
  — same spec asserts `connection-resolved-jdbc-uri`/`connection-resolved-properties` have zero count for
  the MySQL (non-JDBC) case; the whole disclosure itself renders nothing for a non-preview driver
  (MsSql), confirmed via the full `golden-path.spec.ts` run (45/45 passed) and its own `16-connection-test.png`.
- Run this against a real instance in a browser before calling it done — done: both the new spec and the
  full `golden-path.spec.ts` ran against the real webServer + real database containers, not just
  type-checked.
