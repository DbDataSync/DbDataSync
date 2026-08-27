# Phase 17 — SQL Dialect & Generic Watermark Reader

**Status**: Built
**Plan reference**: `architecture/planning/done/additional-database-drivers.md`.

## Why this phase first

Five new drivers are coming, and the temptation is to build one and generalise afterwards. The
opposite order is cheaper here, because there is already a working engine to prove a generic layer
against: **build the dialect abstraction now and prove it against MSSQL**, where a regression is
caught by the existing suite rather than discovered in a driver nobody has used yet.

This phase is deliberately the smallest slice that establishes the pattern — one abstraction, one
implementation moved onto it, no new engine.

## What this phase will build

**`DataSync.Drivers.Generic`** — a new project, and the namespace every engine-neutral implementation
lives in from now on.

**`SqlDialect`** — the small variations, and nothing more:

```csharp
public abstract class SqlDialect
{
    public abstract string QuoteIdentifier(string identifier);
    /// <summary>`@p` on SQL Server and MySQL, `:p` on Oracle, `$1`-style or `@p` on Npgsql.</summary>
    public abstract string ParameterReference(string name);
    /// <summary>Some providers want the placeholder name without its sigil when binding.</summary>
    public abstract string ParameterName(string name);
    public virtual string QualifyTable(string schema, string table) => …;
}
```

It is explicitly **not** an attempt to abstract over engines. Anything that differs structurally —
bulk loading, upsert syntax, identity handling, catalog queries — gets an engine-specific
implementation with a prefixed Kind, per the naming rule settled in the planning doc. The dialect
exists so that a statement whose *shape* is identical everywhere does not need six copies.

**The watermark reader moves and becomes dialect-driven.** `MsSqlWatermarkReader` becomes
`Generic.WatermarkReader`, taking a `SqlDialect`. Its Kind stays the bare string `"Watermark"` —
which the planning doc settles as correct for a generic implementation rather than the oversight it
looked like. **No config migration**: existing replications naming `"Watermark"` keep working
unchanged, because the Kind is the same string and the SQL it generates against MSSQL is the same SQL.

**Generic segment predicate rendering.** `MsSqlSegmentScope`'s predicate construction (`Full` → `1 = 1`,
`List` → `IN (…)`, `Range` → half-open bounds) is shape-identical on every engine; only quoting,
placeholders and typed parameter creation differ. It moves to `Generic.SegmentScope` over a dialect,
with parameter *creation* left to the driver — a `DbParameter` is provider-specific and typed binding
is where engines genuinely diverge (see `MsSqlValueBinding`).

**`MsSqlDialect`** in the MSSQL driver, and `MsSqlDriver` registers the generic watermark reader with
it. `MsSqlWatermarkReader` is deleted rather than left as a duplicate.

**One SPA addition**: wherever watermark mode is selectable, say plainly that it does not detect
deletes. Per the planning doc this is a statement, not a restriction — append-only and
append/update-only tables are well served by watermark mode with nothing else attached, and requiring
a reconciling reload would tax all of them to prevent a misuse the operator is better placed to judge.

## What this phase does not build

Any new driver or `ConnectionDriverType` member. The generic batch-reload reader, staging provider and
writer — phase 18. Any change to the MSSQL-specific readers, writers or staging, which keep their
prefixed Kinds and their bespoke implementations.

## How to verify when built

- Unit tests on `SqlDialect` and `Generic.SegmentScope` rendering, per dialect, with no server — the
  same shape as `MsSqlSegmentScopeTests`, which is the model.
- **The existing MSSQL suite is the real proof.** `MsSqlWatermarkReaderTests` must pass unchanged
  against the generic reader driven by `MsSqlDialect`; if the generated SQL differs at all, that test
  says so.
- A test asserting the generated statement is byte-identical to what `MsSqlWatermarkReader` produced,
  so "no config migration" is a checked claim rather than an assertion.
- `dotnet build` clean; full suite green; `tsc -b` clean for the SPA note.

## Open questions

- Whether `SqlDialect` should carry a row-limit form (`TOP n` vs `LIMIT n` vs `FETCH FIRST n ROWS`).
  Nothing needs it yet; adding it now would be inventing a requirement. Flagged because the first
  thing that needs it should add it rather than work around it.
- Whether typed parameter binding can be expressed on the dialect at all, or must stay per-driver.
  `MsSqlValueBinding` maps a column's native type to a `SqlDbType` — the shape generalises, the type
  enum does not. Start per-driver and extract only if two drivers demonstrably agree.

---

# Retrospective

Built as planned. `DataSync.Drivers.Generic` exists, the watermark reader and segment predicate
rendering live in it, and the MSSQL driver is the first thing sitting on top of it. Both open
questions are still open, deliberately.

## What moved, and what did not

`SqlDialect` carries four things: `QuoteIdentifier`, `ParameterReference`, `ParameterName`, and
`QualifyTable`. Nothing was added speculatively — the row-limit form the plan flagged is still absent,
because nothing needs it yet.

One thing *was* added beyond the plan: `SqlDialect.UseDatabaseAsync`, virtual, defaulting to
`DbConnection.ChangeDatabase`. Not speculation — `ChangeDatabase` is a line in the very method being
generalised, and it is not universal (Oracle's provider throws from it, because a connection there
cannot change database). Leaving a hard `ChangeDatabase` call in an engine-neutral reader would have
been a known dead end shipped on purpose.

`ResultSetSchema` and `DbCommandExtensions` moved to `Generic` and became public. Neither was ever
SQL Server-specific; they were internal to the MSSQL driver only because that was the only driver.

Two things stayed with the driver, both correctly:
- **`MsSqlValueBinding`.** It maps a column's native type to a `SqlDbType`. The *shape* generalises;
  the type enum does not, and there is no common ancestor to return. It now implements
  `ISegmentValueBinder`, which is the seam — the generic renderer asks for a `DbParameter` and does
  not care how it was typed. The plan's second open question ("can typed binding be expressed on the
  dialect at all") is unchanged: extract only once two drivers demonstrably agree.
- **`MsSqlSegmentScope`**, reduced to four lines that bind the driver's dialect and binder to
  `SegmentScope.Build`. Keeping the name means the thirteen existing `MsSqlSegmentScopeTests` still
  exercise the exact call path the readers and writers use, which is what makes them a regression
  check on the move rather than tests of something that no longer runs.

## Placeholders come from the dialect, not from the parameter

The obvious way to render `IN (@a, @b)` is to join the created parameters' own `ParameterName`s —
which is what the MSSQL code did, correctly, because SQL Server's two spellings are the same string.
They are not the same string everywhere: Oracle binds `__segMin` and references `:__segMin`. So
`SegmentScope` renders placeholders from `dialect.ParameterReference` and names parameters with
`dialect.ParameterName`, and `BoundParameterNames_ComeFromParameterName_NotFromTheStatementPlaceholder`
pins the distinction before a driver that cares exists to notice it was lost.

## Delete detection is declared, not inferred

The SPA note the plan asked for is driven by a new `IChangeReader.DetectsDeletes` — a default
interface member returning false, overridden to true by `MsSqlChangeTrackingReader` — surfaced through
`ReaderCapability` and rendered by `readerNotes()`. String-matching `"Watermark"` in the SPA would
have been three lines shorter and would have gone stale the moment a second engine's watermark reader
or a CDC reader appeared, which is the failure `DriverCapabilities` was built to avoid in the first
place.

It defaults to **false** because that is the safe answer for a reader whose author has not thought
about it: overstating the guarantee is what loses data. Batch reload therefore reports false too,
which is accurate — its deletes are the reconciling writer's job, not the reader's.

The note reads "does not detect deletes" and nothing is disabled. Watermark mode stays fully
available, because append-only and append/update-only tables are exactly what it is for.

## Verification

- `WatermarkStatementTests` — seven tests pinning the generated SQL to the exact text the deleted
  `MsSqlWatermarkReader` produced, which is what makes "no config migration" checked rather than
  asserted. The statement building was extracted into `WatermarkStatement` to make that possible,
  following `MsSqlChangeTrackingStatement`'s existing precedent.
- `SegmentScopeTests` — nine tests rendering every segment shape through two deliberately different
  dialects (bracket/`@`, and double-quote/`:` with a sigil-less bound name). The dialects are restated
  in the test project rather than imported from the MSSQL driver, so a change there cannot silently
  redefine what "generic" means.
- **The existing MSSQL suite is the real proof, and it is unchanged in substance.**
  `MsSqlWatermarkReaderTests` now constructs `new WatermarkReader(MsSqlDialect.Instance)` — one line —
  and every assertion is untouched. `MsSqlSegmentScopeTests` reach through `DbParameter` to
  `SqlParameter` for the typed-binding assertions, which is the only change the seam forced.
- Full .NET suite green: 229 tests across seven projects. Playwright: 12 green, with the watermark
  note asserted on the reader picker and asserted *absent* on Change Tracking.
- `tsc -b` clean; `oxlint` unchanged at three pre-existing warnings.

## Still open

Both of the plan's open questions, untouched — the row-limit form (nothing needs it) and dialect-level
typed binding (one driver cannot demonstrate agreement). No new driver, no new `ConnectionDriverType`,
no change to the MSSQL-specific readers, writers or staging beyond the seam.
