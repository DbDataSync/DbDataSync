# Phase 191S — Shared FROM/JOIN builder, subquery wrapping, and retiring the standalone query readers

**Status**: Built, except one deferral. See Retrospective.
**Plan reference**: `phase-190S-source-table-spec-query-and-allow-subquery.md` (the config field this phase
consumes). Extends `phase-187J-relationship-joins-in-the-batch-readers.md`/
`phase-188J-relationship-joins-in-the-change-readers.md`'s own join machinery.

## Why

Segmenting, auto-segmenting, and watermarking are all fundamentally the same operation — "load filtered
source data" — differing only in what predicate they apply, what they project, and whether they need an
ordering. Today that isn't how the code is shaped: `BatchReloadStatement.BuildRead` and
`WatermarkStatement.BuildRead` each independently decide whether the primary table needs a `base` alias and
a `JOIN`, `BatchReloadStatement.BuildRange` (the auto-segment MIN/MAX sampler) has none of that machinery at
all — it renders a bare, unaliased, unjoined `SELECT MIN/MAX FROM table` regardless of whether the mapping
has relationships — and `MsSqlBatchReloadReader` duplicates the same FROM/JOIN decision a third time, with
its own `MsSqlSegmentScope` wrapper and, worse, a live `MsSqlSchemaQueries.GetColumnsAsync` catalog call on
every pass that phase 91's cache-only migration never reached (confirmed: `ExpandAutoSegmentsAsync` in the
very same class already reads from cache and says so — `ReadChangesAsync`/`DescribeAsync` right next to it
never got the same treatment). Query-shaped sources (190S) need the same FROM-construction decision to
widen a third way: wrap as `(<query>) AS base` instead of `dialect.QualifyTable(...)`. Rather than teach that
to three divergent implementations, this phase unifies them into one.

## Decisions made (asked, not guessed)

1. **One shared FROM/JOIN/reference builder**, consumed by `BatchReloadStatement.BuildRead`, the
   auto-segment range query, and `WatermarkStatement.BuildRead` alike:
   ```
   SourceStatement.BuildFrom(dialect, source, relationships, relationshipAliases)
     → (fromClause, reference: string? relationship → Func<string,string>)
   ```
   `fromClause` is `dialect.QualifyTable(...)` for a table-shaped source or `(<query>) AS base` for a
   query-shaped one, with `RelationshipJoins.Render` appended whenever any relationship is referenced.
   `reference` is a single qualifier: `base.[col]` for the primary side, `{alias}.[col]` for a named
   relationship — the same lookup used identically in the SELECT list, a segment predicate, a watermark
   predicate, or the auto-segment MIN/MAX (192S is what actually threads transform-awareness through this;
   this phase only makes the *qualification* uniform).
2. **"Does `base` need aliasing" generalizes** from `relationshipAliases.Count > 0` alone to
   `relationshipAliases.Count > 0 || source.Query is not null`.
3. **`RelationshipAliases.Assign`'s "referenced" set widens** beyond `ColumnMappings` to also union in
   whatever a segment or watermark config references (192S adds those two references; this phase's job is
   only to make `Assign` accept them as additional inputs).
4. **Wrapping is conditional for reload, unconditional for watermark**, per 190S's decision 3 — restated
   here because it's this phase's builder that actually implements the difference: `BuildRead`'s caller
   decides per-pass whether *anything* needs wrapping (a segment, a relationship, a transform) and passes
   that decision in; `WatermarkStatement`'s caller always wraps once `source.Query is not null`, with no
   per-pass conditional at all — safe unconditionally because 190S's hard validation already guarantees
   `AllowSubquery = true` whenever a query-shaped source is paired with `Watermark`.
5. **`MsSqlBatchReloadReader`/`MsSqlSegmentScope` are retired outright**, not migrated. Investigation
   confirmed there is no genuine engine-specific reason for either to exist: `MsSqlSegmentScope.Build` is a
   one-line wrapper supplying only `MsSqlValueBinding.Instance` (an `ISegmentValueBinder`), which the generic
   `SegmentScope.Build`/`BatchReloadReader` already accept as a constructor parameter — every other engine
   (Oracle, Postgres, MySql, JDBC) already passes its own binder straight into the generic reader with no
   wrapper class, and `GenericPipelineTests.cs` already proves `new BatchReloadReader(MsSqlDialect.Instance,
   MsSqlValueBinding.Instance)` works. `MsSqlDriver.cs` registers both readers side by side today, under two
   different Kind strings (`MsSqlDriverKinds.BatchReload` and `GenericDriverKinds.BatchReload`) — history
   shows `MsSqlBatchReloadReader` predates the generic pipeline (phase 9 vs. phase 18) and was simply never
   retired once superseded. `MsSqlWatermarkReader`, referenced in a stale doc comment, doesn't exist — MsSql's
   real incremental reader is already just `new WatermarkReader(MsSqlDialect.Instance,
   MsSqlValueBinding.Instance)`, the working proof that the collapsed shape is sound.
6. **`RawQueryReader`/`DuckDbQueryReader`/`QuerySegmentTokens` are retired**, superseded by 190S's
   `SourceTableSpec.Query` + this phase's wrapping. The `Query`/`DuckDbQuery` Kinds stop being offered for new
   mappings (migration for existing ones tracked as 190S's open question).

## What this phase builds

- The shared `SourceStatement.BuildFrom` (exact type/namespace placement is this phase's own call — see Open
  questions), consumed by `BatchReloadStatement.BuildRead`, `BatchReloadStatement.BuildRange`, and
  `WatermarkStatement.BuildRead`.
- `BatchReloadStatement.BuildRange` gains real join support for the first time — today it renders no `JOIN`
  at all regardless of the mapping's relationships; after this phase it uses the same builder `BuildRead`
  does.
- Deletion of `MsSqlBatchReloadReader.cs` and `MsSqlSegmentScope.cs`; `MsSqlDriver.cs`'s
  `MsSqlDriverKinds.BatchReload` registration points at `new BatchReloadReader(MsSqlDialect.Instance,
  MsSqlValueBinding.Instance)` (already registered under `GenericDriverKinds.BatchReload` — this makes both
  Kind strings resolve to the same instance/behavior, closing the live-catalog-call bug as a side effect).
- Deletion of `RawQueryReader.cs`, `DuckDbQueryReader.cs`, `QuerySegmentTokens.cs`, and their Kind
  registrations.

## What this phase does not build

- Relationship/transform-consistent predicate *expressions* inside a segment or watermark's own bounds —
  the `reference` qualifier this phase adds only handles primary-vs-relationship qualification, not
  transform substitution. That's **192S**.
- The preview UI (max-rows, retry-without-subqueries, stale-metadata guard) — **193S**.
- Any change to `ScriptedQueryReader` (out of scope per the original mutual-exclusivity ruling — it has a
  real table and a compiled query builder, not a query-shaped `SourceTableSpec`).

## Open questions

1. **Exact placement of the shared builder** — a new static class, or a method added directly to
   `BatchReloadStatement` that `WatermarkStatement` calls into (it already imports from
   `DbDataSync.Drivers.Generic`) — not decided here.
2. **Whether Oracle/Postgres/MySql/JDBC have an analogous per-engine reload-reader duplicate** the way MsSql
   did. Not audited this session; worth a quick follow-up grep in the same shape as the anti-pattern sweep
   194S performed, since the MsSql case turned out to be a leftover from before the generic pipeline existed
   rather than something engine-specific, and the same history could apply elsewhere.
3. **Migration for the retired `Query`/`DuckDbQuery`/`MsSqlDriverKinds.BatchReload` Kind strings** — tracked
   in 190S, resolved there, executed here.

## How to verify

- Unit tests for `BuildRange`'s new join rendering: no relationships (byte-for-byte today's output), one
  relationship referenced only by a segment/watermark (not by any `ColumnMapping` — must still join), a
  query-shaped source (must wrap).
- Unit tests for every (table/query × no-relationship/relationship) combination across `BuildRead`,
  `BuildRange`, and `WatermarkStatement.BuildRead`, confirming identical qualification behavior across all
  three.
- An integration test proving MsSql's reload behavior (segment predicates, relationship joins, results) is
  unchanged after collapsing `MsSqlBatchReloadReader` into the generic reader.
- Confirm no currently-saved mapping references `MsSqlDriverKinds.BatchReload`/`Query`/`DuckDbQuery` without
  a working migration path in place first.

## Retrospective

**Built**: `BatchReloadReader`/`WatermarkReader` (and their statement builders, `BatchReloadStatement`/
`WatermarkStatement`) now support a query-shaped `SourceTableRef`. `BuildRead` gained a `query`/`wrapQuery`
pair: when `query` is set, the statement wraps as `(<query>) AS base` whenever `wrapQuery` (a real segment
or a generated transform — the two things the statement builder can't see for itself) or a relationship
join is present; absent all three, it returns the operator's own query text completely unwrapped, no
projection or predicate substituted at all. `BuildRange`/`BuildMaxWatermark` always wrap when `query` is
set, matching the design: there's no "run it unwrapped" alternative for an aggregate, and
`WatermarkStatement.BuildRead` always wraps too, matching the design's watermark-specific reasoning
(`ORDER BY` needed on essentially every pass). `BatchReloadReader.ReadChangesAsync`/`DescribeAsync`/
`ExpandAutoSegmentsAsync` compute the `AllowSubquery`-gated soft-suppression themselves — a query-shaped
source with subqueries disallowed gets an empty effective relationship list and a nulled-out effective
segment, so `needsWrap` and `joins.Length` both stay at zero and the statement builder's own unwrapped path
fires naturally, with no separate suppression branch needed. `WatermarkReader` needed no equivalent gate at
all — 190S's hard validation already guarantees `AllowSubquery = true` whenever `Watermark` pairs with a
query-shaped source, so it can (and does) just always wrap.

`MsSqlBatchReloadReader`/`MsSqlSegmentScope` are deleted. One real correction along the way: the original
file (`MsSqlSegmentScope.cs`) held *two* classes, not one — the retirable one-line `MsSqlSegmentScope`
wrapper, and `MsSqlValueBinding`, the actual `ISegmentValueBinder` implementation still used by
`MsSqlDeleteInsertWriter`/`MsSqlMergeReconcileWriter`/`MsSqlDriver` itself. An investigating subagent's
earlier report (used to plan this phase) said the file's only consumer was the reader being retired — true
of `MsSqlSegmentScope`, not of the file as a whole. Caught immediately by the build (`CS0103: the name
'MsSqlValueBinding' does not exist`) rather than by a deeper investigation; fixed by keeping the file
(renamed `MsSqlValueBinding.cs`) and deleting only the `MsSqlSegmentScope` class from it, updating its two
writer call sites and the corresponding test file to call `SegmentScope.Build(MsSqlDialect.Instance,
MsSqlValueBinding.Instance, ...)` directly instead of through the retired wrapper.

The `"MsSqlBatchReload"` Kind string is **not** dropped, contrary to a first-draft comment in this doc's own
history — `BatchReloadReader` gained an optional `kind` constructor parameter (defaulting to
`GenericDriverKinds.BatchReload`, mirroring `RawQueryReader`'s own existing "one reader, more than one
registered Kind" shape), and `MsSqlDriver` registers a second instance under `MsSqlDriverKinds.BatchReload`.
A mapping already saved against that Kind keeps resolving to a real reader — now correctly cache-only,
unlike before.

**Deferred, not dropped**: retiring `RawQueryReader`/`DuckDbQueryReader`/`QuerySegmentTokens` and their
`"Query"`/`"DuckDbQuery"` Kind registrations, plus the frontend's query-text-field relocation from a reader
option to `SourceTableSpec.Query`. Both touch the identical frontend surface (`QuerySourcePanel.tsx`) that
193S's preview redesign also has to touch — doing the field relocation once, alongside that work, avoids
two separate passes over the same component. Folded into 193S's scope; see that phase doc.

**Verified for real**: full solution build (`dotnet build`, root), 0 errors, 0 warnings. Full non-integration
suite green throughout (2,449+ passed, 0 failed). New unit tests added for every statement-builder
combination the design specifies: unwrapped-by-default, wrap-on-`wrapQuery`, wrap-on-relationship-alone
(`BuildRead`), always-wraps (`BuildRange`, `WatermarkStatement.BuildRead`/`BuildMaxWatermark`). **Not
verified**: the reader-level `AllowSubquery`-gated suppression logic inside `BatchReloadReader` itself has
no unit test of its own — this codebase's existing convention tests reader classes' actual DB-touching
behavior via `[Trait("Category", "Integration")]` tests against a live server, which this sandbox has none
of; the statement-builder tests cover the SQL-generation logic that suppression ultimately depends on, but
the reader-level orchestration (computing `effectiveSegment`/`wrapForFeatures` correctly from
`source.AllowSubquery`) is an honest gap for whoever next has a live server, the same way several existing
phase docs in this repo already name a live-server gap rather than assume it away.
