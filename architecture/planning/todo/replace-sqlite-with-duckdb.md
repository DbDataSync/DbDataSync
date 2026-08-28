# Replace SQLite with DuckDB?

## The question

Investigate replacing SQLite (`DataSync.State`, the central state store) with DuckDB — using
`DuckDB.NET.Data.Full` and the quack protocol.

Not investigated yet. No hypothesis, no benchmarking, no read of `DataSync.State`'s current
concurrency approach against what DuckDB would actually offer instead — just the raw idea, captured
so it isn't lost.
