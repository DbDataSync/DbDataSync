# Phase 188J — Relationship joins in the change (incremental) readers

**Status**: Built. See Retrospective.
**Plan reference**: `phase-185J-declared-relationships-and-foreign-column-lookups.md` (superseded — that
doc had deferred CDC specifically; scope confirmed in conversation to include it here rather than defer it
a second time: joining against a captured-changes result set is mechanically the same as joining against
any other queryable rowset — alias it and join against it like any other table).

## Scope: three MsSql-family readers, this pass

- `WatermarkReader` (`src/DbDataSync.Drivers.Generic/WatermarkReader.cs`) — engine-neutral, so this one
  change covers every driver using it, the same "generic covers everyone, MsSql needs its own parallel
  edit" split 187J found for the batch readers.
- `MsSqlChangeTrackingReader` (`src/DbDataSync.Drivers.MsSql/MsSqlChangeTrackingReader.cs`).
- `MsSqlCdcReader` (`src/DbDataSync.Drivers.MsSql/MsSqlCdcReader.cs`).

Postgres's `PgLogicalSlotReader` and any MySQL-specific incremental reader are **not traced this session**
and are explicitly out of scope here — named as a follow-up, not assumed to be either easy or hard.

## What this phase builds

### `WatermarkReader`

Structurally identical to 187J's work: an ordinary `SELECT <projection> FROM table WHERE watermarkColumn >
@previous`, no existing join or alias. Same treatment — `SourceProjection.Render`'s relationship-aliasing
branch (186J/187J) applies unchanged, `WatermarkStatement.BuildRead`/`BuildMaxWatermark` (or wherever its
statement-building lives — not traced by class name this session, presumably a sibling `WatermarkStatement`
static class matching `BatchReloadStatement`'s own shape) gains the same `relationships` parameter and
`LEFT JOIN` rendering as `BatchReloadStatement.BuildRead`, with the primary table aliased `base` once any
relationship is present. `BuildMaxWatermark` (used for `CapturePositionAsync`/`ChangesFromLatest`) is
**not** touched — it aggregates the primary table's own watermark column, never a joined one.

### `MsSqlChangeTrackingReader`

Already joins `CHANGETABLE(...)` to the base table under a `base` alias — its own existing comment already
requires `{{column}}` to resolve to `base.[Col]`, "or... it has to be `base.[Col]` to be unambiguous against
CHANGETABLE's own copy" of the key. Adding a relationship join here is an extension of a join that already
exists, not a new one: render an additional `LEFT JOIN <foreign> AS r{i} ON base.[LocalColumn] =
r{i}.[ForeignColumn]` clause alongside the existing `CHANGETABLE ... LEFT JOIN dbo.Table AS base` join, and
extend the reader's own column-rendering helper (the one already producing `base.[Col]` today) to also
produce `r{i}.[Col]` for a relationship-sourced `ColumnMapping`, reusing the same alias-assignment logic
187J's `SourceProjection.Render` change introduces (or a shared helper both call, if the same rendering
function turns out to be reusable as-is — worth checking once 187J's shape is settled, not decided here).

### `MsSqlCdcReader`

Reads directly from `cdc.fn_cdc_get_net_changes_<instance>(...)`/`fn_cdc_get_all_changes_<instance>(...)` —
a table-valued function call, not the base table — with no join today (`RenderColumn`'s own comment: "No
join here, so `{{column}}` resolves to the bare quoted name"). That function call gets aliased (e.g. `AS
ct`) and joined against exactly like any other queryable rowset — a table-valued function's result set is
an ordinary source for a `JOIN` in T-SQL, no different in kind from joining two tables. Concretely, in
`MsSqlCdcStatement.BuildRead`: alias the function call, render `LEFT JOIN <foreign> AS r{i} ON
ct.[LocalColumn] = r{i}.[ForeignColumn]` per referenced relationship, and update `RenderColumn` (currently
returning bare quoted names, per the comment above) to qualify with `ct.` once any relationship is present
— matching the exact pattern 187J/the CT case above already establish, just against a function-call alias
instead of a real table alias.

One thing worth naming plainly, not as a blocker: the relationship's looked-up columns reflect the foreign
table's state *at scan time*, not *as of the change* the CDC row itself represents — CDC's own natively
captured columns keep their as-of-change values (untouched by this), only the newly-added lookup columns
have current-state semantics. This is the same behavior every other reader in 187J/this phase already has
(a batch reload, `WatermarkReader`, and Change Tracking's own base-table join are all already "current
state" reads for the primary table too), so it's not a new inconsistency this feature introduces — CDC is
simply the one reader in this codebase whose own native columns previously had a *different* guarantee
(exactly as-of-change) than everything else, and the lookup columns this phase adds don't inherit that
guarantee. Not an open question; stated here so nobody rediscovers it mid-implementation and mistakes it for
a bug.

## What this phase does not build

- No config shape, validation, or `IChangeReader` signature change — 186J, a hard prerequisite.
- No batch/reload reader changes — 187J, independent.
- No Postgres logical-replication or MySQL binlog reader support.
- No frontend — 189J.
- No change to `MsSqlCdcCatalog`, `MsSqlCdcStatement.CdcFunction` selection (net vs. all changes), LSN
  handling, or any of `MsSqlCdcReader`'s position/watermark logic — the join is additive to the statement's
  `FROM`/`SELECT`, nothing about how positions are captured or compared changes.

## Open questions

1. **`WatermarkReader`'s exact statement-building class/method names** — referred to generically above as
   "`WatermarkStatement`"; not confirmed against the actual file this session, only inferred from
   `BatchReloadStatement`'s parallel shape.
2. **Whether the CT and CDC readers' column-rendering helpers can share one implementation** with 187J's
   `SourceProjection.Render` relationship branch, or need their own — both already have their own
   `RenderColumn`-shaped private method distinct from `SourceProjection.Render` (unlike the batch readers,
   which call `SourceProjection.Render` directly). Worth a real look once 187J lands, not decided here.
3. **Multiple relationships' alias numbering staying consistent** between the `FROM`/`JOIN` clause and the
   `SELECT` list in the CDC/CT statements' own existing hand-rolled builders (as opposed to 187J's shared
   `BatchReloadStatement`) — same concern 187J names for `SourceProjection.Render`, needs the same
   resolution, not necessarily the same code.

## How to verify when built

- Unit tests for each of the three readers' new `JOIN` rendering, no live server needed (matching the
  existing pattern of statement-builder tests in this codebase): single/multiple join keys, multiple
  relationships, an unreferenced relationship rendering no join, a self-join relationship.
- Integration tests against a real SQL Server source, one per reader, proving `LEFT JOIN` behavior end to
  end exactly as 187J's does: a row with a matching foreign key gets the looked-up value; a row with no
  match keeps its own change (insert/update/delete) intact with the looked-up column `null`.
- For `MsSqlCdcReader` specifically: a test that changes the *foreign* table's row after the CDC change was
  captured, and confirms the next read reflects the foreign table's now-current value — proving the
  current-state semantics named above are real, not assumed.
- Existing tests for all three readers, with zero relationships declared, pass unchanged — behaviorally
  invisible to every mapping that doesn't use this feature, same bar 187J holds itself to.

## Retrospective

Built as designed for all three readers, with the open questions resolved along the way and one real
deviation each phase-comparable to what 187J found — not assumed up front, found by writing the code and
the tests it called for.

**Open questions, resolved:**

1. **`WatermarkReader`'s statement class is `WatermarkStatement`, in the same file/namespace as
   guessed** (`src/DbDataSync.Drivers.Generic/WatermarkStatement.cs`) — `BuildRead` took the same
   `relationships`/`relationshipAliases` parameters `BatchReloadStatement.BuildRead` did, reusing the same
   shared `RelationshipJoins.Render` helper (see below). `BuildMaxWatermark` untouched, exactly as planned
   — it aggregates the primary table's own column, never a joined one.
2. **The CT/CDC readers' column-rendering helpers do *not* share code with `SourceProjection.Render`
   directly** — they're structurally different (CT/CDC always select every primary-table column their own
   way; only relationship columns are new), but they share the *values* it computes:
   `SourceProjection.RenderExpression` (already public) renders a relationship column's Transform, and a
   new `RelationshipColumns.Distinct` (mirroring `SourceProjection.Render`'s own SELECT-list dedupe) picks
   out which `ColumnMapping`s to append. Both new readers call these rather than reimplementing them.
3. **Alias numbering stays consistent** the same way 187J's did: `RelationshipAliases.Assign` is called
   once per read/preview and its result threaded into both the SELECT list and the JOIN clause — extended
   this phase into a shared `RelationshipJoins.Render` (used by `BatchReloadStatement`, `WatermarkStatement`,
   `MsSqlChangeTrackingStatement`, and `MsSqlCdcStatement` alike, parameterised on the join condition's own
   base-side alias — `base` for the batch/watermark/CT readers, `ct` for CDC's own function-call alias) —
   extracted out of `BatchReloadStatement` in this phase rather than duplicated a third and fourth time.

**Real deviations, found by testing, not assumed:**

- **A second name-collision hazard, worse than 187J's, specific to CT/CDC's whole-row schema.** 187J's own
  ambiguity fix was a SQL-level "which table's Id did you mean" error. CT and CDC build `ChangeSchema` as a
  flat list of column *names* with no dedupe at all (unlike the mapping-driven batch/watermark readers) —
  a relationship column sharing a name with a column the schema already has would not error, it would
  silently have `ChangeSchema`'s own name-to-ordinal dictionary let the later entry win, handing one of the
  two columns the other's slot. New `RelationshipColumns.EnsureNoNameCollision` throws a named, actionable
  error the moment this would happen, in both readers, rather than letting it silently misdeliver a value.
  Not found by the integration tests (neither test's fixture happened to create the collision) — found by
  tracing what `ChangeSchema`'s constructor actually does with a duplicate name before writing the reader
  code, the same way 186J's own retrospective found its "no helper needed" conclusion by tracing the actual
  consumer rather than assuming.
- **`MsSqlCdcReader.ColumnsFor` had to stop treating a relationship-sourced mapping as a wanted CDC column.**
  Its own "wanted" set previously came straight from every `ColumnMapping.SourceColumn` — a relationship
  column names a column on the *foreign* table, not one this capture instance ever captured, and without
  the fix it would fail with a false "does not capture" error the moment a mapping used a relationship at
  all. Caught the same way: read what the method actually computes before writing the caller.
- **CDC's own bounded-read shape (a capping derived table with a separately-rebuilt outer `SELECT`) meant
  the relationship join has to live *inside* the derived table, not outside it** — joining outside would
  either need to repeat the join per output row after capping (correct but wasteful) or, worse, joining
  against an already-capped table with no path back to the relationship's local column. Joined inside, the
  outer query just re-selects the already-joined column by its own alias, the same pattern it already used
  for `OrderingColumn`/`ChangedAtColumn`.
- **Verified for real**: unit tests for all three statement builders (single/multiple join keys, multiple
  relationships aliased `r0`/`r1`, an unreferenced relationship rendering no join, a self-join, and — for
  CT/CDC specifically — the position/ordinal-preserving append order under both bounded and unbounded
  shapes) — 8 in `WatermarkStatementTests`/new, 3 in `MsSqlChangeTrackingStatementTests`, 3 in
  `MsSqlCdcStatementTests`. Integration tests against real servers, one per reader plus the CDC-specific
  current-state proof: `PostgresPipelineTests.Watermark_WithARelationship_...` (proving the shared
  `Drivers.Generic` `WatermarkReader` path), `MsSqlChangeTrackingReaderTests.Reader_WithARelationship_...`,
  and two on `MsSqlCdcReaderTests` — the ordinary matched/unmatched proof, plus
  `Reader_WithARelationship_ReflectsTheForeignRowsCurrentState_NotItsStateAsOfTheChange`, which updates the
  foreign row *after* the source change was captured and confirms the next read reflects the new value, not
  the one live at capture time — proving the "current state, not as-of-change" semantics named above are
  real rather than assumed. Full suites re-run clean: `DbDataSync.Drivers.MsSql.Tests` 271/271,
  `.Postgres.Tests` 120/134 (the same pre-existing `PgLogicalSlotTests`/`wal_level = replica` container
  misconfiguration 186J/187J's own retrospectives already name, nothing this phase's diff touches),
  `.MySql.Tests` 62/62, `.Oracle.Tests` 58/58, `.Jdbc.Tests` 32/32, `.DuckDb.Tests` 33/33, `.Generic.Tests`
  225/225, `.Descriptor.Tests` 33/33, and a full solution build clean.
- **Not built**: Postgres logical replication / MySQL binlog reader support, and the frontend (189J) — both
  explicitly out of scope per this doc, unchanged.
