# Dev harness: multiple tables, varying column widths, concurrent transaction volume

**Status: draft, 2026-08-29 — scoping against the current harness before writing an implementation
phase.**

## The ask

Expand `tools/DataSync.DevHarness` to seed and drive **multiple tables**, with **varying column
widths** (not one fixed 5-column shape), and to generate **concurrent** transaction volume across those
tables — not the single-threaded, one-table stream it produces today.

## What the harness actually does today

`Scenario.cs` is explicit about it: "The one dev scenario this harness stands up: a single `Orders` table."
Everything downstream assumes it:

- **One table, one fixed schema.** `Scenario.Columns` is a hardcoded 5-column array (`Id`, `Region`,
  `CustomerName`, `Amount`, `UpdatedAtUtc`), referenced by name throughout `SqlBootstrap.SeedAsync` and
  `Workload`'s insert/update/delete statement builders.
- **`seed` is bulk-load, already reasonably built.** `SqlBulkCopy` rather than parameterized `INSERT`
  (the comment explains why: SQL Server's 2100-parameter limit caps a multi-row `INSERT` at ~420 rows of
  this table). This part generalizes fine to more tables/columns; it's schema-agnostic in mechanism, just
  not in the column list it currently hardcodes.
- **`workload` is single-connection, sequential, not concurrent.** One `SqlConnection`, one transaction
  per loop tick, `await Task.Delay` between them. `--rate` names a *pace*, not *concurrency* — there is
  exactly one thing happening at a time today, regardless of the configured rate.

## What "multiple tables, varying widths" needs

- **`Scenario` stops being one fixed table.** It needs to describe a *set* of table definitions — name,
  column list, each column's type/width — rather than one constant array. "Varying column widths" reads
  as: some tables narrow (a handful of columns, useful for high-throughput scenarios), some wide (dozens
  of columns, the shape that already matters elsewhere in this codebase — `columnar-change-batches.md`'s
  benchmarks were explicitly run at 50 columns because narrow-table numbers don't predict wide-table
  behavior).
- **`seed`, `workload`, and `verify` all need to iterate the table set** instead of assuming one. This is
  mechanical for `seed` (the bulk-copy approach doesn't care how many tables it's called for) and for
  `verify` (already presumably table-scoped); `workload`'s statement builders need to become
  schema-driven — generating an `INSERT`/`UPDATE`/`DELETE` from a table definition rather than a
  hand-written literal SQL string per operation.

## Simplified scope (resolved 2026-08-29)

**No foreign-key/relational consideration.** This is about variety and volume — independent tables, not
a related set. Removes any need for coordinated key generation across tables.

**Configuration, kept small:**

- `--tables N` — a total table count. No per-table shape configuration; widths vary by a **standard,
  formulaic pattern keyed on table index**, so the scenario stays reproducible and nameable ("table 7")
  without an operator having to describe N table shapes by hand.

**The standard column-width pattern** (proposed, adjustable during implementation): table *i* (1-based)
always carries `Id` (int, PK) and `UpdatedAtUtc` (datetime) — every table needs a key and a watermark
column for the reader/writer machinery already assumed elsewhere in the harness — plus *i* additional
"filler" columns cycling through a small fixed set of representative types (string, decimal, int, bool,
date), so column count grows linearly and predictably with table index and every representative type gets
exercised across the table set without describing each table by hand.

## Concurrency model — revised 2026-08-30, replacing the single-loop interleaving design above

**The first pass here was wrong to call interleaving "concurrent."** It isn't — one connection issuing
one statement at a time is sequential regardless of which table it targets. The actual requirement is
real concurrent writes, evenly distributed across tables, with an explicit parallelism knob:

- **`--parallelism P`** (new): the tables are split into **P groups**, as evenly as `--tables N` allows
  (16 tables at `--parallelism 4` → four groups of four; an uneven split, e.g. 17 at `--parallelism 4`,
  gives groups of 5/4/4/4 rather than requiring `N` to divide evenly).
- **Groups run concurrently.** Each group is its own loop with its **own `SqlConnection`**, all P running
  at once (`Task.WhenAll`) — this is the actual concurrency: P simultaneous connections issuing
  transactions against the source at the same time, which is what a real "several mappings changing at
  once" scenario needs to look like.
- **Within a group, tables are visited in sequence** — a group's loop round-robins its own assigned
  tables one at a time, not concurrently within itself. This is the "perform those inserts in sequence
  from within the group" half of the requirement, and it's also what makes per-table distribution even:
  cycling through a fixed list in order gives every table in the group a turn on a regular schedule,
  rather than a random pick that can (by chance) favor some tables over others.
- **Rate is distributed evenly down to the table level, not just the group level.** Total `--rate R`
  splits across groups in proportion to each group's table count (so an uneven split still gives every
  *table* the same effective rate, not just every *group* the same rate), and within a group, the
  round-robin loop naturally spreads that group's share evenly across its own tables. End state: every
  table gets approximately `R / N` transactions/second, regardless of how groups happen to divide N.

## What this still needs, mechanically

- **`Scenario` becomes a generator**, not a constant: given `--tables N`, produce N table definitions by
  the pattern above, rather than one hardcoded array.
- **`Workload` is restructured around P concurrent group-loops**, each owning its own connection and its
  own round-robin cursor over its assigned tables, replacing today's single loop entirely (not the
  single-loop-with-random-pick design this doc previously described).
- **`seed` and `verify` iterate the generated table set** — unaffected by the concurrency change, since
  seeding and verification aren't rate-paced.
- **Per-table, per-group, and combined reporting**, so the periodic log line can show a group falling
  behind or a specific table going quiet, not just one aggregate number.

**Next step**: ready for an implementation phase doc — a `Scenario` generator plus a `Workload` rewrite
built around P concurrent, connection-owning group loops, each round-robining its own even share of the
tables. No remaining open design questions; what's left is implementation detail (the exact filler-type
cycle, log format, and how an uneven `N`/`P` split allocates the extra tables).

---

# Outcome

Agreed, as `implementation/todo/phase-057-dev-harness-multi-table-workload.md`.
