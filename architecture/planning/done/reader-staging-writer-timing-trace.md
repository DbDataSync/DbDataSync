# Opt-in timing trace: which reader/staging/writer ran, and how long each took

**Status: draft, 2026-08-30 — one real design fork found while grounding this, worth resolving before
writing a phase doc.**

## The ask

An option to enable timers tracing which reader, staging provider, and writer a table mapping used for a
pass, and how long each took.

## What exists today

- **Which Kind is configured is already visible statically** — the Overview page's Pipeline card, and
  phase 37's SQL preview — but neither is a *record of what actually ran for a given pass*, and config
  can change over time. A log line captures what was true for that run, self-contained.
- **No timing exists anywhere today.** `TaskRunRecord` stores overall run duration (start/end) but nothing
  per-stage. Log lines exist (`Log(runId, severity, message)`, shown in the Live Run panel and stored run
  log) with a full severity range including `Trace`/`Debug` — currently unused for anything like this, and
  the natural level for an opt-in diagnostic trace to log at.
- **Instrumentation points are clean for two of the three stages.** `RunExecutor`'s pass loop calls
  `reader.ReadChangesAsync`, `stagingProvider.StageAsync`, then `writer.ApplyAsync` in sequence — three
  distinct call sites, in order.

## The catch this raised, and how it's resolved — 2026-08-30

**The original problem**: reading is streamed. `reader.ReadChangesAsync` returns a lazy
`IAsyncEnumerable<ChangeRow>` almost immediately; the real source querying happens *while staging
consumes that stream*. A naive stopwatch wrapped around each of the three `await` calls would show
reading taking ~0ms and staging absorbing nearly all of it — technically true of the call, misleading as
an answer to "how long did reading take."

**Resolved, not by choosing between "combined duration" and "instrument every staging provider," but by
measuring two well-defined lifecycle events on the reader's own stream, generically.** `ReadResult.Rows`
is `IAsyncEnumerable<ChangeRow>` for every reader — a decorator applied once, in `RunExecutor`, right
after `reader.ReadChangesAsync` returns, needs no change to any `IChangeReader` or `IStagingProvider`
implementation:

- **Time to first row**: a timestamp taken immediately before `await reader.ReadChangesAsync(...)` is
  called, and a second one the moment the wrapped enumerator's **first** `MoveNextAsync()` call returns
  `true`. This works because every reader here does its real setup (build the command, call
  `ExecuteReaderAsync`) synchronously at the top of an `async IAsyncEnumerable` iterator method, before
  its first `yield return` — confirmed against `WatermarkReader.ReadRowsAsync`, and it's the shape every
  other reader shares. The first `MoveNextAsync()` call is where that setup actually executes, so timing
  it *is* timing "query issued → first row," with no reader-specific code needed.
- **Reader lifetime, via dispose**: the same wrapped enumerator records when its `DisposeAsync()` runs
  (staging's `await foreach` disposes it once exhausted, or the pipeline is torn down early on failure/
  cancellation). Lifetime is start-timestamp to dispose-timestamp — a direct answer to "how long was this
  reader/cursor actually held," which is the number that matters for diagnosing a source connection or
  lock held open by a slow consumer downstream, regardless of whether the slowness is the source's query
  or staging's write pace. This is a deliberate choice, not an approximation — it's answering a different,
  and arguably more operationally useful, question than "how long did the query itself take."

**The writer stays a plain wrapped call** — staging fully materializes a `StagedChangeSet` before
`writer.ApplyAsync` runs, so a stopwatch around that one call is already accurate, no decorator needed.

## Storage: a real queryable metric, not log lines — revised 2026-08-30

**Not log lines.** New, nullable columns on `TaskRunRecord`/`TaskRuns` (a state-store migration, the same
`Migrations.Scripts` mechanism phase 52's `Users` table used), populated only when the option is on:

- `ReaderKind: string?`, `ReaderTimeToFirstRowMs: long?`, `ReaderLifetimeMs: long?`
- `StagingKind: string?`, `StagingDurationMs: long?` — **resolved 2026-08-30: staging gets a full,
  precise duration too**, not just its Kind. A plain wrapped-call stopwatch around the whole `StageAsync`
  call is correct for this, not an approximation bounded by reader lifetime: today's only provider
  (`BatchInsertStagingProvider`) writes straight into a target-side table, so its duration tracks close
  to the reader's lifetime — but `IStagingProvider` already names file-based staging (stage to a file,
  move it, load from it) as a real future shape, and that kind of provider does real work *after* the
  reader is exhausted and disposed. A wrapped-call stopwatch captures that correctly without needing to
  be revisited once such a provider exists; a decorator tied to reader lifetime would not.
- `WriterKind: string?`, `WriterDurationMs: long?`

Structured storage means this is queryable the same way phase 36's run-metrics aggregate already queries
`TaskRuns` for duration percentiles — an operator could ask "what's this mapping's p95 time-to-first-row
over the last 24h," not just read one run's number. Building that aggregate view is not assumed as part of
this feature; writing the queryable data is the scope here, an aggregate/dashboard on top is a natural,
separate follow-on (`run-metrics.md`'s own precedent).

**Genuinely opt-in, not just null-when-off.** When the mapping's option is off, `RunExecutor` skips the
decorator/stopwatches entirely — no wrapping overhead paid, not just unused columns.

## Open questions

1. Mapping-level only, or the same replication-default/mapping-override shape other settings have gotten?
   Leaning mapping-level only, consistent with recent precedent, unless told otherwise.
2. Retention for these new columns — same "policy question, not an implementation one" precedent
   `run-metrics.md` and phase 43 both already established for their own data.

**Next step**: small enough to write directly as an implementation phase doc — the enumerable decorator,
the writer stopwatch, the new `TaskRuns` columns, and the opt-in mapping setting.

---

# Outcome

Agreed, as `implementation/todo/phase-059-reader-timing-trace.md`.
