# Phase 91 — Readers and writers run from the cache only; an empty cache fails the run

**Status**: Done. See Outcome.
**Plan reference**: `architecture/planning/done/reader-writer-metadata-cache-consumption.md` — the
deliberately deferred second half of `architecture/planning/done/mapping-metadata-cache.md`, revised
before dispatch from an initial live-fallback design to cache-only/fail-loud. Read the plan doc's
"The design that was rejected" section — it matters for why this phase has no fallback path at all.

## The gap

Phase 90 built `TableMappingConfig.SourceColumns`/`TargetColumns`, captured on save and updated only by
an explicit Refresh action. Nothing reads them. Six run-time consumers still live-query
`ITableCatalog`/`IDriver` introspection on every pass, exactly as before phase 90 existed.

## What to build

### The rule: cache only, no live fallback, ever

For each consumer below, read the mapping's relevant cached column list (`SourceColumns` for a reader,
`TargetColumns` for a writer/staging provider) and look up the needed column by name. If the list is
empty, or the specific column isn't in it, **throw** — never fall through to a live catalog call. This
is a deliberate behavior change on deploy: any mapping that hasn't been saved or explicitly refreshed
since phase 90 shipped will fail its next run under one of these consumers. That's accepted, not a
regression to avoid.

### The failure

A new, named exception (naming your call — `MetadataNotCachedException` is a reasonable starting point),
mirroring `PositionExpiredException`'s existing shape (phase 32): carries the mapping name and which
side/column was needed, with a message telling the operator plainly what happened and what to do — use
Refresh metadata. Caught wherever the run pipeline already catches `PositionExpiredException`, reported
as a distinct `TaskRuns.FailureKind`. Confirm this rides the existing `CompleteRun`/phase-77
run-failure-notification path with no new wiring, the same way phase 80's watermark-expiry producer did
— don't build a second notification path if the first already covers it.

### Source-side consumers

- `WatermarkReader.ReadChangesAsync` — the watermark column, from `SourceColumns`.
- `BatchReloadReader.ReadChangesAsync` — the segment column (feeds `SegmentScope.Build`), from
  `SourceColumns`.
- `TriggerAuditReader.ReadIncrementalAsync`/`ResolveColumnsAsync` — the key/non-key split, from
  `SourceColumns`.
- `ScriptedQueryReader.ReadChangesAsync` — source columns for the script's context, from
  `SourceColumns`.

### Target-side consumers

- `TargetShape.LoadAsync`/`MsSqlTargetShape.LoadAsync` — shared by every writer's `ApplyAsync`
  (`MsSqlDeleteInsertWriter`, `MsSqlMergeReconcileWriter`, `MsSqlMergeWriter`, `DeleteInsertWriter`,
  `SnapshotWriter`, `Scd2Writer`). Fix once here; confirm it actually covers all six call sites rather
  than assuming from the name.
- `BatchInsertStagingProvider.StageAsync` — target/staging columns, from `TargetColumns`.

### Confirm before trusting the cache

`MetadataService`/`IColumnCatalog` (design-time, what phase 90 captures from) and `ITableCatalog`
(run-time, what these six consumers call today) are two different introspection paths in this codebase's
own architecture — a connection with a bound `metadataProvider` script could make them disagree for the
same table. Under a fail-loud design this matters more than it would have under a fallback one: a
diverging cache could report a column present-but-wrong instead of merely absent. Verify this before
wiring anything.

## What this phase should not do

- Touch `BatchReloadReader.ExpandAutoSegmentsAsync` — confirmed to stay live permanently.
- Backfill any mapping's cache, or add a migration/startup step that pre-populates one.
- Add any live fallback, full or partial, anywhere in these six consumers.
- Any SPA change beyond however the new `FailureKind` already displays in run history — check it renders
  sensibly before assuming nothing's needed, but don't build new UI for it.

## How to verify

- Per consumer, a test proving the cache is used and no introspection call happens when it's populated
  with the needed column (a fake `ITableCatalog`/`IDriver` that throws if invoked, the same technique
  phase 87 used to prove `ReaderLagService` stopped calling `IChangeCounterSource`).
- Per consumer, a test proving the new failure is thrown — naming the mapping and the missing side/
  column — when the cache is empty or missing that column, and that no live query happens first.
- A test asserting the failure produces a `Failed` run with the new `FailureKind` and a notification,
  through the existing path, with no new producer wired.
- A test asserting a mapping refreshed or re-saved after this phase ships runs successfully on its next
  pass.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean.

---

## Outcome

Built as specified: all six consumers read only `SourceColumns`/`TargetColumns`, an empty or incomplete
cache throws a new, named failure before any live catalog call, and that failure rides the existing
`CompleteRun`/phase-77 notification path with no new wiring beyond a `FailureKind` and a
`NotificationKind` constant.

### The `MetadataService`/`ITableCatalog` divergence — verified, and it does not change the design

The docs asked this to be confirmed before trusting the cache, rather than assumed. It is real: phase
29's own doc states the boundary plainly — `MetadataService` (design-time, what the picker and phase 90's
capture both go through) honours a connection's bound `metadataProvider` script; `ITableCatalog` (what
these six consumers called before this phase) is the driver's raw catalog and cannot be scripted at all,
because its implementations are singletons registered once per `ConnectionDriverType`, not per
connection. So yes — a script that hides, renames or synthesises a column makes the two disagree for the
same table, and phase 29 already documents the consequence for the pre-phase-91 world: a synthetic
column mapped through the picker already failed at run time with "column not found", because the old
live path asked `ITableCatalog`, which had never heard of it.

**Conclusion: this is real, but it does not weaken phase 91's design — it strengthens the case for it.**
The column names a reader/writer looks up (`ColumnMapping.SourceColumn`/`TargetColumn`, a mapping's
`watermarkColumn` option, a segment column) are themselves chosen by an operator looking at the
`MetadataService`-backed picker — the same surface phase 90's cache is captured from. Switching these six
consumers onto the cache does not introduce a new universe of column names; it puts the run-time
consumers into the *same* universe the operator was already looking at when they configured the mapping,
which the old `ITableCatalog`-based path was not. The risk phase 91's plan doc raised — "a diverging
cache could report a column present-but-wrong instead of merely absent" — is a pre-existing property of
the scripted-metadata feature (phase 29 accepted it explicitly, including the open question "overriding
the pipeline's `ITableCatalog`... needs per-connection driver components," never built), not something
this phase introduces or could fix without redesigning driver registration. No behaviour change was made
in response to this finding; it is recorded here because the docs asked for the verification to be done
and the conclusion stated, not assumed.

### What was built

- **`MetadataNotCachedException`** (`DataSync.Drivers.Abstractions`), mirroring `PositionExpiredException`'s
  shape: carries `MappingName`, `Side` ("source"/"target") and an optional `Column` (null when the whole
  cache is empty, so there was nothing to look a name up in). Its message always ends in "Use Refresh
  metadata on the mapping."
- **`CachedMetadataLookup`**, beside it: `RequireColumn`/`RequireAll` extension methods on
  `IReadOnlyList<CachedColumn>`, and `ToColumnMetadata()`. Every one of the six consumers routes through
  this rather than reimplementing the empty/missing-column check six times.
- **Interface changes**: `IChangeReader.ReadChangesAsync`, `IStagingProvider.StageAsync` and
  `IChangeWriter.ApplyAsync` each gained `string mappingName` and a cached-column list
  (`sourceColumns`/`targetColumns`) inserted after `columnMappings`. This touched every implementation of
  all three interfaces, not just the six in scope — `MsSqlChangeTrackingReader`, `MsSqlCdcReader`,
  `DuckDbQueryReader`, `MsSqlBatchReloadReader` and `MsSqlStagingTableProvider` accept and ignore the new
  parameters, exactly as before. `RunExecutor`'s three call sites (`ReadChangesAsync`/`StageAsync`/
  `ApplyAsync`) now pass `mapping.Name` and `mapping.SourceColumns`/`mapping.TargetColumns`.
- **`WatermarkReader.ReadChangesAsync`**: the watermark column's shape comes from `sourceColumns`,
  resolved only when there is a bound to bind (a first pass has none). `DescribeAsync` (preview) is
  untouched and stays live — see "What's still not done" below.
- **`BatchReloadReader.ReadChangesAsync`**: the segment column's shape comes from `sourceColumns`,
  resolved only for a `ListSegment`/`RangeSegment` — a null/`FullSegment` reload never looks a column up
  at all, so an unrefreshed mapping's plain reloads are unaffected. `ExpandAutoSegmentsAsync` and
  `DescribeAsync` are untouched, confirmed live.
- **`TriggerAuditReader`**: `ResolveColumnsAsync` (live, `ITableCatalog`-backed) now serves only
  `DescribeAsync`'s preview; a new `ResolveColumnsFromCache` serves `ReadIncrementalAsync`, the actual run
  path. Both share a `SplitKeys` helper so the "no primary key" message is identical either way.
- **`ScriptedQueryReader.ReadChangesAsync`**: reads `sourceColumns` and converts it, but — deliberately —
  never throws on an empty cache. See "The one consumer with no empty-cache throw" below.
- **`TargetShape.FromCachedColumns`** (`DataSync.Drivers.Generic`) and
  **`MsSqlTargetShape.FromCachedColumns`** (`DataSync.Drivers.MsSql`, `internal`): new, cache-only
  siblings of each type's existing catalog-backed `LoadAsync`, which stays for preview. Confirmed by
  tracing each writer's `ApplyAsync`, not assumed from the shared method name: `DeleteInsertWriter`,
  `SnapshotWriter` and `Scd2Writer` call `TargetShape.FromCachedColumns`;
  `MsSqlDeleteInsertWriter`/`MsSqlMergeWriter`/`MsSqlMergeReconcileWriter` call
  `MsSqlTargetShape.FromCachedColumns` — all six, confirmed rather than assumed. Both resolve the *full*
  cached column set (not just the mapped columns), because `SegmentScope.Build`/`MsSqlSegmentScope.Build`
  can be asked to resolve a segment column that nothing in the mapping writes.
- **`BatchInsertStagingProvider.StageAsync`**: target column types come from `targetColumns` via
  `RequireColumn` per mapped column. `MsSqlStagingTableProvider` (the SqlBulkCopy-based staging
  provider) was **not** touched beyond the mechanical signature change — it isn't one of the doc's named
  target consumers, and stays live.
- **`RunFailureKinds.MetadataNotCached`** and **`NotificationKinds.MetadataNotCached`**, and a new
  `MetadataNotCachedException` catch block in `RunExecutor`, on the same footing as the existing
  `PositionExpiredException` one immediately above it. `TaskRunStore.NotifyIfNewlyFailed`'s existing
  switch grew one more case, mirroring `PositionExpired`'s — no new interface method, no new endpoint, no
  new journal operation. `NotificationsEndpointTests.MetadataNotCached` proves the real exception's own
  message survives into the notification, the same way `WatermarkExpiry` already proved it for
  `PositionExpiredException`.

### Judgment calls

- **A shared method, split in two, not made conditional.** `TargetShape`/`MsSqlTargetShape` gained a
  second static factory (`FromCachedColumns`) rather than a flag on `LoadAsync` that chooses cache-or-live.
  The plan doc's own rejected-design section is explicit that a live-fallback path must not exist even as
  a latent option; a boolean parameter one call away from "and fall back if the cache is empty" is exactly
  that latent option; two methods with no shared code path between them is what makes it structurally
  impossible instead of merely undesired.
- **Preview (`DescribeAsync`) stays live everywhere, deliberately.** The doc's six consumers are named by
  method — `ReadChangesAsync`, `ReadIncrementalAsync`/`ResolveColumnsAsync` (the run path only),
  `ApplyAsync`, `StageAsync` — not by class. A statement preview shows an operator today's real table
  before they save a mapping that might have no cache yet; switching it to cache-only would make the
  preview panel the first thing to break on every new mapping, for a feature (previewing SQL) that isn't
  a run and was never named in scope. `TriggerAuditReader` is the one class where this required an actual
  split (`ResolveColumnsAsync` vs. `ResolveColumnsFromCache`); the others already had two separate
  catalog-backed call sites for preview and run that only needed one of the two switched.
- **`BatchReloadReader` skips the cache lookup entirely for a null/`FullSegment` reload.** The pre-phase-91
  code queried the live catalog unconditionally, on every reload, whether or not `SegmentScope.Build`
  ever used the answer. Since the columns are only consulted for a `ListSegment`/`RangeSegment`, requiring
  a populated cache for an unsegmented reload would have failed the single most common case — "reload
  this whole table" — for every mapping that has never been touched since phase 90, for no reason: nothing
  downstream would have used the answer anyway. This is a deliberate narrowing in the *reader's* favour,
  not a loophole in the cache-only rule — segmenting still requires it, unconditionally.
- **The one consumer with no empty-cache throw: `ScriptedQueryReader`.** A query source names no table,
  so it has no catalog to introspect at all — phase 90's own capture already leaves `SourceColumns` empty
  for exactly this reader, by design, forever. Requiring `RequireAll` here would mean every
  `ScriptedQuery`-based mapping fails its very next run, unconditionally, which cannot be what "cache-only,
  fail loud" was asking for since there is no refresh action that could ever populate this reader's cache.
  `sourceColumns` is still threaded through and handed to the script's `SourceQueryContext` for any script
  that wants to use it — nothing is hidden — but an empty list is treated as this reader's normal state,
  not a missing refresh. This is a real, considered deviation from the "test per consumer: empty cache
  throws" template, and is called out explicitly rather than silently shipped.
- **"No live query happens first" is about catalog/introspection calls, not all I/O.** Every reader still
  opens the connection and calls `dialect.UseDatabaseAsync`/`ChangeDatabase` before touching the cache —
  that is plumbing every pass needs regardless of metadata, not the live catalog query phase 91 removes.
  `TriggerAuditReader.ReadChangesAsync` also still queries the shadow table's own max sequence before the
  cache is ever consulted; that query is about the shadow table's bookkeeping, not the source table's
  columns, and was already live before this phase.
- **The cache lookup inside an `async IAsyncEnumerable` method is lazy, same as the code it replaced.**
  `TriggerAuditReader.ReadIncrementalAsync`'s cache check does not run until something enumerates the
  returned rows — this was already true of the live `ResolveColumnsAsync` call it replaced, since both
  sit inside an iterator method. Not a regression; recorded because a test that calls `ReadChangesAsync`
  and expects a synchronous throw would be testing the wrong thing.

### Real bugs / near-misses found while implementing

- **`TargetShape.FromCachedColumns`'s first draft only validated the *mapped* columns.** Copying
  `TargetShape.LoadAsync`'s shape naively would have set `Columns` to just the mapped subset, which
  breaks `SegmentScope.Build` for a reload whose segment column isn't one either writer maps — a real
  behavioural difference from the live path, caught by tracing `DeleteInsertWriter.ApplyAsync`'s actual
  use of `shape.Columns` rather than assuming the constructor's shape from its old call site.
- **`MsSqlWatermarkReaderTests`' "keyless table" trigger-audit test** (mirrored in the Postgres suite)
  needed a cached column list *without* a primary key rather than an empty one — an empty cache would have
  raised `MetadataNotCachedException` and proven nothing about the "no primary key" message the test
  actually exists to pin. Fixed while updating call sites; recorded because it is the kind of thing an
  automated find-and-replace across ~15 files would have gotten wrong silently.

### How it was verified

- **`CachedMetadataLookupTests`** (new, `DataSync.Drivers.Abstractions.Tests`, 6 tests, fast): the shared
  lookup's every branch — found case-insensitively, empty cache throws naming no column, populated cache
  missing the column throws naming it, `RequireAll` converts in order or throws on empty.
- **`TargetShapeCacheTests`**/**`MsSqlTargetShapeCacheTests`** (new, 4 tests each, fast, no I/O at all):
  `FromCachedColumns` resolves a populated cache (full column set retained, identity detected), and throws
  on an empty or incomplete one — covering all six writers at their one shared choke point, which is a
  stronger proof than a per-writer fake-throws test since the cache-only overload has no `ITableCatalog`
  parameter to call in the first place.
- **Per-consumer "zero live catalog calls" and "empty cache throws first" tests** (`Category=Integration`,
  a `ThrowingTableCatalog : ITableCatalog` fake that throws if invoked — phase 87's technique for
  `IChangeCounterSource`), added to the existing real-server test files: `MsSqlWatermarkReaderTests`
  (`WatermarkReader`), `GenericPipelineTests` (`BatchReloadReader` + `BatchInsertStagingProvider`, plus a
  full-pipeline run proving all three stages avoid the catalog together), `TriggerAuditReaderTests`
  (`TriggerAuditReader`), `ScriptedQueryReaderTests` (`ScriptedQueryReader` — the zero-calls half only, per
  the judgment call above).
- **`NotificationStoreTests.AMetadataNotCachedFailure_...`** (new, `Category!=Integration`, fast): a
  `MetadataNotCached`-kinded `CompleteRun` produces exactly one notification of that kind, naming the
  mapping and column, through the unmodified `NotifyIfNewlyFailed` path.
- **`NotificationsEndpointTests.MetadataNotCached`** (new): the same proof with the *real*
  `MetadataNotCachedException`, mirroring `WatermarkExpiry`'s existing test for `PositionExpiredException`
  — the point being that the exception's own wording survives into the notification.
- **A mapping refreshed after this phase ships runs successfully on its next pass**: not a separate test.
  Every "populated cache → succeeds" test above (the pipeline tests, `TargetShapeCacheTests`) *is* that
  claim — the code path a refreshed mapping exercises and the code path a test that hands the cache
  directly exercises are identical, so a dedicated end-to-end "press Refresh, then run" test would have
  proven nothing the per-consumer tests do not already prove.
- **Full solution build**: `dotnet build DataSync.slnx` — 0 warnings, 0 errors.
- **`Category!=Integration`**: every failure traced to a pre-existing, sandbox-wide cause, confirmed by
  running the identical test on a clean-`HEAD` git worktree and getting the identical failure count:
  - **`DataSync.Api.Tests` (226 failed)**: every one is an HTTP 500 from `TestApiFactory`/
    `AuthenticatedApiFactory`-hosted endpoints — confirmed present on clean `HEAD` (261/367 failed
    unfiltered, same signature) before this phase touched a single line.
  - **`DataSync.Core.Tests` (38), `DataSync.State.Tests` (1 relevant), `DataSync.TaskRunner.Tests` (9),
    `DataSync.Cli.Tests` (1)**: all but one are `Dispose()`-time `UnauthorizedAccessException` deleting a
    temp git-object directory on Windows — a teardown failure, not an assertion failure — confirmed
    identical (38/170 exactly) on a clean-`HEAD` worktree. The Cli.Tests one is
    `InviteCommandTests.Invite_AgainstAnMsSqlConfiguredRepo_Succeeds`, the same pre-existing failure named
    in phase 90's own retrospective.
  - **`DataSync.Drivers.Generic.Tests` (2), `DataSync.Drivers.MsSql.Tests` (2)**: line-ending
    (`\r\n` vs `\n`) differences in statement-text golden strings, unrelated to any reader/writer this
    phase touched — pure `SqlDialect` rendering assertions.
  - None of these overlaps a file this phase changed the *behaviour* of; every new test this phase added
    passed. `Category=Integration`: 100% connection-refused failures against SQL Server/Postgres, the same
    documented absence of Docker/a live server every prior phase this session has hit.
- **`tsc -b`**: clean, no output. No SPA change was made or needed — `RunsPanel.tsx` already renders any
  `errorSummary`/`StatusBadge` for an unrecognised `failureKind`, and `failureKind: string | null` in
  `types.ts` needed no union update.

### What's still not done / explicitly out of scope

- **Preview (`DescribeAsync`) stays on the live catalog for every one of the six consumers.** Not named in
  the doc's scope, and switching it would break previewing an unrefreshed mapping's SQL — a strictly worse
  outcome for a feature this phase was never asked to touch.
- **`WithDerivedNaturalKeyAsync` in `RunExecutor`** still calls `sourceDriver.ListColumnsAsync` live to
  derive `Scd2Writer`'s natural key when nobody has configured one. This is phase 68's mechanism, not one
  of the six named consumers, and doing anything to it was out of scope — recorded so nobody mistakes its
  continued live call for something this phase missed.
- **`BatchReloadReader.ExpandAutoSegmentsAsync`** untouched, as confirmed in both planning docs.
- **No cache backfill, no migration.** An unrefreshed mapping using one of these six consumers fails its
  next run with `MetadataNotCachedException` — the intended signal, not a gap.
- **The `MetadataService`/`ITableCatalog` divergence itself is not resolved** — see above. It predates
  this phase (phase 29), is orthogonal to the cache-only design's correctness, and fixing it would mean
  per-connection driver component construction, which phase 29's own doc already flagged as its own,
  separate, unbuilt piece of work.
