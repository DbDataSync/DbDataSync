# Phase 57 — Dev harness: multiple tables, varying widths, concurrent grouped workload (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/dev-harness-multi-table-concurrent-workload.md`

## What this covers

`tools/DataSync.DevHarness` grows from one hardcoded table (`Scenario`'s `Orders`) to a generated set of
N independent tables of varying width, driven by **P concurrent group loops** — real parallel
connections, each round-robining its own even share of the tables — with no relational/foreign-key
structure between them.

**Revised 2026-08-30**: the first version of this phase described a single connection interleaving
random table picks and called that "concurrent." It isn't. This version replaces that design with real
concurrency, per the planning doc's revision.

## 1. `Scenario` becomes a generator

Replace the hardcoded `Table`/`Columns` constants with a function of `--tables N`: for table index
*i* (1-based), generate a definition carrying:

- `Id` (int, PK) and `UpdatedAtUtc` (datetime) — always present, since the reader/writer/watermark
  machinery elsewhere in the harness already assumes both exist.
- *i* filler columns, cycling through a small fixed set of representative types (string, decimal, int,
  bool, date) — table 1 gets one filler column, table N gets N, so width grows predictably with index and
  every representative type gets exercised across the set without hand-describing each table.

Table naming: something reproducible and referenceable (e.g. `Table1`, `Table2`, …).

## 2. `seed` iterates the generated set

`SqlBootstrap.SeedAsync`'s `SqlBulkCopy` approach is already schema-agnostic in mechanism — it needs a
table definition's column list instead of `Scenario.Columns`, called once per generated table.

## 3. `workload` runs P concurrent group loops

`--parallelism P` (new flag). Tables are split into P groups as evenly as `--tables N` allows (extra
tables distributed one-per-group starting from the first, so 17 tables at `--parallelism 4` gives groups
of 5/4/4/4 rather than requiring an exact divisor).

`Workload.RunAsync` is restructured around one async loop **per group**, all started together and awaited
via `Task.WhenAll`:

- **Each group loop owns its own `SqlConnection`** — this is what makes the concurrency real: P
  simultaneous connections against the source, not one connection picking targets quickly.
- **Each group gets a rate share proportional to its table count** (`R * groupTableCount / N`), so every
  *table* ends up with the same effective rate (`≈ R / N`) regardless of how unevenly `N`/`P` happened to
  split.
- **Within a group, tables are visited in sequence** — a round-robin cursor over the group's own table
  list, one table per tick, cycling back to the start. This is both the "in sequence from within the
  group" requirement and what makes per-table distribution even (a fixed rotation, not a random pick that
  can favor some tables over others by chance).
- **Insert/update/delete choice per turn stays random**, using the existing 50/35/15 split — only *which
  table* gets picked changes from random (rejected) to round-robin-within-group (this phase). Each table
  needs its own live-id list (today's single `liveIds` becomes one per table), so an update/delete always
  targets a real row of the table it's currently visiting.

## 4. Reporting

The periodic log line reports combined, per-group, and per-table counts, so a group falling behind or a
specific table going quiet is visible rather than hidden inside one aggregate number.

## What this phase does not build

- Any relational/foreign-key structure between generated tables.
- Per-table shape configuration beyond the standard formulaic pattern — an operator gets `--tables N`,
  not a way to hand-describe N distinct shapes.
- Dynamic re-balancing of groups mid-run (e.g. if one group's target table is slower than another's) —
  the group/table assignment is fixed for the run's duration.

## How to verify when built

- `--tables 5` seeds five tables with the expected, increasing column counts and types.
- `workload --rate R --tables 16 --parallelism 4` runs four concurrent connections, each cycling its own
  four tables in a fixed rotation — confirmed by observing four simultaneous connections/sessions against
  the source, not just by aggregate throughput.
- Every table ends up with an approximately equal share of `R` over the run, whether `N`/`P` divides
  evenly or not (test both an even split and an uneven one, e.g. 17 tables at parallelism 4).
- The periodic log line's per-table and per-group counts sum to the combined total.
- `verify` checks all generated tables, not just one.
- A full harness cycle (`up` → `seed` → `workload` → `verify`) succeeds with `--tables`/`--parallelism`
  both greater than 1, exercising a multi-mapping, multi-connection replication scenario end to end.

## Open questions

- The exact filler-type cycle and naming convention — implementation detail, not a design question.
- Default value for `--parallelism` when omitted (1, matching today's fully-sequential behavior, is the
  obvious choice, keeping the simple case simple).
