# Phase 33 — Generic trigger-audit change tracking

**Status**: Done
**Plan reference**: `architecture/planning/done/change-tracking-odbc-jdbc.md`, which identifies this as
"the cheapest large win in the whole change-tracking set", and
`architecture/planning/done/change-tracking-strategies.md` for the strategy taxonomy.

## Why this is the best value in the change-tracking set

Every other mechanism is one engine's. Log-based CDC needs a provider, a privilege and a server
setting per engine; there is a Postgres phase, an Oracle phase and a MySQL phase behind it, each
substantial.

A trigger-maintained shadow table is different: **the read side is completely generic.**

```sql
SELECT <columns> FROM <shadow> WHERE seq > ? ORDER BY seq
```

joined back to the base table for current values, deletes emitted from the shadow row alone. That is
the same shape `MsSqlChangeTrackingReader` already has, and it is engine-neutral apart from quoting
and placeholders — which is exactly what `SqlDialect` is for.

So **one reader implementation serves every engine that has triggers**: SQL Server, Postgres, MySQL,
Oracle, and — the case that motivated it — anything reachable through ODBC or JDBC. It delivers
**delete detection** to all of them at once, which is the single capability watermark mode cannot
offer.

## The split: generic reader, per-engine DDL

The *setup* side is not generic. `CREATE TRIGGER` diverges more than almost anything else in SQL:
PL/pgSQL functions, MySQL's inline body, Oracle's `:NEW`/`:OLD`, T-SQL's statement-level `inserted` and
`deleted` pseudo-tables. There is no portable trigger DDL and there is no point pretending otherwise.

Which gives the shape:

- **`TriggerAudit`** — one generic reader, unprefixed Kind, dialect-driven, in `DataSync.Drivers.Generic`.
- **The DDL is a provisioning step**, per engine. Phase 25 already built `IProvisioner` with a
  plan-and-apply flow that shows the operator the script before running it, and
  `MsSqlProvisioner.BuildEnableChangeCaptureSteps` is already the place SQL Server's Change Tracking
  enablement lives. Trigger enablement is the same shape, in the same place.
- **ODBC and JDBC supply their DDL by script** — phase 27's `ILifecycleHook` and phase 30's builders
  are both precedents for operator-supplied SQL where we cannot know the engine.

## The shadow table

```sql
CREATE TABLE <schema>.DS_Changes_<table> (
    Seq        BIGINT IDENTITY(1,1) PRIMARY KEY,   -- or bigserial / sequence, per engine
    Op         CHAR(1) NOT NULL,                   -- I | U | D
    <key columns>,
    ChangedAt  <timestamp> DEFAULT <now>
);
```

`Seq` is the watermark — monotonic by construction, and a plain string like every other position.

### Two traps, both already paid for once

- **The key must come from the shadow row, never from the joined base row.** Phase 12 found exactly
  this in the Change Tracking reader: `base.*` overwrote the change record's key, so a row deleted
  since capture had its key nulled — destroying the one value still reliable and turning a skippable
  row into a failed run. The same LEFT JOIN has the same trap here.
- **Collapse to net change per key.** `CHANGETABLE` does this natively; a shadow table does not, so a
  row updated fifty times yields fifty shadow rows. Collapsing in the reader (latest `Seq` per key
  wins) keeps the staged set proportional to *changed rows* rather than to write volume — which is the
  difference between this being usable on a busy table and not.

## Pruning needs a post-write hook, and it is the fourth time

The shadow table grows forever unless something deletes below the persisted watermark. The watermark
lives in DataSync's state store, not the source, so something has to carry it back **after a
successful write**.

`IChangeReader` has no "the write succeeded" callback. This is the same missing hook that
`change-tracking-postgres.md` needs for `pg_replication_slot_advance`, that `change-tracking-mysql.md`
needs for the same pruning, and that `script-generated-change-queries.md` named as a fourth. It should
be built **here**, once, in the shape the rest of the codebase already uses for optional capabilities:

```csharp
public interface IPositionAcknowledging
{
    Task AcknowledgeAsync(DbConnection connection, SourceTableRef source, string watermark,
                          IReadOnlyDictionary<string, string> options, CancellationToken ct);
}
```

Opt-in by interface exactly like `ISegmentExpandingReader`, `IConnectionTester`, `IDialectProvider` and
`ITableCatalogProvider`. `RunExecutor` calls it after the write commits and the watermark is persisted
— never before, because the watermark-on-success-only rule is what makes a failed run safe to retry.

## Should DataSync create triggers on someone's source at all?

`change-tracking-odbc-jdbc.md` raises this as a product question and phase 25 has already answered it
for the whole class of DDL: **yes, as a previewable, applyable action.** The generated DDL is shown
before it runs, the operator applies it deliberately, and nothing happens silently. Triggers are more
invasive than an index and less invasive than `ALTER DATABASE SET CHANGE_TRACKING`, which the tool
already offers.

What this phase adds is honesty about the cost at the point of choice: a trigger puts work on every
transaction that touches the table, forever, and the UI should say so where the operator turns it on.

## What this phase does not build

Any log-based reader. Any engine's triggers beyond the ones with drivers (SQL Server and Postgres);
MySQL and Oracle get theirs with their drivers.

Automatic pruning schedules. Pruning happens on acknowledgement, driven by the run, not by a timer
DataSync would have to own.

## How to verify when built

- Unit tests on the generated read statement per dialect, and on the net-change collapsing — the part
  that decides whether this is usable under load.
- A test that a delete's key comes from the shadow row, constructed so that a `base.*` regression
  fails it. This is the phase 12 bug; it should be impossible to reintroduce silently.
- `Category=Integration` against SQL Server **and** Postgres, same reader, same test body: insert,
  update and delete at the source, all three reaching the target.
- The provisioning plan showing trigger DDL before it runs, per engine.
- `IPositionAcknowledging` called after a successful write and **not** after a failed one — the
  assertion that protects the retry.
- Full suite green.

## Open questions

- **Where the shadow table lives.** Beside the source table is simplest and is where an operator will
  look for it; a separate schema is tidier and needs rights they may not have.
- **Composite and non-integer keys.** The shadow table carries the key columns, so a composite key is
  several columns and a GUID key is not orderable — but `Seq` is what orders the feed, not the key, so
  this should fall out. Worth a test rather than an assumption.
- **Truncate.** `TRUNCATE TABLE` fires no row triggers on any engine here, so a truncated source
  silently produces no deletes. Nothing can fix that from inside a trigger; it is worth *saying* at
  the point the operator turns triggers on.

---

# Retrospective

The plan's central claim held: the read side really is generic, and the Postgres integration tests are
the SQL Server ones with different quoting. One reader, two engines, delete detection on both.

## The split is visible in the file list, which is the point

`TriggerAuditReader` and `TriggerAuditStatement` know nothing about an engine beyond what
`SqlDialect` tells them. `MsSqlTriggerAudit` and `PostgresTriggerAudit` share not one line, and could
not: T-SQL's trigger is statement-level over `inserted`/`deleted` pseudo-tables, and Postgres has no
inline trigger body at all — it needs a plpgsql function and a trigger that calls it. Anyone tempted to
unify them later can read the two files and see why not.

The T-SQL trigger is written set-based on purpose. A row-by-row trigger is the classic way to make a
bulk update take minutes, and this one goes on somebody's write path forever.

## Both traps were already paid for, so both are pinned twice

The key coming from the shadow row rather than the joined base row is the phase 12 bug in a new place,
and the assertion exists at both levels: a statement test that fails if `base.[Id]` ever appears in the
select list, and an integration test on each engine that deletes a row and reads its key back. Net
collapsing is the same — asserted in the SQL, and asserted by writing fifty updates and getting one
row out of fifty-one shadow rows.

## `IPositionAcknowledging` was worth building here

Three planned phases want it — Postgres slot advance, MySQL binlog pruning, and script-generated
queries all named it — so building it against the first real caller rather than the first speculative
one is what the plan asked for and it fitted in one interface and one call site.

The ordering is the whole of it: after the write commits **and** after the watermark is stored. The
negative case is the one that matters, and it is tested against a real failing pass rather than a
mock — the target table does not exist, the write fails, and the shadow rows and the absent watermark
are both still there afterwards. Acknowledging early would let a source discard changes this
replication has not written, which turns a retryable failure into permanent data loss.

An acknowledgement that *fails* is logged and does not fail the run. The rows are at the target and the
watermark is stored; unpruned history is a disk-space problem, and reporting it as a failed pass would
be reporting a successful replication as broken.

## Two tests that were wrong before they were right

The keyless-table test failed twice, and both times the reader was correct and the test was not: a
table with no key also has no shadow table, so the first error is "capture is not enabled here" — and
once given a shadow table, an *empty* one means "nothing to do", which is also right. The test now
gives it a shadow table with a row in it, so the failure under test is the one the name claims.

Worth recording because the instinct on a red test is to change the code.

## Verification

- `TriggerAuditStatementTests` (10) — collapsing, the key's source, composite keys, the bounded
  window, the missing-base marker tested against a key column, transforms aliased back, a keyless
  table refused with a reason, pruning's boundary, and the same read through a second dialect, which
  is what stops "engine-neutral" being a hardcoded quote character.
- `TriggerAuditReaderTests` on **SQL Server** (9) and on **Postgres** (6), same reader, same body:
  every operation as itself, a delete keeping its key, fifty updates collapsing to one row, a bounded
  window, pruning off by default and on when asked, and — on Postgres — a composite key surviving a
  delete on both halves, which a single-column test cannot say.
- `RunExecutorIntegrationTests` (2) — a successful pass acknowledging and pruning, and a failed pass
  doing neither.
- Full suite green: 792 .NET tests, 39 Playwright.

## Open questions

- ~~**Where the shadow table lives.**~~ Beside the source table, in its schema: it is where an operator
  will look for it and where the rights that let them create a trigger already reach.
- ~~**Composite and non-integer keys.**~~ They fall out, as the plan suspected, and there is now a test
  saying so rather than an assumption.
- ~~**Truncate.**~~ Unfixable from inside a trigger and therefore *said* — it is one of the two costs
  the enablement plan states where the operator turns capture on.
- **New**: nothing prunes a shadow table for a replication that has been deleted or disabled. The
  trigger keeps writing and nothing reads it, which is a table growing on somebody's source with no
  owner. Disabling capture is not built and should be, next to enabling it.
- **New**: the reader takes the source's primary key from the catalog on every incremental pass. That
  is one round trip per pass against a value that changes about never; the Change Tracking reader does
  the same, so this is a shared cost worth measuring before it is worth fixing.
