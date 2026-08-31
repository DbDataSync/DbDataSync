# Phase 57 — Dev harness: multiple tables, varying widths, concurrent grouped workload

**Status**: Complete
**Plan reference**: `architecture/planning/done/dev-harness-multi-table-concurrent-workload.md`

## What this covers

`tools/DataSync.DevHarness` grew from one hardcoded table (`Scenario`'s `Orders`) to a generated set of
N independent tables of varying width, driven by **P concurrent group loops** — real parallel
connections, each round-robining its own even share of the tables — with no relational/foreign-key
structure between them.

## 1. `Scenario` is a generator

`--tables N` produces N `HarnessTable` definitions. Table *i* carries `Id` (int, PK), *i* filler
columns cycling through `Text`/`Decimal`/`Int`/`Bool`/`Date`, and `UpdatedAtUtc` — so table 1 is three
columns wide, table 17 is nineteen, and every representative type is exercised across the set. Tables
are named `Table1`…`TableN`; each filler carries its type and ordinal in its name (`Text1`,
`Decimal2`, `Int3`, …).

## 2. `seed`, `verify`, `drift` and `up` iterate the set

`SqlBootstrap.SeedAsync` bulk-copies per table (the mechanism was already schema-agnostic; only the
hardcoded column list was not). `Verifier` merge-joins per table and reports per table. `Drift` injects
into every table. `ApiClient` configures one table mapping per table on the one replication.

## 3. `workload` runs P concurrent group loops

`--parallelism P` splits the tables into P groups (remainder distributed one per group from the first,
so 17 at P=4 gives 5/4/4/4). Each group loop **owns its own `SqlConnection`**, all started together and
awaited via `Task.WhenAll`. Each group's rate share is proportional to its table count, so every
*table* sees ≈ `R / N`. Within a group, tables are visited in a fixed rotation. Insert/update/delete
choice per turn stays random on the existing 50/35/15 split.

## 4. Reporting

The periodic line reports combined, per-group and per-table counts.

## What this phase does not build

- Any relational/foreign-key structure between generated tables.
- Per-table shape configuration beyond the formulaic pattern.
- Dynamic re-balancing of groups mid-run.

---

# Retrospective

Mostly a mechanical widening — one hardcoded table becoming a generated set touches every verb, but
each change is the same change. The two parts that were not mechanical were the concurrency model and
a pre-existing break that this phase's own verification walked straight into.

## The concurrency is the point, and it is observable

The revised plan doc was right to reject the first design. One connection issuing one statement at a
time is sequential no matter which table it aims at, so the test that matters is not throughput but
**how many sessions the source actually has open**. Running `workload --tables 17 --parallelism 4` and
querying `sys.dm_exec_sessions` on the source mid-run returns 4. That is the claim, checked directly
rather than inferred.

## Proportional rate is what makes an uneven split even

The obvious implementation gives each group `R / P`. That is wrong whenever `N` does not divide by `P`:
at 17 tables across 4 groups the 4-table groups' tables would each run 25% hotter than the 5-table
group's. Sharing the rate in proportion to group size instead (`R * groupTables / N`) makes every table
converge on `R / N`.

Measured, not assumed: 17 tables at `--rate 34 --parallelism 4` for 20s produced 39 or 40 transactions
for **every one of the 17 tables**, across groups of 5/4/4/4. The per-table and per-group figures also
sum exactly to the combined total, which is the reporting requirement checked at the same time.

## Round-robin, not random pick

A random table per turn distributes evenly only in the limit. Over a 20-second run it does not, and the
harness's whole job is to make a table that went quiet *mean something* — a random pick makes an idle
table indistinguishable from a bug. The fixed rotation gives every table its turn on a schedule, which
is why the per-table counts above differ by at most one.

## `up` had been broken since the auth phases, and this phase found it

`tools/dev-harness up` fails at `Configuring connections…` with **401 Unauthorized** on the first PUT.
Phases 52 and 53 put identity in front of the API; the harness configures the scenario through that
same API on purpose, and nothing taught it to authenticate. There is no user yet either, and the
first-run bootstrap is an invite link meant for a browser — so the harness could not have logged in if
it had tried.

Fixed by having the harness set `DataSync__Auth__Disabled=true` on **the API process it starts
itself** — the escape hatch `AuthOptions` already documents, on a loopback port, against a scratch repo
it deletes. `--no-app` points at somebody else's API and deliberately does not touch its configuration.

This was not in scope, and it is recorded here rather than folded in silently: without it the phase's
own end-to-end verification step was unreachable, and every harness user since phase 52 has been
hitting it.

## Decisions the phase doc left open

- **Filler cycle order: Text first.** Not arbitrary. Table 1 has exactly one filler column, and the
  single-table scenario it replaces had a low-cardinality `Region` string specifically so list
  segmenting had something to bite on. Putting `Text` first means *every* generated table keeps that,
  including the narrowest.
- **Naming: `Table{i}`, and fillers named `{Type}{ordinal}`.** A column identifies both what it is and
  where it sits, so a `verify` failure reading "Decimal7 is … at the target" is legible without the
  schema in front of you.
- **`--parallelism` defaults to 1**, matching the old fully-sequential behaviour, per the phase doc's
  own suggestion. `--parallelism` greater than `--tables` is refused rather than silently creating
  empty groups.
- **`--tables` is read like `--target-engine` is**, with a `DATASYNC_HARNESS_TABLES` environment
  variable, because every verb has to agree on N. `verify` looking for tables `up` never created would
  report a difference that is really a forgotten flag — exactly the failure mode the target-engine
  variable already exists to prevent.
- **`seed --rows` is per table, not split across them.** Widths vary by design; row counts do not, so a
  per-table difference in replication lag is about the shape rather than about the volume.
- **No unit test project for the harness**, matching the existing convention for `tools/` — the harness
  is itself the verification instrument, and it was verified by running it.

## Verification

All against the live containers:

- `reset --tables 5` → `Table1 (3 cols), Table2 (4 cols), Table3 (5 cols), Table4 (6 cols), Table5
  (7 cols)`, increasing as specified, with change tracking enabled on each.
- `seed --tables 17 --rows 50` → all seventeen tables loaded.
- `workload --tables 5 --parallelism 2 --rate 20` (even-ish split) → per-table 48/47/47/47/47.
- `workload --tables 17 --parallelism 4 --rate 34` (uneven, 5/4/4/4) → per-table 39 or 40 across all
  seventeen; group and table figures summing to the combined total; 33.6/s against a target of 34.
- **Four concurrent sessions** confirmed on the source with `sys.dm_exec_sessions` during that run.
- Full cycle `up --tables 4` → `seed` → `workload --parallelism 2 --rate 24` → `verify --tables 4`:
  four mappings configured, and `verify` exits 0 with all four tables identical once the continuous
  scheduler caught up. (An immediate `verify` reported differences that were pure in-flight lag — rows
  present at the source and not yet at the target, and deletes not yet applied — and converged on the
  next passes, which is the expected behaviour rather than a defect.)
- `drift --tables 4` injects into all four and names each one's phantom key range.
- Backend suites unaffected and green (763 unit, 150 integration) — no `src/` file changed in this
  phase.

## Open questions

- ~~**The exact filler-type cycle and naming convention.**~~ Settled above.
- ~~**Default `--parallelism`.**~~ 1.
- **The harness still cannot drive an API that has auth switched on.** Disabling auth on its own API
  is the right answer for `up`; `--no-app` against a secured API remains unsupported. Worth a real
  answer (a service token, or the CLI's own credential path) if anyone needs the harness against a
  deployed instance — nobody does today.
