# Phase 26 — Lifecycle hooks around staging and loading

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/database-provisioning-and-lifecycle-scripts.md`. The
authoring model is phase 22 + phase 23's, restated: SQL an operator writes, inline for a one-off and as a
named reusable artifact in the editor area for anything shared. The artifact model, the binding hierarchy
and the Scripts UI are phase 23's, reused rather than re-invented. The C# tier that *generates* hook SQL
is phase 27.

## What this phase will build

Four points at which an operator's own SQL runs, around each unit of work:

| point | typical use |
| --- | --- |
| `beforeStage` | drop or disable a target index that would slow the staging load |
| `afterStage` | inspect or fix up the staged set before it is applied |
| `beforeLoad` | disable non-clustered indexes, take a control-table row that says "loading" |
| `afterLoad` | rebuild indexes, `UPDATE STATISTICS`, insert the control-table row that says "loaded" |

## Where they go, exactly

Inside the per-segment loop in `RunExecutor.RunMappingAsync`
(`src/DataSync.TaskRunner/RunExecutor.cs:263-312`):

```csharp
foreach (var segment in segments)
{
    var read = await reader.ReadChangesAsync(...);        // lazy — no rows have moved yet
    //  >> beforeStage
    var staged = await stagingProvider.StageAsync(...);   // this is what drains read.Rows
    //  >> afterStage
    try
    {
        //  >> beforeLoad
        var written = await writer.ApplyAsync(...);
        //  >> afterLoad          <-- inside the try, so {{staging}} still exists
    }
    finally { await stagingProvider.CleanupAsync(...); }
}
```

`afterLoad` sits **inside** the `try`, before `CleanupAsync` drops the staged set — otherwise
`{{staging}}` refers to a table that no longer exists.

### Per segment, not per pass

A hook runs once per segment. In practice that is once per unit of work for everything except a
standalone reload replication with a configured segment list: an ordinary incremental pass gets
`[null]` from `ResolveSegmentsAsync`, and a backfill is already one segment per queued work item with
its own `RunId`. The context therefore carries `@segment`, `@segmentIndex`, `@segmentCount` and
`@isLastSegment`, so a control-table hook can write one row per segment or condition on the last one —
which is enough for the case that motivated this without adding a second set of call sites. A per-pass
scope is recorded under "out of scope" with what it would cost.

### Which connection — and why the two sides differ

**Target hooks run on the pipeline's own target connection. Source hooks open their own.** This is not
symmetric and it is not arbitrary:

- `MsSqlStagingTableProvider` stages into `#Staging_{guid:N}` — a **session-scoped temp table**
  (`MsSqlStagingTableProvider.cs:49`). A fresh connection cannot see it. So a target hook that wants
  `{{staging}}` has to be on the connection that made it. (`BatchInsertStagingProvider` uses a real
  table and says why, so the generic path would tolerate either — the contract has to satisfy both.)
- The source connection is mid-command during staging. `ReadChangesAsync` returns an `IAsyncEnumerable`
  that only executes as staging drains it; issuing a second command on that connection is the exact
  deadlock `IChangeReader`'s doc comment warns about ("MARS does not help here"). So a source hook gets
  its own connection, opened only when one is actually bound.

The target connection is pointed at the target database by the staging provider's
`UseDatabaseAsync`/`ChangeDatabase`, so a target hook runs in the target database's context. Worth
stating in the UI copy, because it is invisible from the SQL.

## Config

```yaml
hooks:
  beforeLoad:
    - name: disable-nonclustered
      connection: target            # target | source; default target
      sql: "ALTER INDEX ALL ON {{target}} DISABLE;"
  afterLoad:
    - name: record-load
      onError: fail                 # fail | warn; default fail
      sql: |
        INSERT INTO dbo.LoadControl (Replication, Mapping, RunId, Segment, RowsWritten, LoadedAtUtc)
        VALUES (@replication, @mapping, @runId, @segment, @rowsWritten, SYSUTCDATETIME());
```

A **list** per point, run in declared order. Carried on `ConnectionConfig`, `ReplicationTaskConfig` and
`TableMappingConfig` as `Dictionary<string, List<HookConfig>?> Hooks`, resolved mapping → replication →
connection, absent-means-inherit and present-with-null means explicitly none — **the same rules and the
same reasons as `ScriptBinding`**, including that the most specific level *replaces* the list rather
than appending to it. Merging would mean reading a table mapping does not tell you what runs.

`ScriptResolution` already implements exactly this walk for a different value type. Extract the walk into
a shared helper and have both `ScriptResolution` and the new `HookResolution` sit on it, rather than
copying forty lines — two implementations of an inheritance rule is how they drift.

## Reusable hooks live in the editor area

Inline `sql:` is right for a one-off. It is wrong for the hook every mapping in an estate needs — the
control-table insert copied into eighty table mappings is eighty places to fix when the control table
gains a column, and eighty diffs that have to be read to find the one that was copied wrong.

So a hook entry has a second form: a reference to a **named, reusable SQL hook**, authored in the same
editor area as C# scripts and bound the same way.

```yaml
hooks:
  afterLoad:
    - hook: record-load                 # config/scripts/record-load.sql + .yaml
      parameters:
        controlTable: dbo.LoadControl
```

**This reuses the script artifact model rather than inventing a parallel one.** `config/scripts/<name>.yaml`
already carries a name, a `Kind` (which slot it implements), a description, declared parameters and an
`Enabled` flag, beside a separate code file — for exactly the reason a SQL hook needs too: *"code embedded
in YAML diffs badly, cannot be opened by an editor, and re-indents on every round trip through a
serializer."* A SQL hook is the same artifact with `.sql` beside the manifest instead of `.cs`.

Two changes to that model, both small and both forced:

- **`ScriptConfig` gains `Language` (`CSharp` | `Sql`)** and `EntryType` stops being `required` — it is
  the C# entry point and is meaningless for SQL. `ScriptCompiler` and `ScriptSyntaxGuard` are skipped
  entirely for a SQL artifact; its validation is the token/parameter check below, which is the SQL
  equivalent of compiling.
- **Declared parameters become substitutable in the hook body**, so `record-load` can be written once
  against `{{controlTable}}` and bound to a different table per connection. Declared parameters are
  **identifiers** and go through the same quoting as the built-in tokens; they are not a second value
  channel.

`ScriptsController` and the Scripts list/edit pages carry both kinds: the list shows the language, and
the edit page swaps the Compile action for Validate and the C# editor mode for SQL. One place an
operator goes to write the code that runs in their pipeline, whichever language it is in.

Inline and named forms are both allowed at every level, in one list, in declared order. Inline stays
because forcing a named artifact for a single `UPDATE STATISTICS` would be ceremony — the same judgement
that keeps `ColumnMapping.Transform` inline while a script generates the reusable version.

## Identifiers are substituted; values are bound

The safety line, unmoved from phases 9, 17 and 22:

**Tokens — identifiers, substituted textually, all quoted by the target dialect's `QuoteIdentifier`,
every one derived from introspected metadata or validated config, never operator free-text:**
`{{target}}` (qualified), `{{targetSchema}}`, `{{targetTable}}`, `{{source}}`, `{{staging}}`.

**Parameters — values, bound:** `@replication`, `@mapping`, `@runId`, `@runKind`, `@segment`,
`@segmentIndex`, `@segmentCount`, `@isLastSegment`, `@rowsStaged`, `@rowsWritten`, `@watermark`.

Rendered through the dialect's `ParameterReference`/`ParameterName`, so the same hook text works where
the sigil is `:` rather than `@`. The host scans the hook text and adds **only the parameters it
actually references** — an unreferenced parameter is a portability hazard across providers and there is
no reason to send one.

### Not everything is available at every point

| | `beforeStage` | `afterStage` | `beforeLoad` | `afterLoad` |
| --- | --- | --- | --- | --- |
| `{{staging}}` | — | yes | yes | yes |
| `@rowsStaged` | — | yes | yes | yes |
| `@rowsWritten` | — | — | — | yes |

**Referencing an unavailable token or parameter is a validation error at save time**, naming the point
and what is available there — not a null at 3am. This is the same instinct as `ScriptSyntaxGuard`: the
most likely accident should fail where the operator is looking.

## The C# tier is phase 27, not this phase

A script that *generates* hook SQL is the natural third tier, and it is deliberately not built here —
see `phase-027-scripted-hook-generation.md`. Two reasons for the split: this phase is already a config
model, an execution path in the runner, a validator and a UI, and the phase README's rule is to split
rather than absorb; and the C# tier's real motivating use — target schema evolution — needs the plan
types phase 25 introduces, so building it after both is strictly cheaper than building it alongside.

What this phase must do for it: leave the execution path taking an ordered `IReadOnlyList<HookStatement>`
per point rather than a string, so phase 27 adds a producer and changes nothing else.

## Failure, and the thing that must be said out loud

`onError: fail` (default) fails the run, with the hook's name, the point, and the failing statement in
the error. `onError: warn` logs at Warning and continues.

**An `afterLoad` hook is not in the writer's transaction, and cannot be.** There is no host-owned
transaction to join: `MsSqlMergeWriter` runs a bare MERGE, and `DeleteInsertWriter`
(`DeleteInsertWriter.cs:46`) opens and commits its *own*. So a run can apply its data and then die before
its control-table row is written. Hooks must be written to be idempotent, and the UI has to say so where
someone authoring an `afterLoad` hook will read it.

Making it atomic means extending `IChangeWriter` to accept post-statements and threading them into every
writer's transaction — a change to all five writers in exchange for a real guarantee. That is a defensible
future phase, and it is deliberately not this one; recording the cost is what stops it being rediscovered.

Also worth stating: `afterLoad` runs **before** the watermark is persisted (`RunExecutor.cs:317`). A hook
that fails with `onError: fail` therefore leaves the watermark un-advanced and the same changes are
re-read next pass — which is the correct behaviour and another reason hooks must be idempotent.

## Logging

Every hook execution logs at Info into the run's log stream: point, hook name, connection side, statement
count, rows affected, elapsed. Non-negotiable — `task-run-errors-during-high-volume-workload.md` and
`ReadDiagnostics` both exist because work that happens invisibly is work nobody can diagnose, and a hook
is operator code running on someone else's schedule.

## How it will be verified

**Unit** (`DataSync.Core.Tests`, `DataSync.TaskRunner.Tests`, `DataSync.Scripting.Tests`)
- resolution across all three levels, including explicitly-none and list-replacement-not-merge
- token substitution: a target table named `Order]s` is quoted so it cannot break out of the identifier
- parameter binding: only referenced parameters are added; `:name` rendering on Postgres
- save-time validation rejects `@rowsWritten` at `beforeStage`, naming what is available
- `onError: fail` fails the run and `warn` does not, with both paths asserting the log line
- a named hook artifact loads, substitutes its declared parameters, and reports a missing required one
  as a configuration error naming the binding — and a SQL artifact never reaches `ScriptCompiler` or
  `ScriptSyntaxGuard`
- inline and named entries in one list execute in declared order

**Integration** (`Category=Integration`)
- an `afterLoad` control-table insert against the real target, asserting the row's `@rowsWritten` matches
  what the run reported
- a `beforeLoad` + `afterLoad` index disable/rebuild pair over a real load
- an `afterStage` hook selecting from `{{staging}}` on **both** staging providers — the temp-table one
  and the real-table one — because that is the case the connection-reuse decision exists to make work
- a source-side hook running while the reader's stream is live, proving the separate connection avoids
  the deadlock

**E2E** (`tests/DataSync.Web.Tests`) — authoring a reusable SQL hook in the Scripts area, binding it at
replication level, seeing it show as `INHERITED` on a mapping, and overriding it to none.

## Out of scope

- **C# that generates hook SQL.** Phase 27.
- **Transactional coupling with the writer.** Costed above.
- **Per-pass (rather than per-segment) scope.** It doubles the call sites to serve only the
  standalone-reload-with-segments case, and `@isLastSegment` covers the motivating example today. If a
  real need appears, it is a `scope: pass | segment` field and two extra call sites per point.
- **Hooks around the read** (`beforeRead`/`afterRead`) and around a whole worker run rather than a unit
  of work. Both are plausible; neither was asked for, and each adds a connection-lifetime question of
  its own.
- **Conditional hooks** (`when: rowsWritten > 0`). That is an expression language, and the workaround —
  `IF @rowsWritten > 0 …` on SQL Server, a `DO` block on Postgres, or the script tier on either — exists
  today.

## Open questions to resolve during implementation

- **Does a target hook need its own transaction?** An `afterLoad` that runs three statements currently
  runs them unbracketed. Wrapping each hook's statement list in one transaction is cheap and probably
  right; it is called out because it changes what a partially-failed hook leaves behind.
- **Command timeout.** An `ALTER INDEX ALL … REBUILD` on a large target will exceed any default. Almost
  certainly a per-hook `timeoutSeconds`; confirm against how the rest of the pipeline sets timeouts today
  rather than inventing a second convention.
- **Should `beforeStage` be able to see the row count it is about to stage?** It cannot — the reader is
  lazy and nothing has been counted. Worth making the UI say so, because "how many rows are coming" is
  the first thing someone will reach for.
