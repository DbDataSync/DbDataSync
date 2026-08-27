# Phase 19 — Connection Testing & Reachability (planned)

**Status**: Planned, not started
**Plan reference**: the mockups behind phase 15, which show reachability in four places; and
`architecture/planning/done/additional-database-drivers.md`, which is why this lands inside the driver
sequence rather than before it.

## Why here

Phase 15 omitted every reachability element in the design — the `Reachable` column, the "reachable"
pills on endpoints, the `Last test` card, "reachable · 12ms" — because nothing tested a connection and
a console must not show an invented reading. This phase supplies the reading.

It sits after the generic layer (17–18) and before Postgres (20) deliberately: `IDriver` grows a
capability here, so the first new driver should arrive to an interface that already has it rather than
have it added underneath.

## What this phase will build

**An opt-in driver capability.** Testing a connection is not universally meaningful — a driver
reaching an arbitrary engine (ODBC, JDBC) may have no sensible probe, and forcing one would mean
every such driver implementing a method it cannot honour. So it is an interface a driver may
implement, in the same shape as `ISegmentExpandingReader` (phase 9):

```csharp
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken);
}

public sealed record ConnectionTestResult(bool Succeeded, TimeSpan RoundTrip, string? ServerVersion, string? Error);
```

Callers ask `driver is IConnectionTester`, exactly as they ask about segmentation today, and
`DriverRegistry.Describe` reports `supportsConnectionTest` alongside the capability flags it already
carries — so the SPA learns whether to offer the button from the endpoint it already calls.

**`POST /api/connections/{name}/test`** — opens a connection through `DriverConnectionFactory`, runs
the driver's probe, returns the result. `404` for an unknown connection, and a result with
`Succeeded: false` (not a 500) when the connection fails: an unreachable database is an answer to the
question, not a server fault.

**MSSQL implements it**: `SELECT @@VERSION`, timed. Postgres will implement it in phase 20.

**SPA.**
- **Connection editor**: the `Test connection` action and the `Last test` card the design shows —
  result, round trip, server version, when. Both hidden entirely when the driver does not advertise
  the capability, rather than shown greyed out; an action that can never work is worse than no action.
- **Connections list**: the `Reachable` column, populated only on demand. **Not** a background poll —
  a list that silently opens every configured database on render is a surprising thing for a console
  to do, and nothing in the design implies it.
- **Credential store field**, greyed out, per review: show which secret this connection resolves
  through — `SecretRefs.ForConnection(name)` (`datasync:connection:<name>`) and the environment
  variable it falls back to (`CLRKERNEL_SECRET_DATASYNC_CONNECTION_<NAME>`). Read-only and disabled
  until there is more than one store to choose between, but it tells an operator staring at an auth
  failure exactly which value the process is looking for, which is most of that diagnosis.

## What this phase does not build

Health rollups, the "4 of 5 healthy" header figure, endpoint reachability pills on the replication
Overview, or anything that implies continuous monitoring — those belong with
`planning/todo/run-metrics-and-monitoring.md`. Storing test history: `Last test` shows the last test
*this session performed*, not a recorded series.

## How to verify when built

- `Category=Integration`: a good connection reports success with a plausible round trip and a server
  version; a connection pointed at a closed port reports failure with the driver's message and does
  **not** throw.
- A driver that does not implement `IConnectionTester` is reported as such by the capabilities
  endpoint, and the SPA hides the affordance — asserted, since the whole point of the opt-out is that
  it is visible to callers.
- Playwright: test a connection from the editor and see the result card populate.

## Open questions

- Whether a failed test should surface the raw provider message. It is the most useful thing for
  diagnosis and the most likely thing to leak a host name or credential fragment into a shared screen.
  Leaning towards showing it — this is an operator console, not a public page — but worth a deliberate
  decision.
