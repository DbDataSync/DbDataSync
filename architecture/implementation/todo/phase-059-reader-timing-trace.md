# Phase 59 — Opt-in reader/writer timing trace, queryable per run (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/reader-staging-writer-timing-trace.md`

## What this covers

A per-mapping opt-in option that records, per pass, which reader/staging/writer Kind ran and precise
timing: time to first row and total reader lifetime (via a generic stream decorator, not per-reader
instrumentation), plus staging and writer duration — stored as new queryable `TaskRuns` columns, not log
lines.

## 1. The reader-timing decorator

New, in `DataSync.TaskRunner` (or a small shared spot both it and the abstractions layer can see):

```csharp
public static IAsyncEnumerable<ChangeRow> WithTiming(
    this IAsyncEnumerable<ChangeRow> rows, ReaderTimingRecorder recorder)
```

Wraps the enumerable returned in `ReadResult.Rows`. On the first successful `MoveNextAsync()`, records
time-to-first-row (measured from a timestamp taken immediately before `await reader.ReadChangesAsync(...)`
was called). On `DisposeAsync()` of the wrapped enumerator, records reader lifetime from that same start
timestamp. No change to `IChangeReader`, any reader implementation, or `IStagingProvider` — this wraps the
stream at one point in `RunExecutor`'s pass loop, applied only when the mapping's option is on.

## 2. Staging and writer timing — both plain wrapped calls, and why that's correct for staging too

**Revised 2026-08-30.** A plain `Stopwatch` around `stagingProvider.StageAsync(...)`, and another around
`writer.ApplyAsync(...)` — both the whole call, start to return. No decorator needed for either.

This looked redundant with reader lifetime at first (today's only staging provider,
`BatchInsertStagingProvider`, writes straight into a target-side staging table, so its `StageAsync` call
finishes right as the reader is exhausted — the two numbers would track closely). **But that's an
accident of today's one implementation, not a property of the interface.** `IStagingProvider` already
names file-based staging (parquet, "deferred past v1") as a real future shape, and a stage-to-file /
move-file / load-from-file provider does real work **after** the reader stream is fully consumed and
disposed — moving or uploading the file has nothing left to do with the reader at all. For a provider
like that, `StageAsync`'s wall-clock duration is *genuinely longer* than the reader's lifetime, and the
difference (`StagingDurationMs - ReaderLifetimeMs`) is itself a meaningful number: staging's own work
beyond consuming the source. A plain wrapped-call stopwatch captures this correctly today (where the two
numbers are expected to be nearly equal) and remains correct unchanged once a staging provider that does
real post-read work exists — nothing about this needs revisiting when that provider is built.

## 3. New `TaskRuns` columns (state-store migration)

Nullable, populated only when the mapping's trace option is on — following the same `Migrations.Scripts`
pattern phase 52's `Users`/`UserCredentials` tables used:

```sql
ALTER TABLE TaskRuns ADD COLUMN ReaderKind TEXT NULL;
ALTER TABLE TaskRuns ADD COLUMN ReaderTimeToFirstRowMs INTEGER NULL;
ALTER TABLE TaskRuns ADD COLUMN ReaderLifetimeMs INTEGER NULL;
ALTER TABLE TaskRuns ADD COLUMN StagingKind TEXT NULL;
ALTER TABLE TaskRuns ADD COLUMN StagingDurationMs INTEGER NULL;
ALTER TABLE TaskRuns ADD COLUMN WriterKind TEXT NULL;
ALTER TABLE TaskRuns ADD COLUMN WriterDurationMs INTEGER NULL;
```

`TaskRunRecord` (`DataSync.State/Models.cs`) gains the matching fields. `TaskRunStore` gains the write
path when the option is on and leaves them null otherwise.

## 4. The opt-in setting

- `TableMappingConfig` gains a boolean (e.g. `traceReaderTiming`), mapping-level only — no
  replication-default/override layer, consistent with this session's recent precedent for similar
  settings unless a real need for that shape shows up.
- **Off means no wrapping at all**, not just null columns — `RunExecutor` checks the flag once per pass
  and only applies the decorator/stopwatch when it's on, so an unopted-in mapping pays nothing.

## What this phase does not build

- Any aggregate/dashboard view over the new columns (a `run-metrics.md`-style percentile view) — writing
  queryable data is this phase's job; an aggregate on top is a natural, separate follow-on.
- A future file-based staging provider itself — this phase only makes sure whichever provider exists
  (today's or a later one) gets its duration measured correctly, without assuming which shape it takes.
- A replication-level default/override layer for the trace option.
- Retention/purging of the new columns' data — left as a policy question, per `run-metrics.md`'s and
  phase 43's own precedent.

## How to verify when built

- A mapping with the option off produces `TaskRuns` rows with all five new columns null, and no
  measurable overhead from the decorator (it's never applied).
- A mapping with the option on produces non-null `ReaderKind`/`ReaderTimeToFirstRowMs`/`ReaderLifetimeMs`,
  `StagingKind`/`StagingDurationMs`, and `WriterKind`/`WriterDurationMs` for each run.
- `ReaderTimeToFirstRowMs <= ReaderLifetimeMs` always (time-to-first-row is a prefix of the full
  lifetime) — a basic sanity invariant worth asserting directly.
- A deliberately slow staging provider (test double) lengthens `ReaderLifetimeMs` without lengthening
  `ReaderTimeToFirstRowMs` — confirms the two metrics actually measure different things.
- Against today's `BatchInsertStagingProvider`, `StagingDurationMs` tracks close to `ReaderLifetimeMs`
  (staging has no work left once the reader is exhausted).
- A test double staging provider that does deliberate work *after* consuming all rows (simulating a
  future file-move step) produces `StagingDurationMs` measurably greater than `ReaderLifetimeMs` —
  confirms the wrapped-call approach captures post-read staging work correctly, without needing a
  decorator, ahead of any real file-based provider existing.
- A run that fails mid-read still disposes the wrapped enumerator and records whatever lifetime elapsed
  before cancellation, rather than leaving the columns null on a run that did produce partial timing.
- Full suite green.

## Open questions

- Exact retention policy for this data — not decided here, per precedent.
