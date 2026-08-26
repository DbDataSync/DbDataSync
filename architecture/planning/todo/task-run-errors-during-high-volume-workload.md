# During a run of `dev-harness workload --rate 1500 --duration 2m` several task runs reported errors

The errors indicated that it was not able to set certain target columns to NULL.

My theory is that the change tracking captured inserts or updates followed by deletes for the same primary key
during a single task run. This would cause the join from the change tracking to the actual row to return
null, which would cause the process to fail for an insert/update operation that requires non-null columns.

This theory may be incorrect, because I haven't found an explanation for how it would recover in that
scenario, and the `scripts/dev-harness verify` command reported that all rows were identical.
---

## Investigation (Claude, 2026-08-26) — cause confirmed, not yet fixed

**The theory was right in shape, wrong in mechanism, and the recovery has a mundane explanation.**

It is not that Change Tracking captures an insert/update *and then* a delete for one key within a
run. `CHANGETABLE(CHANGES ...)` returns **one net row per primary key, stamped with that key's latest
change version** — so a key that is updated and then deleted comes back as a single `'D'` row at the
delete's version. Proven directly: with `previousVersion=1`, an update at v2 and a delete at v3, the
reader's query with `@targetVersion=2` returns **zero rows** — the `WHERE CT.SYS_CHANGE_VERSION <=
@targetVersion` filter excludes it outright. That path is safe; the change simply arrives on the next
pass.

The real cause is a **read-consistency race inside the reader's single SELECT**
(`MsSqlChangeTrackingReader.ReadIncrementalAsync`):

```sql
SELECT CT.SYS_CHANGE_OPERATION, CT.[Id], base.*
FROM CHANGETABLE(CHANGES [dbo].[Orders], @previousVersion) AS CT
LEFT JOIN [dbo].[Orders] AS base ON CT.[Id] = base.[Id]
WHERE CT.SYS_CHANGE_VERSION <= @targetVersion
```

It runs under READ COMMITTED with no snapshot, so `CHANGETABLE` and the base table are read at
different instants. A key whose change version is already `<= @targetVersion` passes the filter, and
if the workload deletes that row before the join probes it, `base.*` comes back entirely NULL.

Reproduced deterministically: 400k change-tracked rows, the reader's query run against a session
deleting 2,000 rows at a time — **28,000 of 312,000 returned rows came back `SYS_CHANGE_OPERATION='I'`
with a NULL base row** (~9%).

**A second, independent defect turns that into a hard failure rather than a skippable row.** The
select list is `CT.[Id], base.*`, and `base.*` re-emits the primary key — so `Id` appears **twice**
(confirmed against a live query). The C# reader copies CT's PK columns first, then copies every
`base.*` column into the same dictionary by name:

```csharp
for (var i = 1; i <= pkColumns.Count; i++)                    // CT.Id  -> values["Id"] = 42
    values[reader.GetName(i)] = ...;
if (operation != ChangeOperation.Delete)
    for (var i = pkColumns.Count + 1; i < reader.FieldCount; i++)   // base.Id -> values["Id"] = NULL
        values[reader.GetName(i)] = ...;
```

So `base.Id` **overwrites** the good `CT.Id`. When the base row is missing, the one reliable value in
the row — the key — is destroyed along with everything else. Staging then holds a row with
`__Operation='I'` and every column NULL; `MsSqlMergeWriter`'s `ON tgt.Id = src.Id` never matches NULL,
so it falls through to `WHEN NOT MATCHED BY TARGET ... THEN INSERT` and attempts to insert NULLs into
NOT NULL columns. That is the observed error (SQL Server error 515).

**Why it recovers, and why `verify` is clean.** `RunExecutor.RunMappingAsync` calls
`watermarkStore.SetWatermark` only *after* `writer.ApplyAsync` returns. A failed apply throws straight
past it, so the run is marked Failed and **the watermark is not advanced**. The next scheduled pass
re-reads from the same `previousVersion` with a fresh, higher `targetVersion`; by then the deletes are
in range and come back as `'D'`, which applies correctly. The failures are transient and self-healing —
which is why the end state reconciled.

### Options (not yet chosen)

1. **Snapshot isolation** — Microsoft's documented pairing for Change Tracking: read `CHANGETABLE` and
   the base table inside one snapshot transaction so they are consistent. Needs
   `ALLOW_SNAPSHOT_ISOLATION ON` on the *source* database, which makes it an operator prerequisite
   like enabling CT itself — this driver deliberately never changes database settings.
2. **Fix the reader** — no database setting required, and it fixes the PK clobber, which is a plain
   defect regardless of isolation:
   - stop `base.*` overwriting the CT primary key (alias the base columns, or don't re-select the PK);
   - add a `CASE WHEN base.<pk> IS NULL THEN 1 ELSE 0 END` marker and **skip** a non-`'D'` row whose
     base row has vanished. Skipping is safe and converges: that key's CT version is now above
     `targetVersion`, so the delete is guaranteed to arrive on a later pass.

Option 2 looks like the one to do unconditionally; option 1 is a worthwhile extra for operators who
can enable it, since it avoids the wasted read-and-skip.
