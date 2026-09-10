# Operator setup and config — the path to a working replication

**Status: proposal, not agreed.** Draft for review. Lower priority than
`app-and-service-setup.md` — that one has to land first, since an operator cannot do any of this
until the app is running and they can sign in.

Scope: everything a signed-in operator does, from an empty console to a replication that is keeping
a target in sync and that they can keep healthy. The deliverable is mostly **documentation and
in-app hints** — the product mechanics (connections, mappings, provisioning, runs) already exist and
work; what is thin is the guidance that gets a first-time operator through them without a wrong turn.

---

## Recommendation up front

1. **A "first replication" tutorial** in `docs/` — one linear walkthrough, source connection to a
   verified target, with the screenshots the Playwright golden-path suite already produces.
2. **Guided empty states.** `ConnectionsPage` and `ReplicationsPage` say "No connections yet." /
   "No replications yet." and stop. Replace with a one-line what-and-why plus the button, and — on
   the Replications page with zero replications *and* fewer than two connections — a pointer to add
   connections first.
3. **Surface the prerequisites an operator keeps hitting**: which permissions the connection's
   account actually needs, and that enabling Change Tracking / CDC on the source is a provisioning
   step (it is — `ProvisioningActions.EnableSourceChangeCapture`), not something they must do by
   hand out-of-band. When the app's own account lacks the rights, show the DDL to hand to a DBA.
4. **The operator side of drivers** (post-phase-109): the connection editor's engine picker becomes
   "whatever is installed"; an operator who needs an engine that is not there needs a clear "ask an
   admin to run `dbdatasync driver install`" rather than a missing option with no explanation.

---

## The journey, and where each step stands

### 1. Sign in

`SignInScreen` offers the methods the deployment configured (passkey always; Windows if groups are
set). An invited operator opens `/invite#<code>` (`InvitePage`), enrols a passkey, and is in.

- **Fine as is.** One note for the tutorial: an invite code is single-use and time-limited; an admin
  reissues with `dbdatasync invite` or the Invites screen.

### 2. Add connections

`Connections → New` (`ConnectionEditPage`, `ConnectionTestCard`): driver, host/port/database, auth
mode, **Test**.

Gaps:

- **Empty state** — "No connections yet." Should say what a connection *is* (a database DbDataSync
  reads from or writes to) and that a replication needs two.
- **Permissions** — Test proves the account can connect, not that it can do the job. A least-
  privilege source account needs `SELECT` + `VIEW CHANGE TRACKING` (Change Tracking) or the CDC
  role; a target account needs write + `CREATE TABLE` if provisioning will create it. Nothing states
  this until a run fails. Options: a doc section with the exact `GRANT`s per engine/reader; or Test
  doing a follow-up capability probe and reporting what it could not do (bigger).
- **Driver picker** (post-109) — reads `GET /api/drivers`. If the operator's engine is not listed:
  an inline "Need another database engine? An administrator installs it with `dbdatasync driver
  install`" with a link to the relevant doc, rather than the option simply not being there.

### 3. Create a replication

`Replications → New` (`ReplicationsPage`): name, source + target connection, schedule
(Continuous / Periodic), change-processing settings.

- **Empty state** — "No replications yet." Should say what a replication is and, if there are fewer
  than two connections, send them to Connections first.
- The schedule and parallelism defaults are reasonable; the tutorial should say "leave these" and
  link to the fuller explanation rather than making a first-timer choose.

### 4. Table mapping + provisioning

`TableMappingForm` and its tabs: source `schema.table` → target `schema.table`, column mapping
(auto-derived), transforms, natural key. Then `ProvisioningCard` / `ReplicationProvisioningPanel`
(phase 105 — one plan for the whole replication): a previewable plan covering
`enableSourceChangeCapture` and `createTargetTable` / `alterTargetTable`, applied deliberately.

- **This is the strongest part of the product and the least-documented.** The tutorial should walk
  it: add a mapping, open the provisioning panel, read the plan (it shows the `ALTER DATABASE … SET
  CHANGE_TRACKING = ON` and `CREATE TABLE` it will run), Apply.
- **When Apply is not available to the operator's account** — a least-privilege deployment where the
  app cannot `ALTER DATABASE` — the plan already carries the statements; the UI should make "copy
  this for your DBA" a first-class action on that panel, not something the operator reconstructs.
- Column-mapping and transform depth is a reference-doc topic, not a tutorial one — link out.

### 5. First run

`Run Now` → initial load → the live panel shows progress and the final `succeeded` with rows
read/written.

- Tutorial: trigger it, watch it, then look at Run History.
- An in-app hint on a replication that has **never run** and has a valid mapping: a subtle "Ready to
  run — press Run Now for the initial load" rather than an empty Runs panel.

### 6. Verify

A Verification check (`ChecksCard`) compares source and target row-by-row and stores the result.

- Tutorial closes here: add a check, run it, see it pass. This is what turns "I think it worked"
  into "it did."

### 7. Operate (day 2)

- **Monitoring tab** — lag, run history, hold/resume, per-mapping read intent (phases 100–103).
- **Backfill** — for target drift the incremental path cannot see (the README's `drift` scenario).
  A hint worth adding: when a Verification check *fails* on a mapping whose recent runs all
  succeeded, that is the backfill case — say so, with a link.
- **Version Control tab** — config is git; every connection/replication/mapping edit is a commit,
  diffable and revertible.

These are covered by an **operations reference** doc, not the tutorial.

---

## What to build

### Documentation — `docs/`

- **`first-replication.md`** — the linear tutorial above, MSSQL → MSSQL (what v1 supports), with
  screenshots. Generate them from the Playwright golden-path run rather than by hand — the suite
  already shoots `screenshots/golden-path/06-table-mappings-list`, `07/08-live-run`,
  `24-create-table-plan`, `25-mapping-preview`, `27-run-metrics`, etc. A small script copies the ones
  the doc references.
- **`connections.md`** — per driver: connection-string vs. host/port, auth modes, and the **exact
  `GRANT` statements** for a least-privilege source and target account. This is the single most
  reusable reference; support questions will land here.
- **`operations.md`** — monitoring, lag, hold/resume, backfill (and when to reach for it), config
  history and revert.

### In-app hints — `src/DbDataSync.Web/`

- Empty states on `ConnectionsPage` / `ReplicationsPage` — a component (`EmptyState`) taking a title,
  a sentence, and a primary action; the Replications one conditionally points at Connections.
- "Ready to run" / "no runs yet" hint on a replication detail with a valid mapping and zero runs.
- "This looks like drift — try a backfill" on a mapping whose latest Verification failed but whose
  recent runs succeeded.
- Provisioning panel: a "Copy DDL for a DBA" action whenever a plan is `NotSatisfied` and Apply is
  unavailable or has failed on a permissions error.
- Driver picker (post-109): the "ask an admin to install" affordance.

None of these are modal wizards — the product's style is a working screen with a hint on it, not a
guided flow that gets in the way on the second use.

---

## What this does not do

- Installing/configuring the app or its providers — `app-and-service-setup.md`.
- A step-by-step wizard that walls off the normal screens.
- Re-documenting column mappings, transforms, scripts, SCD2, segmenting strategies in the tutorial —
  those are reference material the tutorial links to.
- Changing how provisioning or runs work — only how they are explained and hinted.

---

## Open questions

1. **Screenshots in docs** — committed PNGs (large, but the repo already commits the Playwright
   screenshots) or generated at doc-build time? Leaning: a script that copies a named subset out of
   `screenshots/` into `docs/img/`, run in the same CI job, so they cannot drift from the UI.
2. **Permission probing on Test** vs. a doc table of `GRANT`s. Leaning: the doc table first (cheap,
   covers every case); a probe later if the doc does not stop the questions.
3. Whether the "first replication" tutorial should assume the dev-harness containers (reproducible,
   but dev-only) or a reader's own two databases. Leaning: their own, with a one-line "or use
   `tools/dev-harness up` if you just want to see it work."
4. How much the driver-picker affordance depends on phase 109 landing — the empty-state and
   prerequisite work is independent and can go first.
