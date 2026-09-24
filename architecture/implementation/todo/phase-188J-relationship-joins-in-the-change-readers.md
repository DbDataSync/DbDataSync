# Phase 188J — Relationship joins in the change (incremental) readers

**Status**: Planned, not started. Depends on `architecture/implementation/done/phase-186J-relationship-config-and-shared-reader-plumbing.md` (built).
Independent of `phase-187J-relationship-joins-in-the-batch-readers.md` (no shared code path beyond what
186J already provides — can be built in either order, or in parallel).
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
