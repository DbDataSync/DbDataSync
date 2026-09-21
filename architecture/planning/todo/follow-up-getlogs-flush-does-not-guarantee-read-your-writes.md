# `GetLogs`' own `Flush()` does not actually guarantee read-your-writes

**Status: open.** Found 2026-09-18 as a one-off CI failure, diagnosed 2026-09-21 while reviewing which
follow-ups were still unwritten. Filed per `architecture/implementation/README.md`'s "Follow-up work
gets its own doc, not a paragraph." Not in
[the flake catalogue](follow-up-ci-is-red-on-most-pushes-from-unrelated-flaky-tests.md) — that starts at
the 2026-09-20 runs and this predates it.

## The symptom

`dotnet-windows`, run `35390840513`, on a commit that changed only `architecture/branching-and-releases.md`
and `.github/workflows/release.yml` — nothing compiled, nothing that test touches:

```
Failed DbDataSync.Api.Tests.RunnerStateEndpointTests.The_remote_implementation_does_the_same_thing_as_the_local_one
  Assert.Contains() Failure: Filter not matched in collection
  Collection: []
  at RunnerStateEndpointTests.cs:line 150
```

Line 150 is `Assert.Contains(logs.GetLogs(claimed.RunId), l => l.Message == "hello from a runner")`. It
passed on re-run with no change, and the other four jobs were green. Easy to file as "another Windows
flake" — but the empty collection has a specific cause, and it is not in the test.

## Why it is not a test-timing artifact

Trace one log line from a remote runner to that assertion:

1. `RemoteRunnerState.Log` buffers into `_pendingLogs` and returns; it only sends at 200 lines.
2. `remote.Flush()` sends the batch to the owner over HTTP, synchronously. **This part is fine** — when
   it returns, the owner has the line.
3. The owner calls `LogWriter.Log`, which enqueues into `_buffer` and flushes only at
   `FlushThreshold = 50`. One line never reaches that, so the line sits in memory, not the database.
4. `LogWriter.GetLogs` therefore calls `Flush()` itself before its `SELECT` — precisely so a caller sees
   what was just written.

So the read-your-writes guarantee is deliberate and is supposed to come from step 4. The defect is that
`Flush()` is unsynchronized:

```csharp
public void Flush()
{
    var batch = new List<...>();
    while (_buffer.TryDequeue(out var entry))   // drains the queue
    {
        batch.Add(entry);
        Interlocked.Decrement(ref _pendingCount);
    }
    if (batch.Count == 0)
        return;                                  // <-- "nothing pending", so nothing to wait for
    _database.Retry(() => { /* open connection, BEGIN, INSERT, COMMIT */ });
}
```

`LogWriter` also runs a background flush loop on a `PeriodicTimer` every `FlushInterval = 2` seconds.
When that timer's `Flush()` has already drained the queue and is **inside the still-uncommitted
transaction**, a concurrent `GetLogs` → `Flush()` finds `_buffer` empty, takes the `batch.Count == 0`
early return, and runs its `SELECT` against a database where the insert has not committed yet. Empty
collection.

The queue is concurrent, so nothing is corrupted and no line is lost — it lands a moment later. What is
lost is only the *ordering guarantee* `GetLogs` calls `Flush()` to obtain. The rarity matches: the window
is the width of one small transaction, and you have to land in it.

## Why it is worth fixing rather than re-running

- **The test is asserting a real contract, correctly.** `GetLogs` flushes for a reason; a fix that
  relaxed the assertion would be deleting the only thing that noticed.
- **It is not confined to tests.** Anything polling `GetLogs` — the run log view — can momentarily fail
  to show a line the writer has already accepted. Self-correcting on the next poll, so low severity, but
  it is the same defect, and "the UI fixes itself a second later" is a description of the bug, not a
  reason it is not one.
- **It will re-present as an unrelated flake.** It already did, on a docs-only commit, in a test whose
  name mentions neither logs nor flushing.

## Fix shape

Serialize `Flush` so a caller that finds the queue empty still waits for an in-flight flush rather than
racing its commit — a lock held across the drain *and* the write, so the early return cannot overtake a
transaction that is already carrying this caller's entries.

The one thing to weigh: `Log()` itself calls `Flush()` at the threshold, on the hot path, so the lock is
held during a database write on whichever thread crosses 50 lines. That is already true of the work, not
new contention, but it makes the high-rate path' behaviour worth a moment's thought rather than
reflexively wrapping the method. An alternative that avoids holding a lock across I/O is for `Flush` to
record a completed-flush sequence number and have `GetLogs` wait for the in-flight one to publish.

Either way this is a small, local change in `src/DbDataSync.State/LogWriter.cs`; the tracing above is the
expensive part and it is done.

## How to verify when closed

- With the background timer's interval temporarily lowered (or a flush forced concurrently), a
  `Log` → `GetLogs` pair returns the line every time, rather than almost every time.
- `RunnerStateEndpointTests.The_remote_implementation_does_the_same_thing_as_the_local_one` stops being
  a candidate for this failure — and is not modified to achieve that.
- `GetLogs`' `Flush()` call still exists and still means what its presence implies.
