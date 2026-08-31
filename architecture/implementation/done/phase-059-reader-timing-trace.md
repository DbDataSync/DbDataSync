# Phase 59 — Opt-in reader/writer timing trace, queryable per run

**Status**: Complete
**Plan reference**: `architecture/planning/done/reader-staging-writer-timing-trace.md`

## What this covers

A per-mapping opt-in that records, per pass, which reader/staging/writer Kind ran and precise timing:
time to first row and total reader lifetime (via a generic stream decorator, not per-reader
instrumentation), plus staging and writer duration — stored as new queryable `TaskRuns` columns.

## 1. The reader-timing decorator

`ReaderTiming.WithTiming(this IAsyncEnumerable<ChangeRow>, ReaderTimingRecorder, CancellationToken)` in
`DataSync.TaskRunner`. Records time-to-first-row on the first successful `MoveNextAsync()`, measured
from a stopwatch started before `ReadChangesAsync` is called, and lifetime in the enumerator's
`finally`. No change to `IChangeReader`, any reader, or `IStagingProvider`.

## 2. Staging and writer timing

Plain `Stopwatch`s around the whole `StageAsync` and `ApplyAsync` calls, summed across a pass's
segments.

## 3. New `TaskRuns` columns

Seven, all nullable, added through `Migrations.Scripts`: `ReaderKind`, `ReaderTimeToFirstRowMs`,
`ReaderLifetimeMs`, `StagingKind`, `StagingDurationMs`, `WriterKind`, `WriterDurationMs`.
`TaskRunRecord` gained a `RunTiming? Timing`; `TaskRunStore` writes them when a trace is supplied and
leaves them null otherwise.

## 4. The opt-in setting

`TableMappingConfig.TraceTiming`, mapping-level only. Off means the stream is never wrapped and no
stopwatch is started.

## What this phase does not build

- Any aggregate or dashboard view over the new columns.
- A file-based staging provider.
- A replication-level default/override layer for the option.
- Retention/purging of the new columns' data.
- **Any UI.** The columns are queryable and reach the API through `TaskRunRecord`; nothing renders them
  yet. See the open questions.

---

# Retrospective

The smallest of the five, and the design was already settled — which left the interesting decisions in
two places the phase doc did not reach: what a multi-segment pass reports, and how far the change
propagates through the remote state protocol.

## The decorator is a `finally`, and that is the whole robustness story

`WithTiming` enumerates by hand rather than with `await foreach`, because the point is the `finally`.
A pass that throws mid-read, or a consumer that `break`s out early, still disposes the enumerator —
and that disposal is the only signal the decorator gets that the read is over. Without it, exactly the
runs an operator most wants timing for (the ones that failed) would report none.

Both cases have tests. The early-`break` one matters more than it looks: nothing in the pipeline
currently stops early, but a future one might, and a decorator that silently reported nothing for it
would be worse than no decorator.

## A multi-segment pass needs two different accumulations

The phase doc describes one pass, one read. A reload with a segment list does one read *per segment*,
all recorded on one `TaskRuns` row, and the two reader numbers do not combine the same way:

- **Lifetimes add.** The run really did spend the sum of them reading.
- **Time-to-first-row does not.** It is the *first* segment's, because the question it answers is "how
  long did the source take to start answering" — and a later segment's first row is measured from its
  own read, not from the pass's start. Summing them would produce a number larger than any real wait.

`timeToFirstRowMs ??= passTiming.TimeToFirstRowMs` is the whole of it, and it is a `??=` on purpose.

## Null versus zero is the contract

`CompleteRun` writes every timing column as NULL when no trace was supplied, rather than writing
zeros. "Not measured" and "measured as nothing" are different answers, and the entire point of putting
these in columns rather than log lines is that somebody will aggregate them later — an average that
silently included every untraced run's zero would be worse than no average.

`ReadTiming` mirrors this: it returns `null` when all seven columns are, rather than a record full of
nulls, so "was this run traced" is answerable without interrogating seven fields.

## The remote state protocol had to change, and had a precedent for it

`CompleteRun` is not just a store method — it crosses `IRunnerState`, `LocalRunnerState`,
`RemoteRunnerState`, `CompleteRunRequest`, the `/complete-run` endpoint, and journal recovery. Six
places, for one optional argument.

`CompleteRunRequest` already carried `string? FailureKind = null` with a comment explaining that it is
defaulted so a journal written by an older runner still deserializes. `RunTiming? Timing = null`
follows exactly that, and the comment says so — a journal entry written before this phase replays
unchanged, which is the property that makes the protocol extensible at all.

## Decisions the phase doc left open or did not reach

- **Seven columns, not five.** The doc's prose says "five new columns" while its own SQL block lists
  seven. The SQL is the specification and was followed; the two staging columns are what the doc spends
  a whole section justifying, so they were plainly meant to be there.
- **The Kinds are stored beside the numbers**, per the doc, and it is load-bearing rather than
  decorative: a unit of work may override the replication's configured pipeline — a backfill reloads
  through a different reader — so "which reader produced this number" cannot be recovered from config
  afterwards.
- **A verification run records no timing.** It reads both sides and writes nothing, so the three stages
  this traces do not exist for it. Null rather than zeros, for the same reason as above.
- **`TraceTiming` rather than `traceReaderTiming`.** The doc's suggested name describes a third of what
  the option does; staging and writer duration are not reader timing.
- **The option is not in the SPA.** Deliberate scope: the phase's stated job is writing queryable data,
  and it names an aggregate view as separate follow-on work. Adding one checkbox with nothing rendering
  its output would be the least useful half of that follow-on. Noted as a gap rather than pretended
  otherwise.

## Verification

- `ReaderTimingTests` (7) — rows passed through unchanged; time-to-first-row always a prefix of the
  lifetime; **a slow first row and a slow stream moving only the number each should**, which is the
  claim that makes two numbers worth having; an empty read recording a lifetime and no first row; a
  read that throws mid-stream still recording one; a consumer that stops early still recording one;
  and staging that works after the read is done measuring longer than the read — the future
  file-based provider's case, modelled ahead of any such provider existing.
- `RunTimingStoreTests` (5) — an untraced run carrying no timing at all, a full round trip of all seven
  columns, the Kinds recorded with the numbers, zero distinguishable from not-measured, and timing
  surviving into run history.
- `RunExecutorIntegrationTests` (+3, integration) — the option off producing a run with null timing;
  the option on producing every stage's Kind and duration against a real SQL Server, with
  time-to-first-row ≤ lifetime and staging spanning the read it consumes; and a pass that found no
  changes still recording a lifetime with no first row.
- Full suite green: 793 unit, 153 integration. SPA typechecks (its types are unchanged — the API
  serialises the new field automatically).

## Open questions

- ~~**Exact retention policy for this data.**~~ Still not decided here, and now genuinely adjacent:
  phase 60 prunes `TaskRuns` rows wholesale, which prunes these columns with them. Whether timing
  deserves a *different* retention from the run it belongs to is a real question and the answer is
  probably no.
- **Nothing renders it.** The columns reach the API on every `TaskRunRecord` and no screen shows them.
  The aggregate view the phase doc names as follow-on work is where they become useful; a per-run
  display in the run detail panel is a smaller, obvious first step.
- **The option can only be set through the config file or the mapping API**, not in the mapping editor.
