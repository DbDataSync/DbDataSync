# Phase 12 — Change Tracking Read Consistency (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/task-run-errors-during-high-volume-workload.md` — the
observation, the investigation, and the repro that established the cause. Read that first; this
document is only the fix.

## What this phase will build

Two independent problems, found together, fixed together.

### 1. The primary key must survive a missing base row

`MsSqlChangeTrackingReader.ReadIncrementalAsync` selects `CT.[<pk>], base.*`. Because `base.*`
re-emits the primary key, the result set contains the PK **twice**, and the row-mapping loop copies
CT's PK columns first and then every `base.*` column into the same dictionary keyed by name — so
`base.<pk>` overwrites the authoritative `CT.<pk>`. Where the base row is present the two are equal
and nothing is visibly wrong; where it is absent, the one column that was still trustworthy is set to
NULL along with the rest.

The select list becomes explicit instead:

```sql
SELECT CT.SYS_CHANGE_OPERATION,
       CASE WHEN base.{firstPk} IS NULL THEN 1 ELSE 0 END AS __BaseMissing,
       {ctPkColumns},          -- CT.[Id], ... — the only source of the key
       {baseNonPkColumns}      -- base.[Region], base.[Amount], ... — never the key
FROM CHANGETABLE(CHANGES {schema}.{table}, @previousVersion) AS CT
LEFT JOIN {schema}.{table} AS base ON {joinCondition}
WHERE CT.SYS_CHANGE_VERSION <= @targetVersion
ORDER BY CT.SYS_CHANGE_VERSION;
```

Non-PK column names come from `MsSqlSchemaQueries.GetColumnsAsync`, which the reader already calls for
the primary key. No column is ever named twice, and the key is sourced only from `CHANGETABLE`, which
always has it.

This is a defect on its own terms, independent of any isolation level, and would be worth fixing even
if the race below never occurred.

### 2. A vanished source row is skipped, not applied as NULLs

`__BaseMissing = 1` on a `'D'` row is normal and expected — the row is meant to be gone. On an `'I'`
or `'U'` row it means the source row was deleted between `CHANGETABLE` being evaluated and the join
probing the base table. Those rows are **skipped**: not yielded to the pipeline at all.

Skipping is safe and converges, and the reason is worth stating precisely, because "silently drop a
change" normally is not. That key's `SYS_CHANGE_VERSION` is now the delete's version, which is
strictly greater than this run's `@targetVersion` — so the very filter that let the stale `'I'`/`'U'`
through guarantees the delete is still pending, and the next pass (reading from a watermark at or
below `@targetVersion`) is certain to deliver it as a `'D'`. The target converges one pass later
rather than failing now.

**Skipped rows are counted and logged**, because the cost of the current behaviour was mostly that
nothing was observable: a run failed with a NULL-constraint error that named a column, and nothing
connected it to source-side churn. `ReadResult` gains an optional diagnostics object:

```csharp
public sealed record ReadResult(
    IAsyncEnumerable<ChangeRow> Rows,
    string NewWatermark,
    ReadDiagnostics? Diagnostics = null);

public sealed class ReadDiagnostics { public int RowsSkippedSourceRowGone; }
```

Optional and defaulted so the other readers are untouched. `RunExecutor` reads it after enumeration
completes and, when non-zero, logs one line naming the count — a run that skipped rows is a run whose
source was changing under it, which is exactly what an operator wants to see next to a slow or
repeatedly-retried mapping. (Judgment call: a counter mutated during enumeration and read afterwards
is a slightly awkward shape for a record. The alternative — no visibility at all — is what made this
bug expensive to find.)

### 3. Optional snapshot isolation

The skip above tolerates the race. Snapshot isolation removes it, and is what SQL Server's own Change
Tracking guidance pairs with `CHANGETABLE`: read the change table and the user table inside one
snapshot transaction and they are consistent as of the same instant.

Opt-in via a reader option, `snapshotIsolation`, default off — because it requires
`ALTER DATABASE <source> SET ALLOW_SNAPSHOT_ISOLATION ON`, and this driver deliberately never changes
database settings (see the type's XML doc: it queries Change Tracking, it does not enable it). It
becomes an operator prerequisite alongside enabling Change Tracking itself.

When enabled, the ordering changes so that everything is read inside the transaction:

1. `BeginTransaction(IsolationLevel.Snapshot)` on the source connection.
2. `CHANGE_TRACKING_CURRENT_VERSION()` **inside** it — so `@targetVersion` is consistent with the rows.
3. The `CHANGETABLE` query, on the same transaction.
4. Commit once enumeration completes.

The transaction must outlive the streamed `IAsyncEnumerable`, so it is begun inside the row-producing
iterator and disposed in its `finally`, alongside the command and reader — not in `ReadChangesAsync`,
which returns before a single row is read.

If the database setting is off, SQL Server raises error 3952. Catch it and rethrow with an actionable
message naming the exact `ALTER DATABASE` statement, rather than surfacing the raw text.

## What this phase does not build

Enabling `ALLOW_SNAPSHOT_ISOLATION` from the application (out of scope by design). Any change to the
watermark-on-success-only behaviour — it is what made these failures self-healing and it is correct.
Any equivalent hardening of `MsSqlWatermarkReader` or `MsSqlBatchReloadReader`; neither joins two
sources, so neither has this class of problem.

## How to verify when built

- **Unit tests on the generated SQL** (deterministic, no server): the primary key appears exactly once
  in the select list and is sourced from `CT`; `__BaseMissing` is present; every non-PK base column is
  named explicitly; a composite primary key renders correctly. Needs the statement builder extracted
  to an internal helper, reachable via the `InternalsVisibleTo` already in
  `DataSync.Drivers.MsSql.csproj`.
- **Unit test on row mapping**: an `'I'` row with `__BaseMissing = 1` is skipped and counted; a `'D'`
  row with `__BaseMissing = 1` is yielded normally with its PK intact.
- **`Category=Integration`, the race at scale**: ~400k change-tracked rows read while a second
  connection deletes in batches — the shape that produced 28,000 anomalous rows out of 312,000 during
  the investigation. Assert the read completes, no row reaches staging with a NULL key, and the target
  converges. Note honestly in the test that a race-based test passes vacuously if the race does not
  fire; the row count is set high because at that size it fired on every attempt.
- **`Category=Integration`, snapshot isolation**: same load with `snapshotIsolation` enabled and
  `ALLOW_SNAPSHOT_ISOLATION ON` — assert `RowsSkippedSourceRowGone` is **zero**, which is the whole
  point of the option and distinguishes it from merely tolerating the race.
- **Clear failure**: `snapshotIsolation` requested against a database without the setting produces the
  actionable message, not error 3952's raw text.
- `dotnet build` clean; the full existing suite green.

## Open questions to resolve during implementation

- Whether `dev-harness up` should set `ALLOW_SNAPSHOT_ISOLATION ON` on its scenario database so the
  option is exercisable locally without hand-editing SQL. It would make the harness's source database
  differ from a default one, which is either useful realism or a misleading default — decide when the
  option exists and can be tried both ways.
- Whether the skip should be capped or escalate: a mapping skipping a large fraction of its rows every
  pass is a source changing faster than the schedule can drain it, and logging alone may not be enough
  signal. Worth seeing the log line under real load before adding a threshold nobody asked for.
