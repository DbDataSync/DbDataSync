# Phase 177M — Show the resolved connection string and JDBC URI in the console

**Status**: Not started — design only.
**Plan reference**: `architecture/planning/todo/jdbc-url-template-and-connection-testing.md` (full
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

## How to verify

- A failed test against a connection whose URL the driver rejects (phase 175M's `acceptsURL` check) shows
  the resolved connection string/JDBC URI in the card, not just the bare error string — confirming the
  diagnostic value this whole design chain exists to deliver actually reaches an operator's screen.
- The redacted marker (not the real password) is what renders, confirmed against a connection using
  `AuthMode.SqlAuth` with a real credential configured.
- `outsideProperties` renders nothing (no empty section, no stray heading) when the map is empty or absent,
  for every non-JDBC driver's test result.
- Run this against a real instance in a browser before calling it done — this is a UI change, and the task
  guidance is explicit that type-checking/build passing is not the same as confirming the feature works.
