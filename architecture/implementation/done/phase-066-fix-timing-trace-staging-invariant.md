# Phase 66 — Fix the timing trace's staging-vs-reader invariant (root cause found, not flakiness)

**Status**: Planned, not started — root cause confirmed by reading the code, not just the CI log.
**Plan reference**: none — a bug fix in already-shipped code (phase 59/62's timing trace), not a new
design.

## The failure

`RunExecutorIntegrationTests.WithTheTraceOption_ARunRecordsEveryStage` failed on a real GitHub Actions
run (33428905264, 2026-08-31, after phase 65's Postgres fix landed and cleared the way to see it):

```
staging 148ms was shorter than the read it consumes (684ms)
```

The assertion (`RunExecutorIntegrationTests.cs:423`) already carries a 5ms tolerance
(`timing.StagingDurationMs >= timing.ReaderLifetimeMs - 5`) — this run missed by ~536ms, far past
rounding/scheduling noise. **This is a structural measurement bug, not CI flakiness.**

## Root cause

`RunExecutor.cs`'s two clocks for this pass don't start at the same reference point:

```csharp
// line 615 — before ReadChangesAsync is even called
var passTiming = trace ? new ReaderTimingRecorder() : null;

var read = await reader.ReadChangesAsync(...);          // line 617
var readRows = ... read.Rows.WithTiming(passTiming, ...);
var rows = transforms.IsEmpty ? readRows : transforms.ApplyAsync(readRows, ...);

...

// line 637 — only after ReadChangesAsync has already returned, and the
// transform-wrapped enumerable has been built
var stagingClock = trace ? Stopwatch.StartNew() : null;
var staged = await stagingProvider.StageAsync(...);
```

`passTiming` (which produces `ReaderLifetimeMs`) starts **before** `reader.ReadChangesAsync(...)` is
called — deliberately, per that line's own comment: "how long the source takes to *begin* answering is
part of what the reader cost." But several readers do real, synchronous-awaited work *inside*
`ReadChangesAsync` before ever returning the enumerable — `WatermarkReader.ReadChangesAsync`, for one,
awaits a real round trip (`GetMaxWatermarkAsync`) before returning `ReadResult`; `MsSqlChangeTrackingReader`
likely does something equivalent to establish its bounded window.

`stagingClock` starts **after** `ReadChangesAsync` has already returned — so it never sees that same
upfront cost. The two clocks measure overlapping-but-different spans: `ReaderLifetimeMs` includes
`ReadChangesAsync`'s own setup time; `StagingDurationMs` does not. On a fast, idle machine that setup
cost is small enough to stay under the test's 5ms tolerance; under CI's shared/contended hardware, it
grew to ~536ms and the assertion — which assumes the two spans nest — broke.

**The `ReaderTimeToFirstRowMs <= ReaderLifetimeMs` assertion is unaffected and still correct** — both
sides of that comparison come from the same clock (`passTiming`), so this is specifically about the
cross-metric comparison between two different `Stopwatch`s.

## The fix

Not a timing-tolerance bump — the gap is unbounded (grows with however long `ReadChangesAsync`'s own
setup takes), so no fixed slack value is honest. Two real options:

- **(A) Drop the `StagingDurationMs >= ReaderLifetimeMs` assertion entirely.** It was testing an
  incidental property of today's one staging provider, not a real invariant — phase 59's own design
  doc only ever guaranteed today's provider's duration would *track close to* the reader's lifetime, not
  that it's bounded below by it. The two numbers are independently meaningful; nothing requires one to
  dominate the other.
- **(B) Start `stagingClock` at the same reference point as `passTiming`** (before `ReadChangesAsync` is
  called), so both spans genuinely nest. Changes what `StagingDurationMs` measures — it would then
  include time that's really the reader's setup cost, muddying the "staging's own work" signal phase 59
  was built to isolate.

**Resolved 2026-08-31: (A).** Drop the assertion — it doesn't hold as a real invariant, and (B) would
misattribute the reader's own setup cost to staging, undermining the exact distinction phase 59 exists
to draw. `ReaderTimeToFirstRowMs <= ReaderLifetimeMs` stays (both sides share one clock, so it's genuinely
sound); the cross-metric `StagingDurationMs >= ReaderLifetimeMs` line goes, along with its explanatory
comment about a relationship that isn't actually guaranteed. The test keeps asserting each of the four
durations is present and non-negative — it just stops claiming a relationship between them that was never
actually true.

## What this phase does not build

- Any change to how the three durations are computed for production use — this is a test-assertion fix,
  the underlying measurements are doing what phase 59 designed them to do.
- A change to the reader-timing decorator's "start before the call" design — that decision is sound and
  unrelated to this bug.

## How to verify when built

- The fixed test passes reliably on a real CI run, not just locally.
- If (A): the removed assertion's absence doesn't hide a real regression — confirm by checking the
  numbers are still individually sane (non-null, non-negative) in the test's remaining assertions.
- Re-run CI a few times (or the equivalent local stress test) to confirm this isn't masking a second,
  genuinely flaky issue underneath.

## Open questions

None — (A) is settled. Ready to implement: remove the assertion and its comment at
`RunExecutorIntegrationTests.cs:420-424`, leaving the four presence/sanity checks above it in place.

---

# Outcome

Done, as planned: option (A). The cross-metric `StagingDurationMs >= ReaderLifetimeMs - 5` assertion and
its comment are gone from `RunExecutorIntegrationTests.WithTheTraceOption_ARunRecordsEveryStage`; a short
comment in their place records *why* nothing is asserted between those two clocks, so the next reader
doesn't re-add it. The four presence checks and the `ReaderTimeToFirstRowMs <= ReaderLifetimeMs` check
(both sides from one clock) stayed.

No production code changed — `RunExecutor.cs`'s two clocks still start where phase 59/62 put them, which
is the right place for each measurement on its own; the bug was only in the test claiming they nest.
