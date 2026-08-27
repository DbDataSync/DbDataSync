# Generating and running database scripts — provisioning, and hooks around a run

**Status: resolved 2026-08-27 — see Outcome at the end.**

Two things were asked for together, and most of the value in this document is in separating them.

## The thought, as stated

> When a replication is being set up, we need to be able to automatically generate and run scripts for
> configuring the source or the target database. For example, there will need to be commands generated,
> and possibly executed to enable change tracking on SQL Server, and on the target, we will need to be
> able to generate the create table script. We'll also want to support running commands before/after
> staging data, and before/after loading data. This could be index or statistics maintenance, or
> inserting into a control table to indicate that the data has been loaded.

## These are two features, not one

They share exactly one thing — "DataSync produces SQL and executes it" — and differ in every way that
determines how they are built:

| | **Provisioning** | **Lifecycle hooks** |
| --- | --- | --- |
| when | once, at setup | every pass, forever |
| who authors the SQL | DataSync, from metadata | the operator |
| approval | a human reads it and presses Apply | approved once, at config time |
| what it touches | schema — `ALTER DATABASE`, `CREATE TABLE` | data and maintenance — statistics, indexes, control tables |
| where it is configured | nowhere; it is derived | connection / replication / mapping, inherited |
| blast radius of a bug | a wrong table shape, caught immediately | a wrong statement on every run of every mapping |

Building them as one "run some generated SQL" abstraction would force the provisioning side to carry an
inheritance model it has no use for, and the hook side to carry a preview-and-approve interaction that
would be absurd on every run. They get a phase each.

## Provisioning — what is actually missing today

Not a gap in the design so much as a hole where a feature should be:

- **Change Tracking enablement exists only in the dev harness.** `tools/DataSync.DevHarness/SqlBootstrap.cs`
  runs `ALTER DATABASE … SET CHANGE_TRACKING = ON` and `ALTER TABLE … ENABLE CHANGE_TRACKING`. Nothing
  in the shipped product does. `MsSqlChangeTrackingReader.cs:97` throws *"Change Tracking is not enabled
  for table X"* — a run failure with no remedy offered anywhere in the UI.
- **There is no CREATE TABLE renderer outside staging.** `BatchInsertStagingProvider.cs:192` and
  `MsSqlStagingTableProvider.cs:54` each build one inline, from the *target's own* column types. Neither
  helps create a target table that does not exist yet, which needs the **source's** types translated.
- **There is no cross-engine type map anywhere in the codebase.** The only SQL Server → Postgres shape
  translation that exists is hand-written in `tools/DataSync.DevHarness/TargetEngine.cs:232`, for one
  fixed five-column table.
- **A shipped reader option is unreachable for want of one statement.** `MsSqlChangeTrackingReader`'s
  `snapshotIsolation` exists to stop a concurrent delete stranding a reported insert, and phase 12 built
  it — but it requires `ALTER DATABASE … SET ALLOW_SNAPSHOT_ISOLATION ON`, which
  `phase-012-change-tracking-read-consistency.md` lists under what it deliberately did *not* build and
  which `dev-harness up` does not run either. Turning the option on today fails with error 3952 unless
  someone knew to run that statement by hand.

So a new mapping to a target table that does not exist has no path through the product at all, and a
source without Change Tracking has one that ends in an exception.

## Should DataSync run DDL on someone's database at all?

`change-tracking-odbc-jdbc.md` already lists this as an open **product** question — it arises there
because a `TriggerAudit` reader would need `CREATE TRIGGER` on a source. It is the same question, and it
should be answered once, here:

> Doing it silently would be wrong; offering it as an explicit, previewable, opt-in action with the
> generated DDL shown before it runs is defensible.

That is the answer, with one narrow exception (below). It also means the generator has to be good enough
that a DBA who will *not* let DataSync run it can copy the script and run it themselves — which is the
common case in an enterprise, and makes "Copy" as important a button as "Apply".

## Hooks — the seam already exists

`RunExecutor.RunMappingAsync` (`src/DataSync.TaskRunner/RunExecutor.cs:263-312`) already has the four
insertion points, precisely placed, inside the per-segment loop:

```csharp
var read    = await reader.ReadChangesAsync(...);      // rows are LAZY; nothing has executed yet
var staged  = await stagingProvider.StageAsync(...);   // beforeStage / afterStage bracket this
var written = await writer.ApplyAsync(...);            // beforeLoad / afterLoad bracket this
await stagingProvider.CleanupAsync(...);               // afterLoad must precede this to see the staging table
```

`StagedChangeSet` carries the staging location and row count; `WriteResult` carries rows written. Both
of the things a control-table row wants are already in hand at the point it would be written.

**Two facts about connections make the design, and neither is obvious:**

1. `MsSqlStagingTableProvider` stages into `#Staging_{guid:N}` — a **session-scoped temp table**
   (`MsSqlStagingTableProvider.cs:49`), visible only on the connection that made it.
   `BatchInsertStagingProvider` deliberately uses a real table instead, and says why. So a hook that
   wants to reference the staging table *must* run on the pipeline's own target connection; a fresh
   connection cannot see it on SQL Server.
2. The source connection is the mirror image. `ReadChangesAsync` returns an `IAsyncEnumerable` that is
   only executed as staging drains it, so during staging the source connection is mid-command. This is
   the same hazard `IChangeReader`'s own doc comment warns about ("MARS does not help here"). A
   source-side hook must get its **own** connection.

Target hooks reuse the pipeline's target connection; source hooks open their own. The asymmetry is not
a wart — each side is doing the only safe thing available to it.

## Authoring: literal SQL, with a script as the generator

The house pattern is already established by `ColumnMapping.Transform` (phase 22, literal SQL in the
source dialect) and `ISqlColumnExpression` (phase 23, a script that *generates* that SQL). Hooks take
the same two tiers for the same reasons: `INSERT INTO LoadControl …` and `UPDATE STATISTICS …` are one
statement and should not cost a compiled C# script, while anything conditional or engine-branching
needs one.

The scripting rule carries over unchanged, from `csharp-script-extension-points.md`:

> A script returns a description of what to do; the host does it.

**And the SQL itself is an artifact, not a config field.** A control-table insert copied inline into
eighty table mappings is eighty places to fix when the control table gains a column. `config/scripts/`
already models a named, described, parameterised, separately-editable code artifact with an `Enabled`
flag — for exactly the reason a shared hook needs one — so a reusable hook is that artifact with `.sql`
beside the manifest instead of `.cs`, listed and edited in the same place. Inline stays for the one-off,
on the same judgement that keeps `ColumnMapping.Transform` inline.

## Safety: identifiers are substituted, values are bound

The one thing a hook must not become is a string-concatenation hole. The split is:

- `{{target}}`, `{{staging}}`, `{{source}}` — **identifiers**, substituted textually, every one of them
  derived from introspected metadata or validated config, and quoted by the dialect's own
  `QuoteIdentifier`. Never operator free-text.
- `@rowsWritten`, `@runId`, `@segment` — **values**, bound as real parameters.

That is the same line phases 9, 17 and 22 already drew, and this is not the place to move it.

---

# Outcome — resolved 2026-08-27

Agreed, and split into two phases:

- **`implementation/todo/phase-025-database-provisioning.md`** — an opt-in `IProvisioner` driver
  interface returning a *plan* (state + steps + fidelity warnings), the canonical type system that makes
  cross-engine `CREATE TABLE` possible, the preview/Apply API and the Setup card in the SPA.
- **`implementation/todo/phase-026-lifecycle-hooks.md`** — `beforeStage` / `afterStage` / `beforeLoad` /
  `afterLoad`, authored as SQL: inline for a one-off, or as a **named reusable artifact in the same
  editor area as C# scripts** for anything shared, bound and inherited across the three levels.
- **`implementation/todo/phase-027-scripted-hook-generation.md`** — the `lifecycleHook` script slot: C#
  that *generates* hook statements. Split out of 26 rather than built alongside it, because 26 is already
  a config model, a runner path, a validator and a UI, and because the generated tier's motivating use —
  target schema evolution — needs phase 25's canonical type system to render a column type.

Four decisions were taken in the discussion that produced these:

1. **Two phases, provisioning first.** It unblocks a failure mode (`Change Tracking is not enabled`)
   that today has no remedy in the product at all.
2. **Never automatic, with one narrow exception.** Every generated script is previewed and applied by a
   human. The exception is an opt-in, per-mapping `createTargetTableIfMissing`: target-side only,
   additive only, never `ALTER`, never source DDL — because "the target table does not exist yet" is
   the one case where the answer is fully determined by config the operator already wrote, and making
   them press a button to confirm what they just typed is ceremony. This also answers the open product
   question in `change-tracking-odbc-jdbc.md`: **no**, DataSync does not create triggers on a source
   without an explicit human Apply.
3. **Literal SQL plus a generating script**, mirroring phase 22 / phase 23.
4. **Cross-engine type mapping is in scope now**, not deferred to same-engine-first. The SQL Server ↔
   PostgreSQL pairing is already exercised end to end by the dev harness (`--target-engine postgres`),
   so a same-engine-only generator would be unusable on the one heterogeneous pairing the project
   actually runs. The cost is accepted knowingly: this is where silent-truncation bugs live
   (`nvarchar(max)` → `text`, `datetime2(7)` → `timestamp(6)`, `money` → `numeric`), which is why the
   plan carries per-column **fidelity warnings** as a first-class output rather than as documentation.
