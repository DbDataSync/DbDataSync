# Phase 149 — Update docs for MySQL/MariaDB and Oracle drivers

**Status**: Planned, not started. **Depends on phases 147 and 148 actually shipping first** — this phase
documents shipped behavior, not the plan for it. If either phase's build deviates from its own doc (a
Kind name changes, a boolean convention flips, a reader gets dropped), this phase follows the real code,
the same "verify against source, not assumption" posture used throughout the planning work that produced
147/148 in the first place.

**Plan reference**: `architecture/implementation/done/phase-147-mysql-mariadb-driver-and-trigger-audit.md`,
`architecture/implementation/todo/phase-148-oracle-driver-trigger-audit-and-flashback.md`.

## Why a separate phase, not folded into 147/148

Both driver phases are already substantial. Splitting the docs update out keeps each of those phases
scoped to "build and verify the driver," and lets this phase batch both engines' documentation into one
pass — most of the touched files (the reader-kinds table, the drivers table, the feature matrix) need
edits for both engines at once anyway; doing it twice would mean re-opening the same tables mid-review.

## What this phase will update

- **`docs/replication-concepts.md`** — the reader-kinds table gets three new rows: MySQL/MariaDB
  trigger-audit, Oracle trigger-audit, Oracle Flashback Version Query (noting it as the one reader with
  no `IPositionAcknowledging` step, and its `IPositionCapturing` support). Cross-check the existing
  `KeyReconcile`/`DeleteGuard`/segmenting sections for any driver-specific caveats worth a footnote (e.g.
  MySQL's `RenderTieSafeRowLimit` decision, once actually made in 147, if it constrains segment ordering
  on ties).
- **`docs/drivers-and-libraries.md`** — the built-in drivers table (currently MsSql/Postgres/DuckDb only)
  gains MySQL/MariaDB and Oracle rows: default port, `RequiredLibraryId`, supported reader/writer kinds,
  `AuthMode` support. The worked example in that doc is Postgres-flavored; consider whether a second
  worked example (e.g. Oracle wallet auth via `AuthMode.None` + TNS name) earns its place or would just
  duplicate the Postgres one's structure without adding information.
- **`README.md`** — the feature-support matrix (currently SQL Server/PostgreSQL/DuckDB columns only)
  gains MySQL/MariaDB and Oracle columns, reflecting each engine's actual reader/writer/change-tracking
  support as shipped, not as planned.
- **`docs/state-database.md`** — check for any driver-enumeration references (e.g. a list of known
  `DriverType` values) that need the two new entries; likely a small or no-op edit, confirmed rather than
  assumed.
- **`architecture/planning/done/additional-database-drivers.md`** — its own tracking table marks
  MySQL/Oracle as "not yet written"; update to point at phases 147/148 as built, following this repo's
  own convention for a resolved planning doc's outcome note.
- **`architecture/planning/done/change-tracking-strategies.md`** — the umbrella doc's "Suggested order"
  and "Outcome" sections are a historical record of a decision already made; check whether they need a
  pointer added to 147/148 as the eventual implementations, without rewriting the historical reasoning
  itself.

## What this phase does not do

- Does not touch `architecture/implementation/done/phase-147-...`/`phase-148-...` themselves (their own
  retrospectives are where build-time findings get recorded, not this phase).
- Does not re-litigate any decision phase 147/148 made (boolean literal convention, 12c+ floor, etc.) —
  documents what shipped.

## How to verify when built

- Every new/changed doc cross-link resolves (no dangling reference to a driver capability that wasn't
  actually built).
- A fresh read-through of `docs/drivers-and-libraries.md` and `docs/replication-concepts.md` end-to-end
  for internal consistency — table rows match prose, prose matches shipped `DriverType`/Kind names.
- Breadcrumb/nav ordering in `README.md`/`docs/getting-started.md`/`docs/install.md`/`docs/development.md`
  unaffected (no new page added by this phase, only existing pages edited) — confirm this is still true
  once the edits are drafted, rather than assumed from this doc's own scope.
