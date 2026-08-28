# Replace SQLite with DuckDB?

## The question

Investigate replacing SQLite (`DataSync.State`, the central state store) with DuckDB — using
`DuckDB.NET.Data.Full` and the quack protocol.

Not investigated yet. No hypothesis, no benchmarking, no read of `DataSync.State`'s current
concurrency approach against what DuckDB would actually offer instead — just the raw idea, captured
so it isn't lost.

## Why no implementation phase yet (2026-08-27)

Nothing here is wrong; there is simply nothing to design against. A phase doc is "a design that is
ready to build", and this is a question with no hypothesis attached — no read of what
`DataSync.State`'s concurrency actually needs, no measurement of what it costs today, and no statement
of what DuckDB would be *for* here.

The state store is small, write-mostly, and accessed concurrently by the API and one TaskRunner process
per run — which is close to the shape SQLite is best at and close to the shape DuckDB (a columnar
analytical engine) is worst at. That is a hypothesis, not a finding, and it is the one worth testing
first: if the motivation is analytical queries over `TaskRuns`, that is
`planning/todo/run-metrics-and-monitoring.md`'s problem and may not need a different engine at all.

**Next step**: decide what this is trying to make better. Then measure that thing.
