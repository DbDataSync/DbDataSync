# Provisioning: auto-enabling source change capture, and one script for the whole replication

**Status: resolved 2026-09-04 — see Outcome at the end.** Two asks from 2026-09-04. One of them was
dropped in the discussion, and the analysis below is kept as the record of why.

## The asks

1. **Auto-provision the source system's change processing**, if the operator wants it — the same
   opt-in shape the target already has.
2. **Aggregate every table mapping's provisioning steps across a replication**, so the operator can
   copy the whole script and run it by hand, or run it as one batch.

## What is there today

**Two target-side auto flags**, nullable bools on `ProvisioningConfig`, resolved replication → mapping
by `ProvisioningResolution`:

- `CreateTargetTableIfMissing` — only when the table is absent entirely
- `AlterTargetTableColumnsIfMissingOrChanged` — additive and modifying, never `DROP`

Both are applied by `RunExecutor.EnsureTargetTableProvisionedAsync`, once per pass before the segment
loop, logging every statement at Info.

**No source-side auto flag, deliberately.** Phase 25 states it plainly — *"`enableSourceChangeCapture`
has no automatic mode at any setting"* — and phase 82 recorded the same answer to the product question
`change-tracking-odbc-jdbc.md` had been carrying: **no**, DbDataSync does not run DDL on a source
without an explicit human Apply.

**Provisioning is per-mapping, everywhere.** The controller is routed
`api/replications/{r}/table-mappings/{m}/provisioning`, and `ProvisioningService.GetPlansAsync` takes
one mapping, opens a source connection and a target connection, plans both sides, and disposes them.

**A replication-level provisioning tab already exists** — Overview → Provisioning
(`TargetProvisioningTab`) — but it holds only the two default *settings*. It shows no plans and no
scripts.

## 1. Auto-provisioning the source

This is a reversal of a decision taken twice, and it should be recorded as one rather than slipped in
as a third flag beside two that look just like it. Opt-in and off by default is genuinely different
from silent — but the target flags and a source flag are not the same kind of permission, for three
reasons.

### The three source actions have wildly different blast radius

`enableSourceChangeCapture` is one action name covering plans that are not comparable:

| | scope | cost if wrong |
| --- | --- | --- |
| `ALTER TABLE … ENABLE CHANGE_TRACKING` | one table | small, additive, reversible |
| `ALTER DATABASE … SET CHANGE_TRACKING = ON`, `SET ALLOW_SNAPSHOT_ISOLATION ON` | **the whole database** | changes a setting shared by every application on it |
| `CREATE TRIGGER` + shadow table (`TriggerAuditPlan`) | one table, **on every write, forever** | a write-path latency cost on somebody else's OLTP source |

**A single boolean authorising all three is probably wrong.** The first is the one the ask is really
about; the third is the one phase 82 refused to do unattended, and it would be authorised by the same
flag if the flag is written carelessly.

The database-wide row is its own problem: **a per-mapping flag would let one mapping change a
database-level setting** that every other mapping — and every other application on that server —
shares. The target flags have no equivalent; `CREATE TABLE` touches only the table named.

### Where it would run, and with whose credential

The target flags run in `RunExecutor`, unattended, using the run's own connections. A source flag
following that pattern puts source DDL in the TaskRunner — and phase 25 was explicit that the runner's
credential is meant to be least-privilege:

> the driver only ever queries it, never enables it (keeps the app's own DB permissions to read-only +
> VIEW CHANGE TRACKING, per §6's least-privilege guidance)

So auto-enabling in the runner means the runner's credential needs `ALTER DATABASE` — DDL rights held
permanently by an unattended process, rather than exercised once by an admin pressing Apply. That is
the actual security change being requested, and it is worth naming as such before it is built, because
"just add a third flag" makes it look like a UI toggle.

The alternative — the API performs it when the mapping is saved, still without a human pressing Apply —
keeps DDL rights out of the runner but makes saving config a thing that alters a database.

### What is genuinely easy

Once the scope question is answered, the mechanics are already built. The plan is computed by the same
`PlanEnableSourceChangeCaptureAsync`, the state check makes it a no-op when already satisfied, and
`EnsureTargetTableProvisionedAsync` is the exact pattern to follow. This is a permissions and consent
question, not an implementation one.

## 2. One script for the whole replication

The valuable half of the ask, and the one with no equivalent today. Setting up a replication with forty
mappings currently means visiting forty Setup cards and pressing Apply forty times, or copying forty
scripts.

### Duplicate database-level steps are the interesting problem

Phase 25 decided the database-level `ALTER DATABASE` step would appear in **every** mapping's plan, and
leaned on the live state check to make it disappear once any mapping had run it:

> include it in every plan and let the live state check make it disappear once any mapping has run it,
> so the operator never has to know which mapping "owns" it

**That reasoning does not survive aggregation.** All N plans are computed against the same pre-state in
one pass, so an aggregated script contains `ALTER DATABASE Sales SET CHANGE_TRACKING = ON` forty times.
Running it is merely noisy; *copying it to a DBA* is embarrassing and invites the script being rejected.
So aggregation needs real deduplication — by statement within a scope, not by mapping.

### It is not one script, because it is not one server

A replication's source and target are different connections and usually different servers. A single
copyable block would be a script nobody can run anywhere. The aggregate has to be **grouped by
connection and database**, so what the operator copies is one script per server — which is also what
they will hand to whoever administers that server.

That grouping is probably the organising idea of the whole feature, and it is easy to miss if the ask
is read as "concatenate the plans".

### Ordering matters within a group

Database-level before table-level: `ENABLE CHANGE_TRACKING` on a table fails if the database has not
had it turned on. `CREATE TABLE` before `ALTER TABLE` for the same target. The per-mapping plans are
already ordered internally; merging them has to preserve that, which a naive concatenation-then-dedupe
does not guarantee.

### Batch apply is not a transaction

DDL across two servers cannot be atomic, and even within one server these are separate statements. A
batch that fails on step 17 of 60 leaves 16 applied. The honest design reports per-step outcomes and
stops at the first failure — later steps commonly depend on earlier ones, so continuing produces a
cascade of failures that buries the real one.

### Cost

`GetPlansAsync` opens a source connection and a target connection **per mapping**. Aggregating over
forty mappings naively is eighty connections and eighty rounds of catalog checks. Connections have to
be reused per endpoint, and even then a replication with hundreds of mappings makes this a slow
request — which raises whether it streams, caps, or is simply allowed to take a while behind a spinner.

### Mappings whose plan is not applicable

Some will come back `Unsupported` — a source with no primary key, a column with no cross-engine type
mapping. The aggregate must **list them as excluded, with reasons**, rather than quietly producing a
script that covers thirty-eight of forty mappings and looks complete.

---

# Outcome — resolved 2026-09-04

**One feature, one phase.** Not the two this doc assumed.

## Ask 1 is dropped, not deferred

**There will be no auto-provisioning of source change capture**, at any scope, in the runner or the
API. Phase 25's decision and phase 82's answer to `change-tracking-odbc-jdbc.md` both stand unchanged:
DbDataSync does not run DDL on a source without a human saying so.

It is dropped because **the aggregate page answers the problem it was meant to solve, better**. The
ask existed because setting up a replication meant visiting N mappings and pressing Apply N times. A
page that shows every statement for the whole replication, lets the operator tick the ones to run and
runs them in one batch removes that burden without anyone pre-authorising a *category* of DDL. Consent
becomes per-statement and visible, which is strictly more informative than a boolean agreed once in a
settings tab and then invisible forever.

The analysis above is kept because it is the reason, and because the flag will otherwise be proposed
again: `enableSourceChangeCapture` covers three actions of wildly different blast radius, a per-mapping
flag would authorise database-wide statements, and following the target flags' pattern would require
the TaskRunner's credential to hold DDL rights permanently — which phase 25 deliberately avoided.

## Ask 2 — the replication-wide provisioning page

Lives in the **existing Overview → Provisioning tab**, which already holds the two target auto-flags.
It is currently titled "Target provisioning"; once source steps appear beside them that name stops
being true and becomes **"Provisioning"**.

**Structure.** Steps from every table mapping, aggregated and then:

- **Deduplicated by statement within a scope.** This is the part phase 25's design did not anticipate.
  It deliberately put the database-level `ALTER DATABASE` step in *every* mapping's plan and relied on
  the live state check to make it vanish once any mapping ran it — but all N plans are computed against
  the same pre-state in one pass, so an aggregate contains it N times. Noisy to run; embarrassing to
  hand to a DBA.
- **Grouped by connection and database.** A replication's source and target are different servers, so
  a single copyable block would be runnable nowhere. What the operator copies is one script per server
  — which is also what they hand to whoever administers that server.
- **Ordered within a group**, database-level before table-level, create before alter. The per-mapping
  plans are already internally ordered; merging must preserve that, which concatenate-then-dedupe does
  not guarantee on its own.
- **Mappings whose plan is `Unsupported` are listed with their reasons**, not silently omitted. A
  script covering thirty-eight of forty mappings that looks complete is worse than one that says so.

**Selection.** Every statement can be included or excluded individually.

- **All ticked on load, and the choice is ephemeral.** The plan is recomputed against live state on
  every visit, so a remembered exclusion could hide a step that has since become necessary for a
  different reason.
- **Copy and Run both act on the selected set**, so what was copied and what would run are the same
  thing.
- **A step whose prerequisite is excluded warns but still runs.** The commonest reason to untick the
  database-level statement is that a DBA already ran it out of band — which DbDataSync cannot see from
  a plan computed a moment earlier — so blocking would break the exact workflow the page is for.
- **Steps the target auto-flags would apply anyway are listed, ticked, and labelled automatic.** The
  page is the complete truth about what the replication needs; a copied script that withholds steps
  because the app *might* do them later produces a half-configured database if it never runs, and the
  DBA receiving it has no way to know anything was left out.

**Batch apply is not a transaction.** DDL across two servers cannot be atomic. Per-step outcomes are
reported and the batch **stops at the first failure** — later steps commonly depend on earlier ones, so
continuing would bury the real error under a cascade.

**Cost.** Planning today opens a source and a target connection *per mapping*. The aggregate reuses one
connection per distinct connection+database across all mappings — usually two or three in total —
computed on demand behind a spinner. If any bound on the number of mappings planned is ever introduced,
the page must state how many were left out; a silent cap reads as "this is everything".

## Where the work goes

**`implementation/todo/phase-105-replication-wide-provisioning.md`** — one phase.
