# Phase 149 — Update docs for MySQL/MariaDB and Oracle drivers

**Status**: Built and verified 2026-09-17. See the Retrospective below.
**Depended on phases 147 and 148 actually shipping first** — this phase
documents shipped behavior, not the plan for it. If either phase's build deviates from its own doc (a
Kind name changes, a boolean convention flips, a reader gets dropped), this phase follows the real code,
the same "verify against source, not assumption" posture used throughout the planning work that produced
147/148 in the first place.

**Plan reference**: `architecture/implementation/done/phase-147-mysql-mariadb-driver-and-trigger-audit.md`,
`architecture/implementation/done/phase-148-oracle-driver-trigger-audit-and-flashback.md`.

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

# Retrospective

No new pages added; five existing files edited: `docs/replication-concepts.md`, `docs/drivers-and-libraries.md`,
`README.md`, `architecture/planning/done/additional-database-drivers.md`,
`architecture/planning/done/change-tracking-strategies.md`. `docs/state-database.md` needed no change,
confirmed rather than assumed — the state store's own engine choice (`StateEngine`: `Sqlite`/`MsSql`/`Postgres`)
is a completely separate mechanism from replication drivers, untouched by phases 147/148, and was never a
candidate for MySQL/Oracle support. Breadcrumb nav in every page checked unaffected, as predicted.

## The plan's own predicted shape didn't survive contact with the real Kind names

The plan said the reader-kinds table would get "three new rows: MySQL/MariaDB trigger-audit, Oracle
trigger-audit, Oracle Flashback Version Query." It got **one** new row. MySQL's and Oracle's trigger-audit
options are not new Kinds at all — both drivers register the exact same `GenericDriverKinds.TriggerAudit`
Postgres and SQL Server already use, so the existing `TriggerAudit` row's own "When to use" cell just
needed widening to name the two new engines, not a duplicate row per engine. Only `OracleFlashback` (a
genuinely new, Oracle-only Kind) needed a row of its own. Exactly the kind of plan-vs-shipped divergence
this phase's own header warned to expect and follow the real code on.

## Finding: the MySQL descriptor worked example became a stale, load-bearing false claim

Not anticipated by the plan at all. `docs/drivers-and-libraries.md`'s "Worked example: MySQL, with no
rebuild" walkthrough — written before phase 147 existed — opened by claiming MySQL was "an engine no part
of DbDataSync's own compiled code references at all." Phase 147 made that false: MySQL is now a compiled,
built-in driver with real provisioning support, and the walkthrough was still telling a reader to reach
for the slower descriptor path with no mention that a better one now exists. Fixed by reframing the
section's own intent — it now says plainly that a real MySQL replication should use the built-in driver,
and that the walkthrough keeps MySQL as its example only because `mysql.generic` is the one starter
template that exists, not because it is still the recommended path. The mechanics themselves needed no
change; only the claim about why anyone would run them did.

## What else changed, briefly

- `docs/replication-concepts.md`: the `TriggerAudit` row widened; one new `OracleFlashback` row; a
  footnote naming MySQL/MariaDB's real, unresolved `Watermark` tie-safety gap (phase 147's own Finding 2)
  at the one place an operator combining `Watermark` with a row cap would need to know it.
- `docs/drivers-and-libraries.md`: built-in drivers table gains `MySql`/`Oracle` rows; every "three
  built-ins" reference in the page (there were three separate ones, not one) updated to five.
- `README.md`: feature matrix gains MySQL/MariaDB and Oracle columns, matching the established
  per-engine-distinctive-feature convention (`Native (...)` only where an engine actually has a native
  mechanism — Oracle's Flashback qualifies, MySQL's doesn't, matching how Postgres's own row was already
  written before this phase touched it) rather than inventing a new convention for the two new columns.
- `architecture/planning/done/additional-database-drivers.md`: its own outcome table now points phases
  23–24 at the real phases 147/148, and its "metadata browsing is per-engine" open question is noted
  resolved for MySQL/Oracle specifically (still open for ODBC/JDBC).
- `architecture/planning/done/change-tracking-strategies.md`: its outcome table gains a note that
  MySQL/Oracle shipped in the reverse order its own "Suggested order" section proposed (MySQL first, not
  Oracle) — stated plainly as an order difference with no argument behind it, not corrected or hidden.

## Verification

- Every cross-reference added resolves to a real file at the path given (checked by eye, not tooling —
  this is a docs-only phase with no test suite of its own).
- Read through both `docs/drivers-and-libraries.md` and `docs/replication-concepts.md` end to end after
  editing; found and fixed the stale MySQL worked-example claim this way, not by following the plan's own
  checklist item mechanically.
- No source code changed in this phase; nothing to build or test.
