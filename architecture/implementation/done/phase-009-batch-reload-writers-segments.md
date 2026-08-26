# Phase 9 — Batch Reload: Segment Machinery & New Writers

**Status**: Complete
**Plan reference**: `architecture/implementation-plan.md` § Backlog ("Batch reload"); design history in
`architecture/implementation/done/phase-008-work-queue-schema.md`'s "Design history" (four review
passes, three user-driven architectural corrections against the batch-reload design as a whole).
Corresponds to what the design review's Build Order called **"Phase B — Segment machinery and the two
new writers, still one process per trigger."** This document was written as a plan before
implementation and rewritten as a retrospective after it, per `architecture/implementation/README.md`.

## What was built

Phase 8 built the foundation batch reload needs (per-mapping `RunKind`/lock model, the durable
`WorkQueue`, the bounded producer/consumer worker) but none of batch reload's own pieces. This phase is
the first slice of the feature itself: the segment type system and the drivers that consume it, kept
decoupled from the queue/worker plumbing and from the SPA/trigger work (Phase 10) so every piece is
testable in isolation against real MSSQL.

**`BatchReloadSegment` — sealed record hierarchy** (`DataSync.Drivers.Abstractions`, engine-neutral):
`FullSegment`, `ListSegment(Column, Values)`, `RangeSegment(Column, RangeMin, RangeMax)` (half-open),
and `AutoSegment(Column, BucketCount)` (expansion-time only, never a runtime segment). A sealed
hierarchy rather than one record with mode-dependent nullable fields, so consumers `switch`
exhaustively instead of relying on nullable-field discipline. Each mode carries a `Describe()` used as
a run's `SegmentLabel` and in log lines.

**`SegmentSerializer`** — the single place segments are serialized, with a `"mode"` discriminator (not
System.Text.Json's default `$type`) on one shared `JsonSerializerOptions` used at both round-trip
points: the ephemeral `options["segment"]` channel and the persisted `Options["segments"]` array a
standalone reload replication will use (Phase 10). Every method's signature takes/returns the *base*
`BatchReloadSegment`, which is what forces System.Text.Json to emit the discriminator — the known
footgun the plan called out is closed structurally at the API rather than left to call-site
discipline, and pinned by a test that deliberately passes a concretely-typed local.

**`ISegmentExpandingReader`** — opt-in interface (`ExpandAutoSegmentsAsync`). Callers query the
capability with `reader is ISegmentExpandingReader`, never by matching Kind strings.

**`MsSqlBatchReloadReader`** (`Kind = "MsSqlBatchReload"`, implements both interfaces): ignores
`previousWatermark` entirely; reads `options["segment"]` (absent = whole table); composes the segment
predicate with the mapping's own `SourceTableRef.Filter` rather than replacing it; yields every row as
`ChangeOperation.Insert`, since a full scan can only observe rows that exist — encoding deletion is the
*writer's* job on a reload. `ExpandAutoSegmentsAsync` does one `MIN`/`MAX` round-trip per `Auto` entry
and passes every other mode through untouched.

**`MsSqlMergeReconcileWriter`** (`Kind = "MsSqlMergeReconcile"`) — the corrected design from the
review: the target is pre-filtered by a CTE *before* MERGE evaluates matching, so the engine bounds
what it scans and locks instead of comparing the change set against the whole table and considering
every row of it for `WHEN NOT MATCHED BY SOURCE`. `SELECT *` in the CTE (not just the mapped columns)
so it stays updatable.

**`MsSqlDeleteInsertWriter`** (`Kind = "MsSqlDeleteInsert"`) — delete the segment's scope, insert the
staged set, both inside one `DbTransaction`. Needs **no primary key**, since nothing is joined row by
row — the real differentiator from both MERGE-based writers. `RowsWritten` is the INSERT's count only;
folding the DELETE's in would double-count every row that was merely replaced.

**Shared predicate construction**: `MsSqlSegmentScope` renders `Full` → `1 = 1`, `List` →
`{col} IN (@__seg0, …)`, `Range`/expanded-`Auto` → `{col} >= @__segMin AND {col} < @__segMax`. Bounds
are always typed `SqlParameter`s built from the column's own catalog metadata — stricter than the
`SourceTableRef.Filter` precedent, and appropriate since segment bounds originate from a web form or
from system-computed buckets rather than from an administrator hand-authoring SQL.

**Shared writer construction**: `MsSqlTargetShape` resolves the target's columns, primary key, identity
flag, and the insert/join/update fragments once, and all three writers (including the pre-existing
`MsSqlMergeWriter`) build on it, rather than three near-identical copies of the clause-building code.

**Capability discovery**: `IChangeWriter` gains `bool SupportsReconciliation`; `DriverRegistry` gains
`SupportsSegmentation`, `SupportsReconciliation`, and `Describe(driverType)`, all backed by the
declarative properties and an interface check. New `GET /api/connections/{name}/capabilities` returns
`DriverCapabilities { driverType, readers[{kind, supportsSegmentation}], stagingProviders[{kind}],
writers[{kind, supportsReconciliation}] }` — the piece that lets Phase 10's SPA Kind pickers query live
capabilities instead of hardcoding a list.

## Decisions made during implementation

- **Segment columns are translated source→target through the mapping's `ColumnMapping`s** when a
  *writer* builds its scope. Not in the plan, and a real gap: a segment names a column on the source
  table, but a writer scopes the target, and a mapping may rename it across the two. Without
  translation a reload segmented on a renamed column either fails to find the column or — worse —
  finds an unrelated target column that happens to share the source name. Readers pass no mappings and
  resolve against the source directly.
- **Integral columns get integral bucket boundaries.** Dividing an `INT` range with decimal arithmetic
  produces fractional bounds, which then can't be bound to a parameter typed to match the column.
  Auto expansion floors for integral types and keeps full precision for decimal/float/temporal ones.
- **The last bucket's upper bound overshoots MAX by a whole unit** (`max + 1`, or `+ 1 day` for
  temporal columns) rather than by the type's smallest representable step. A half-open range would
  otherwise exclude MAX itself, and a "smallest step" finer than the column's own storage resolution
  (1 tick against a `datetime`'s 3.33ms) rounds straight back to MAX and silently drops the maximum
  row. Overshooting the top of an observed range costs nothing — there is nothing above MAX to sweep
  in.
- **Bucket boundaries are computed once into an array and consumed as consecutive pairs**, so bucket
  *i*'s exclusive upper bound *is* bucket *i+1*'s inclusive lower bound, and the buckets provably tile
  the range even where the division doesn't come out even. Degenerate (zero-width) buckets — more
  buckets requested than distinct values — are dropped rather than enqueued as empty no-op runs.
- **`Auto` expansion against an empty table yields one `FullSegment`, not zero segments.** An empty
  source still has to reach a writer, or a reconciling reload never gets the chance to clear the
  target.
- **An empty `ListSegment` is rejected outright.** `IN ()` isn't valid SQL, and the "obvious" reading
  of an empty list — matches nothing — would make a reconciling writer delete the target's entire
  scope.
- **The batch-reload reader echoes `previousWatermark` back as its `NewWatermark`** rather than
  inventing a value. It has no watermark of its own, and a standalone reload replication (Phase 10)
  runs as a `Primary` pass, whose watermark *is* persisted — echoing leaves the stored value exactly as
  it was found instead of overwriting it with something meaningless.
- **A new `DataSync.Drivers.Abstractions.Tests` project** was added; the segment types and their
  serialization are engine-neutral contract, and there was nowhere to test them that didn't imply they
  belonged to the MSSQL driver.

## Real bugs found

1. **`ListSegment`'s record equality silently degraded to reference equality.** The compiler-generated
   equality for a record compares an `IReadOnlyList<string>` member by reference, so two segments
   covering the same list — one just deserialized, one built in memory — compared unequal. Found by a
   round-trip test; the wrong answer for a value type describing a scope, and exactly the kind of thing
   that quietly breaks a dedupe rather than failing loudly. Fixed with an explicit
   `Equals`/`GetHashCode` on the type, not by working around it in the test.
2. **`MsSqlMergeWriter` has never handled identity targets** — replicating into a table whose key is an
   `IDENTITY` column fails at apply time with "Cannot insert explicit value for identity column",
   because a change set always supplies the key explicitly. Pre-existing, latent since Phase 3, and
   surfaced here because both new writers needed the same handling. Fixed for all three at once via
   `MsSqlIdentityInsert`, which brackets the write with `SET IDENTITY_INSERT ... ON/OFF` when a mapped
   column is an identity column — detected from the catalog (`sys.columns.is_identity`, newly exposed
   on `ColumnMetadata`) rather than declared in config, since the failure otherwise lands on an
   operator who had no reason to think the table was special. The setting is session-scoped and
   permitted on only one table at a time, so it is always turned back off, including on failure.
3. **A `SqlDbType.Decimal` parameter left at its default scale truncates the fraction.** A decimal
   range boundary would have become a slightly different boundary rather than an error. Precision and
   scale are now set explicitly from the value itself.

## How this was verified

- **39 new non-integration tests** (137 total, up from 103): `SegmentSerializerTests` (discriminator
  emission including the concretely-typed-local footgun, round-trips of every mode, the options
  channel), `MsSqlSegmentScopeTests` (predicate rendering for every mode, typed/scaled parameter
  binding, source→target column translation, and the rejection paths — unknown column, empty list,
  unexpanded `Auto`, malformed bound), `MsSqlSegmentExpansionTests` (even and uneven integral
  division, tiling with no gap or overlap, decimal and temporal boundaries, MAX coverage at coarse
  datetime resolution, single bucket, more buckets than values, and the rejections).
- **14 new `Category=Integration` tests** against the docker-compose SQL Server containers (33
  integration tests total): `MsSqlMergeReconcileWriter` making a segment match the source — insert,
  update, *and* delete-what's-missing — while leaving out-of-segment rows untouched even when they're
  equally missing from the source, for both list and range segments; `MsSqlMergeWriter` provably
  leaving out-of-segment deletions alone, pinning upsert-only as an intentional option rather than an
  oversight; `MsSqlDeleteInsertWriter` against a keyless target (with the MERGE writer's rejection of
  the same table asserted alongside) and its atomicity, with a concurrent observer on a second
  connection proving the segment is never seen empty mid-write; both reload writers inserting explicit
  keys into an `IDENTITY` target; the reader ignoring the watermark, composing segment and `Filter`,
  and `ExpandAutoSegmentsAsync` producing the expected bucket bounds, passing other modes through, and
  yielding a `FullSegment` for an empty table — plus running every produced bucket and asserting the
  whole table lands exactly once.
- Two `DataSync.Api.Tests` for the capabilities endpoint, asserting the flags track the drivers'
  declarations rather than a hardcoded list, and 404 for an unknown connection.
- `dotnet build` clean (0 warnings); full suite green — 137 non-integration, 33 integration.

## Open questions resolved

- **Is-identity metadata**: `MsSqlSchemaQueries` did *not* expose it. `sys.columns.is_identity` was
  added to the existing catalog query and `IsIdentity` to `ColumnMetadata` (see bug 2 above).
- **Capabilities response shape**: finalized as described above. It's deliberately flat and
  flag-per-entry so a Kind picker can render options and disable/annotate them from one request.

## What's explicitly not built

Nothing yet *injects* a `"segment"` value into a claimed work item's Reader/Cache/Writer options, and
nothing yet enqueues a `RunKind.Backfill` item with a segment attached — `WorkQueue.SegmentJson`
(Phase 8) is still written by no one and read by no one. The Backfill HTTP trigger endpoint,
standalone reload replications (`Reader.Options["segments"]`), the SPA's Options editor, Backfill
trigger form, capability-driven Kind pickers, and `RunKind` badges in run history are all Phase 10.
This phase produced working, independently testable driver-level pieces; Phase 10 wires them into
something triggerable end to end.
