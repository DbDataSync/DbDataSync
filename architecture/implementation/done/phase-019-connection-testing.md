# Phase 19 — Connection Testing & Reachability

**Status**: Built
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

---

# Retrospective

Built as planned. Every reachability element phase 15 omitted now has a real reading behind it, and
the open question is answered.

## The open question: yes, show the provider's message

**The raw provider message is shown**, unedited. This is an operator console reached through the same
network as the databases it configures, not a public page, and the provider's own wording is most of
the diagnosis — "Login failed for user 'sa'" and "A network-related or instance-specific error
occurred" send an operator to completely different places, and a sanitised "connection failed" sends
them nowhere. The information it might leak — a host name, a database name — is already on the same
screen, in the fields that produced it.

## Failing to *open* never reaches the driver's probe

`IConnectionTester.TestAsync` takes an already-open connection, so the most common failure — a wrong
host, a closed port, a rejected login — happens in `DriverConnectionFactory.OpenAsync` and never gets
near a driver. That is the path most likely to escape as a 500, so the controller catches it and
reports it in the same shape as a probe failure. `Test_AgainstAClosedPort_ReportsFailureRatherThanThrowing`
covers it specifically, and asserts a 200 rather than only checking `succeeded: false`.

The report separates `connectMs` from `probeMs` for the same reason: connecting is usually the
dominant cost and the part that fails, and folding them into one "round trip" would hide which half
was slow.

## Reachability is on demand, everywhere

Nothing polls. The connections list shows `—` until someone presses **Test all**, and the Playwright
test asserts that: navigating to the list must not open a database. A console that opens every
configured production database because a page rendered is a surprising thing to do, and the design
never implied it.

`Test all` fires the tests concurrently and lands each result as it arrives rather than after the
slowest, because an unreachable host takes its full connect timeout to answer and holding every other
row behind it reads as a hang.

"Untested" is a third state, distinct from "unreachable" — the reason this column was left out of
phase 15 rather than filled with a placeholder.

## The credential field tells the operator the exact string to set

`SecretRefs.EnvironmentVariableFor` now defines the fallback variable name — uppercased, non-alphanumerics
replaced, under `CLRKERNEL_SECRET_`. It was previously restated in five test files and nowhere in
`src`, which is the wrong way round for a value an operator has to type exactly; those five now call
it, and `GET /api/connections/{name}/credential-source` serves it to the SPA rather than the SPA
reimplementing the transform in TypeScript, where it would drift silently.

The store picker is disabled rather than hidden: there is one store, so there is nothing to choose,
but seeing which one is in play is the point.

## Opt-out is asserted, not assumed

`DriverCapabilityOptInTests` registers a driver that does *not* implement `IConnectionTester` and
asserts `SupportsConnectionTest` comes back false — the whole value of making the capability optional
is that a caller can see the answer, so a test that only ever exercised the implementing driver would
prove nothing. The SPA hides both the action and the card on that flag.

## Verification

- `ConnectionTestIntegrationTests` (`Category=Integration`) — a reachable server reports success with
  a one-line version and a real timing; a closed port reports failure as a 200 with the provider's
  message; an unknown connection is 404; and the credential source names the exact environment
  variable.
- `DriverCapabilityOptInTests` — the opt-out, the opt-in, and the `DetectsDeletes` default.
- `ConnectionsControllerTests` asserts the capabilities endpoint carries the flag.
- Playwright test 13 — the card reads "Not tested yet" before anything is tested, the credential
  variable is spelled out, testing populates the card with `reachable` and a server version, the list
  column is `—` on load, and **Test all** fills it.
- Full .NET suite green: 260 tests. Playwright: 13 green. `tsc -b` clean; `oxlint` unchanged at three
  pre-existing warnings.

## Still not built

Everything the plan excluded: health rollups, the "4 of 5 healthy" header figure, endpoint pills on the
replication Overview, and any stored test history. `Last test` means the last test this browser session
performed, and the card says so.
