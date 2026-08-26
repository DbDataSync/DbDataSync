# Phase 13 — Benchmarks Tool

**Status**: Complete
**Plan reference**: Arose from `architecture/planning/todo/optimize-in-memory-data-column-oriented.md`.
That question could not be settled by reasoning — two successive conclusions drawn from a throwaway
benchmark were wrong, and the second was wrong in a way that argued *against* the right answer. The
tool exists so the numbers are re-runnable by anyone rather than quoted from a session that has ended.
Written as a retrospective; the work was small and its design was settled by the measurement it came
from.

## What was built

**`tools/DataSync.Benchmarks`** plus `scripts/benchmarks` / `scripts/benchmarks.cmd`, matching the
dev harness's launcher shape. One command compares three candidate in-memory shapes for a batch of
changes:

- `dictionary` — a pre-sized `Dictionary<string, object?>` per row, read back by name: what
  `ChangeRow.Values` carries today.
- `rowarray` — a positional `object?[]` per row with ordinals resolved once.
- `columnar` — hand-rolled batches over `ArrayPool`-rented, natively-typed arrays; values stored
  unboxed, boxed only if a consumer insists, one cell at a time.

Each is measured against **two sinks**: a real `SqlBulkCopy` into a real wide table, and a consumer
that reads typed values (`GetInt32`/`GetDecimal`/…) as a Parquet writer or a Postgres binary COPY
would. The gap between them is the boxing floor a sink's object-per-cell contract imposes, and it is
the number the whole question turns on.

Three decisions worth recording:

- **Each variant runs in its own child process.** Peak working set is a process-level statistic and
  does not reset; measuring several variants in one process would attribute the first one's peak to
  all of them.
- **Peak managed heap is sampled at 2 ms, not read at the end** — the end of a run is precisely when
  the heap is not at its peak.
- **The measured set is deliberately broader than allocated bytes**: collection counts per generation,
  total GC pause, peak heap and peak working set. Cumulative allocation cannot distinguish boxing every
  row up front from boxing one cell transiently at a sink boundary where it dies in gen0 immediately —
  and that distinction turned out to be the entire answer.

`--help` documents `--rows`, `--columns`, `--batch` and `--server`; the scratch database is created and
dropped per invocation, and each bulk-copy run verifies its own row count so a variant that silently
wrote nothing cannot look like the fastest.

## How this was verified

Run end to end against the docker-compose source instance at 200,000 rows × 50 columns (10M cells),
reproducing the ad-hoc measurements it replaced to within noise:

| variant | ms | alloc MB | peak heap | peak WS | gen0 | pause |
| --- | --- | --- | --- | --- | --- | --- |
| dictionary / bulk-copy | 3,487 | 1,293.1 | 17.0 | 93.4 | 81 | 17 ms |
| rowarray / bulk-copy | 2,978 | 335.0 | 16.7 | 91.7 | 21 | 6 ms |
| columnar / bulk-copy | 2,987 | 335.0 | 23.6 | 96.4 | 21 | 6 ms |
| dictionary / typed | 1,012 | 1,117.1 | 16.4 | 90.9 | 70 | 8 ms |
| rowarray / typed | 238 | 152.6 | 16.0 | 85.9 | 9 | 1 ms |
| columnar / typed | 115 | **0.0** | **7.2** | **73.3** | **0** | **0 ms** |

`dotnet build` clean, no warnings; the existing suites are untouched by this phase.

## What's explicitly not built

Any change to `ChangeRow` or the readers — the representation decision is still open in the planning
doc, and this tool exists to inform it, not to pre-empt it. No source-side measurement: the benchmark
generates values rather than reading them from a `DbDataReader`, which is the right isolation for
comparing representations but means it does not yet show what a columnar reader would need from the
source side (typed getters, so the boxing floor does not simply move upstream). No disk offload,
multi-target fan-out, concurrent mappings, or Server GC — all named in the planning doc as open.

## Notes / things to revisit later

- Adding a variant means an `enum` member and a `DbDataReader` subclass; the orchestrator picks it up
  from `Enum.GetValues`. That is the extension point if a fourth shape is worth measuring.
- The benchmark is not part of any test suite and nothing runs it automatically. That is deliberate —
  it needs a live SQL Server and takes tens of seconds — but it does mean it can rot silently. It
  builds as part of the solution, so it will not rot far.
