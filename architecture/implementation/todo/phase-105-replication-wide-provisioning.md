# Phase 105 — one provisioning script for the whole replication

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/source-auto-provisioning-and-replication-wide-scripts.md`,
resolved 2026-09-04.

Setting up a replication with forty table mappings currently means opening forty Setup cards and
pressing Apply forty times, or copying forty scripts. This aggregates every mapping's provisioning into
one page: grouped by server, deduplicated, individually selectable, copyable, and runnable as a batch.

**No auto-provisioning of source change capture.** That was the other half of the original ask and it
was dropped — this page answers the same problem without anyone pre-authorising a category of DDL.
Phase 25's rule and phase 82's answer to `change-tracking-odbc-jdbc.md` stand: DbDataSync runs no DDL
on a source without a human saying so. The planning doc keeps the full reasoning, because a rejected
flag gets re-proposed unless the reason is written down.

## What this phase will build

### 1. An aggregated plan

`ProvisioningService` gains `GetReplicationPlanAsync(replicationName, ct)`, returning steps from every
table mapping, processed four ways.

**Deduplicated by `(connectionName, database, commandText)`.** This is the part the existing design did
not anticipate. Phase 25 deliberately put the database-level step in *every* mapping's plan and leaned
on the live state check to make it disappear once any mapping had run it:

> include it in every plan and let the live state check make it disappear once any mapping has run it

That works when looking at one mapping. It does not survive aggregation: all N plans are computed
against the same pre-state in one pass, so `ALTER DATABASE Sales SET CHANGE_TRACKING = ON` appears N
times. Noisy to run, and not something to hand a DBA.

Dedup is on the **statement**, not the title — two mappings could title the same statement differently
in future. Each surviving step records **which mappings contributed it**, so a single database-level
row can explain why it is there.

**Grouped by connection and database.** A replication's source and target are different servers, so one
copyable block would be runnable nowhere. Each group is one script for one server — which is also the
unit an operator hands to whoever administers it.

**Ordered within a group: `Database` scope before `Table` scope.** `ProvisioningStep.Scope` already
carries this. `ENABLE CHANGE_TRACKING` on a table fails if the database has not had it turned on, and a
plain concatenate-then-dedupe does not preserve that. Within a scope, first-appearance order.
`createTargetTable` and `alterTargetTable` need no ordering rule between them — `ProvisioningService`
already treats them as mutually exclusive for a given table.

**Mappings whose plan is `Unsupported` are listed with their reasons**, not omitted. A script covering
thirty-eight of forty mappings that looks complete is worse than one that says what it left out.

### 2. Stable step identity, because Apply re-plans

Phase 25 established that Apply re-plans and executes what it just planned rather than trusting a
plan the client holds — "applying stale DDL is exactly the failure the preview is meant to prevent."
Selection has to survive that round trip, so a step needs an id **derived from its content**, not a
position: a hash of `(connectionName, database, scope, commandText)`.

The client sends selected ids; the server re-plans and matches. **An id that no longer exists in the
fresh plan is reported as no longer needed, not as a failure** — it means someone else satisfied that
step between preview and Apply, which is a normal outcome on a shared database and not an error.

### 3. Selection, and prerequisites

Every statement is individually included or excluded.

- **All ticked on load; the choice is ephemeral.** The plan is recomputed against live state on every
  visit, so a remembered exclusion could hide a step that has since become necessary for a different
  reason. Nothing is persisted to config.
- **Copy and Run both act on the selected set**, so what was copied and what would run are the same
  thing.
- **A selected step whose prerequisite is excluded warns, and still runs.** The rule is declared, not
  inferred by a dependency graph: *a `Table`-scope step depends on the `Database`-scope steps in its own
  group.* That is exactly the Change Tracking case and the only real one. Blocking would be wrong — the
  commonest reason to untick the database statement is that a DBA already ran it out of band, which
  DbDataSync cannot see from a plan computed a moment earlier.
- **Steps the target auto-flags would apply anyway are listed, ticked, and labelled automatic.**
  `CreateTargetTableIfMissing` and `AlterTargetTableColumnsIfMissingOrChanged` still exist, so some
  target steps will be applied by `RunExecutor` on the next pass regardless. They stay in the list
  because the page is the complete truth about what the replication needs: a copied script that
  withholds steps because the app *might* do them later produces a half-configured database if it never
  runs, and the DBA receiving it has no way to know anything was left out.

### 4. Batch apply

`POST api/replications/{name}/provisioning/apply` with the selected ids.

**Not a transaction, and the doc says so.** DDL across two servers cannot be atomic. Per-step outcomes
are reported and the batch **stops at the first failure**, with the remaining steps reported as not
attempted — later steps commonly depend on earlier ones, so continuing would bury the real error under
a cascade of consequences.

### 5. Connections

`GetPlansAsync` opens a source connection *and* a target connection per mapping and disposes both.
Forty mappings would be eighty connections and eighty rounds of catalog checks.

The aggregate opens **one connection per distinct `(connectionName, database)`** — usually two or three
in total — and passes it to the planners. That means extracting the connection acquisition out of
`PlanEnableSourceChangeCaptureAsync` and `PlanTargetAsync` so both the per-mapping and aggregate paths
call the **same** planning code with a connection handed in. Two planning paths that could diverge is
the bug this avoids: the per-mapping Setup card and this page must never disagree about what a mapping
needs.

Computed on demand behind a spinner. **If any bound on how many mappings are planned is ever
introduced, the page states how many were left out** — a silent cap reads as "this is everything".

### 6. The page

The existing **Overview → Provisioning** tab, which today holds only the two target auto-flag settings.
It is titled "Target provisioning"; once source steps appear beside them that stops being true and it
becomes **"Provisioning"**. The settings stay where they are, with the aggregate below them.

Per group: a header naming connection · database and which side it is, a **Copy** button, and the step
rows — checkbox, title, scope badge, `AUTOMATIC` badge where applicable, the contributing mappings, and
the statement itself. **Run selected** applies across all groups. Excluded mappings and their reasons
render below.

## How it will be verified

**Unit** (`DbDataSync.Api.Tests`)
- N mappings on one source database collapse to **one** `ALTER DATABASE` step, and it records every
  contributing mapping — the regression the whole dedup exists for
- `Database`-scope steps precede `Table`-scope steps in every group after the merge
- steps land in the right group when a replication's mappings span more than one target connection
- an `Unsupported` mapping appears in the excluded list with its reason and contributes no steps
- **one connection per endpoint, not per mapping** — asserted with a counting connection factory, since
  this is invisible in behaviour and would silently regress
- step ids are stable across two plans of unchanged state, and change when the statement changes

**API**
- apply with a subset runs exactly those statements
- apply with an id absent from the fresh plan reports it as no longer needed, not as a failure
- apply stops at the first failure and reports the remainder as not attempted
- a selected `Table` step whose `Database` prerequisite was not selected still runs, and the result
  carries the warning

**E2E** — the page renders one group per server; unticking the database-level step surfaces the warning
on dependent steps; Copy yields only the selected statements; Run reports per-step outcomes and the
plan refreshes afterwards.

## Decisions

- **Dedup on the statement within `(connection, database)`**, recording contributing mappings.
- **Group by server; there is no single script**, because there is no single server.
- **Content-derived step ids**, because Apply re-plans and must not trust a held plan.
- **Warn on excluded prerequisites, never block.**
- **Auto-applied steps are shown and ticked**, labelled rather than hidden.
- **Stop at first failure**; the batch is explicitly not atomic.
- **One connection per endpoint**, via shared planning code rather than a second implementation.

## Out of scope

- **Auto-provisioning source change capture.** Dropped — see the planning doc.
- **Persisting exclusions.** The plan is recomputed each visit; a remembered exclusion can outlive its
  reason.
- **A general dependency graph.** The declared Database→Table rule covers the real case; anything more
  is speculation about steps that do not exist yet.
- **Making the batch transactional.** Not possible across servers.
- **Provisioning across more than one replication at a time.**

## Open questions to resolve during implementation

- **Does Copy emit a header comment** naming the replication, server and generation time? Useful to a
  DBA receiving a script out of context, and it makes the copied text no longer purely executable —
  though every engine in scope accepts `--` comments.
- **Two operators applying at once.** Apply re-plans, so the second gets a shorter plan rather than a
  conflict, and the stale-id path already reports it cleanly. Worth confirming that is genuinely all
  that is needed rather than assuming it.
- **Whether the excluded-mappings list should link to each mapping's own Setup card.** Probably yes,
  but it depends on how much of the reason the aggregate can show inline.
