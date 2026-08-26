# Phase 9 — Batch Reload: Segment Machinery & New Writers (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/implementation-plan.md` § Backlog ("Batch reload"); design history
in `architecture/implementation/done/phase-008-work-queue-schema.md`'s "Design history" (four review
passes, three user-driven architectural corrections against the batch-reload design as a whole).
Corresponds to what the design review's Build Order called **"Phase B — Segment machinery and the two
new writers, still one process per trigger."**

## Why this phase, and why now

Phase 8 built the foundation batch reload needs (per-mapping `RunKind`/lock model, the durable
`WorkQueue`, the bounded producer/consumer worker) but deliberately built none of batch reload's own
pieces. This phase is the first slice of the actual feature: the segment type system and the two new
writers it needs, kept deliberately decoupled from the worker/queue plumbing (already done) and from
the SPA/trigger-endpoint work (Phase 10) so it's testable in isolation against real MSSQL.

## What this phase will build

**`BatchReloadSegment` — sealed record hierarchy** (`DataSync.Drivers.Abstractions`, engine-neutral):

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(FullSegment),  "full")]
[JsonDerivedType(typeof(ListSegment),  "list")]
[JsonDerivedType(typeof(RangeSegment), "range")]
[JsonDerivedType(typeof(AutoSegment),  "auto")]
public abstract record BatchReloadSegment;

public sealed record FullSegment : BatchReloadSegment;
public sealed record ListSegment(string Column, IReadOnlyList<string> Values) : BatchReloadSegment;
public sealed record RangeSegment(string Column, string RangeMin, string RangeMax) : BatchReloadSegment;  // half-open
public sealed record AutoSegment(string Column, int BucketCount) : BatchReloadSegment;   // enqueue-time only, never persisted as a runtime segment
```

A sealed hierarchy, not one record with mode-dependent nullable fields — each mode carries only the
fields valid for it, enabling exhaustive `switch` pattern matching instead of trusting nullable-field
discipline (a direct, explicit correction from the design review). Discriminator property named
`"mode"` (not System.Text.Json's default `$type`), registered once on a shared `JsonSerializerOptions`
used at both round-trip points this phase introduces: the ephemeral `options["segment"]` channel
(injected into Reader/Cache/Writer options per iteration once the worker becomes segment-aware — see
"What this phase does not build") and the persisted YAML-embedded JSON array for a standalone reload
replication's static segment list (`ReaderConfig.Options["segments"]`, consumed starting in Phase 10).

**Known footgun to guard against in code review**: serialization must go through the generic overload
typed as the *base* `BatchReloadSegment` (`JsonSerializer.Serialize<BatchReloadSegment>(segment)`), not
a variable statically typed as the concrete derived record — otherwise `System.Text.Json` won't emit
the discriminator and deserialization silently breaks.

**`ISegmentExpandingReader`** (`DataSync.Drivers.Abstractions`) — opt-in interface,
`Task<IReadOnlyList<BatchReloadSegment>> ExpandAutoSegmentsAsync(sourceConnection, source, segments, ct)`.
Only `MsSqlBatchReloadReader` (this phase) implements it. Expands each `Auto` entry into `BucketCount`
concrete `RangeSegment`s via one `MIN`/`MAX` round-trip against the source. `Auto` is consumed exactly
once, wherever segment expansion happens (the enqueue-time caller, in Phase 10) — it should never flow
through as a runtime segment value.

**`MsSqlBatchReloadReader : IChangeReader, ISegmentExpandingReader`** (`DataSync.Drivers.MsSql`,
`Kind = MsSqlDriverKinds.BatchReload = "MsSqlBatchReload"`): ignores `previousWatermark` entirely
(always a full-or-segmented scan, never incremental); parses `options["segment"]` (absent = whole
table); builds `WHERE {segmentPredicate} [AND ({source.Filter})]` using `SqlIdentifier.Quote` +
parameterized literals (same style as `MsSqlWatermarkReader`); all rows yielded as
`ChangeOperation.Insert` — the *writer*, not the reader, encodes delete semantics for reload pipelines.

**`MsSqlMergeReconcile` writer** (`Kind = MsSqlDriverKinds.MergeReconcile = "MsSqlMergeReconcile"`) —
the corrected design from the review: the target is pre-filtered by a CTE *before* the MERGE evaluates
matching, not filtered only inside a `WHEN` clause (the original draft's mistake — see the design
review history):

```sql
;WITH TargetScope AS (
    SELECT * FROM {quotedTarget} WHERE {scopePredicate}   -- SELECT * (not just mapped columns) —
)                                                           -- required for the CTE to stay updatable
MERGE INTO TargetScope AS tgt
USING {staged.StagingLocation} AS src
ON {onClause}
WHEN MATCHED AND src.__Operation <> 'D' THEN UPDATE SET {updateClause}
WHEN MATCHED AND src.__Operation = 'D' THEN DELETE
WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D' THEN INSERT ({insertColumns}) VALUES ({insertValues})
WHEN NOT MATCHED BY SOURCE THEN DELETE;
```

This way the engine seeks/filters the target set up front instead of evaluating
`WHEN NOT MATCHED BY SOURCE` against the whole table — keeping segment-scoping a real, bounded
scan/lock footprint rather than a decorative filter that still forces a full-table comparison
(the specific Halloween-problem-adjacent risk the design review flagged). Factor the shared
ON-clause/update-clause/insert-list construction out of the existing `MsSqlMergeWriter` into one
internal helper used by both writers, rather than duplicating it.

**`MsSqlDeleteInsert` writer** (`Kind = MsSqlDriverKinds.DeleteInsert = "MsSqlDeleteInsert"`) — the
simpler option the design review asked for explicitly, not just MERGE-based choices:

```sql
BEGIN TRAN;
DELETE FROM {quotedTarget} WHERE {scopePredicate};
INSERT INTO {quotedTarget} ({insertColumns})
    SELECT {insertColumns} FROM {staged.StagingLocation} WHERE __Operation <> 'D';
COMMIT;
```

No CTE needed (`DELETE` takes a `WHERE` natively). No PK required — a real differentiator from both
MERGE-based writers, since there's no row-by-row join. One shared `DbTransaction` around both
statements guarantees no reader ever observes the segment empty mid-operation. `RowsWritten` = the
INSERT's affected-row count; the DELETE's count is a log line, not folded in.

Both new writers share this predicate construction: `Full` → whole scope (no filter);
`List` → `{col} IN (@v0,@v1,...)`; `Range`/expanded-`Auto` → `{col} >= @min AND {col} < @max`
(half-open). Bound values are always typed `SqlParameter`s (via the same column-type metadata
`MsSqlStagingTableProvider` already uses), not string-spliced — stricter than the existing
`SourceTableRef.Filter` precedent, appropriate since segment bounds can originate from a web form or
system-computed buckets, not just an admin hand-authoring SQL.

**Identity-column check**: confirm during implementation whether `MsSqlSchemaQueries` already exposes
is-identity metadata — both reload writers need `SET IDENTITY_INSERT {target} ON` when the target PK
(or any inserted column) is an identity column, auto-detected rather than operator-declared, matching
what the existing `MsSqlMergeWriter` already implicitly needs for identity targets today.

**Engine-neutral capability discovery**: `IChangeWriter` gains `bool SupportsReconciliation`
(`true` for `MsSqlMergeReconcile`/`MsSqlDeleteInsert`, `false` for the existing `MsSqlMerge`);
segmentation support is queried via `reader is ISegmentExpandingReader`. `DriverRegistry` gains
capability-query methods wrapping these checks. New API endpoint, e.g.
`GET /api/connections/{name}/capabilities`, surfaces available reader/cache/writer Kinds per registered
driver plus which readers/writers support segmentation/reconciliation — backed by the declarative
properties above, never by string-matching `Kind` values. This is the piece that lets Phase 10's SPA
Kind pickers query live capabilities instead of hardcoding a default "since v1 is MSSQL-only" (one of
the design review's four corrections).

## What this phase does not build

The worker/queue is already segment-aware at the schema level (Phase 8's `WorkQueue.SegmentJson`
column exists), but nothing yet *injects* a `"segment"` value into a claimed item's Reader/Cache/Writer
options, and nothing yet enqueues a `RunKind.Backfill` item with a real segment attached — that wiring,
along with the actual Backfill HTTP trigger endpoint, standalone-reload SPA authoring, and the
`MsSqlBatchReload`/new-writer Kind pickers, is Phase 10. This phase produces working, independently
testable driver-level pieces; Phase 10 wires them into something triggerable end-to-end.

## How to verify when built

- Unit tests for predicate rendering: `Full`/`List`/`Range`/`Auto`-after-expansion, with and without a
  coexisting static `SourceTableRef.Filter`.
- `Category=Integration` tests against real MSSQL (docker-compose containers):
  - `MsSqlMergeReconcile` deletes/inserts/updates only rows within a segment's scope, leaving
    out-of-segment rows untouched (seed a target with rows both inside and outside the segment range).
  - `MsSqlDeleteInsert`'s transaction is atomic — no reader ever observes the segment as empty
    mid-operation (a concurrent read during the transaction sees either the old or new state, never
    neither).
  - `MsSqlMergeWriter` (existing, reused unchanged) provably leaves out-of-segment deletions alone —
    documents the upsert-only path as a real, intentional option, not an oversight, for a mapping that
    picks it deliberately.
  - `MsSqlBatchReloadReader.ExpandAutoSegmentsAsync` produces the expected bucket boundaries for known
    `MIN`/`MAX` values and a given `BucketCount`.
- `dotnet build` clean; full existing suite (`--filter "Category!=Integration"` and
  `--filter "Category=Integration"`) green alongside the new tests.

## Open questions to resolve during implementation

- Confirm `MsSqlSchemaQueries` is-identity metadata (see above) — small prerequisite, not a design
  blocker, but needs checking before the `IDENTITY_INSERT` handling can be written.
- Exact `GET /api/connections/{name}/capabilities` response shape — sketch exists above, but the
  concrete DTO should be finalized alongside Phase 10's SPA needs so it isn't redesigned immediately
  after landing.
