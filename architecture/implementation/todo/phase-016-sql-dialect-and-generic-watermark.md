# Phase 16 — SQL Dialect & Generic Watermark Reader (planned)

**Status**: Planned, not started
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
writer — phase 17. Any change to the MSSQL-specific readers, writers or staging, which keep their
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
