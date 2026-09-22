# Phase 171V — `JdbcConnection.ServerVersion`/`DataSource`: from "deferred" to "used and crashing"

**Status**: Built. See Retrospective.
**Plan reference**: none — found while surveying JDBC feature gaps for the user.

## Why — this isn't a gap, it's a live bug

`JdbcConnection.ServerVersion` and `.DataSource` both throw `NotImplementedException` today, with a
doc comment saying they're "deferred to a connection-testing phase (see phase 19)":

```csharp
// src/DbDataSync.Drivers.Jdbc/Imported/JdbcConnection.cs:71
public override string ServerVersion => throw new NotImplementedException(
    "JdbcConnection.ServerVersion: deferred to a connection-testing phase (see phase 19); " +
    "DatabaseMetaData.getDatabaseProductVersion() is the answer when that phase needs it.");
```

That comment predates phase 168V. `GenericDriverBase<TSpec>.TestAsync` — which `JdbcGenericDriver`
inherits unconditionally, with no override — **already calls `connection.ServerVersion`** on every
successful test:

```csharp
// src/DbDataSync.Drivers.Generic/GenericDriverBase.cs:133-146
public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
{
    var started = Stopwatch.GetTimestamp();
    try
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT 1;";
        await cmd.ExecuteScalarAsync(cancellationToken);
        return new ConnectionTestResult(true, Stopwatch.GetElapsedTime(started), connection.ServerVersion, null);
    }
    catch (DbException ex)
    {
        return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, ex.Message);
    }
}
```

`NotImplementedException` is not a `DbException`, so it isn't caught here — it propagates out of
`TestAsync` uncaught. `ConnectionsController.cs:177` is the caller `dbdatasync`'s own "Test Connection"
button reaches. **Today, testing any JDBC-backed connection that successfully runs `SELECT 1` crashes
instead of reporting success**, because the crash happens *after* the query that proves the connection
works, while building the success result. This was true the moment phase 168V put `JdbcGenericDriver` on
the shared `GenericDriverBase.TestAsync` — the "deferred" comment describing the old, no-longer-true state
that `JdbcDriver` (phase 165V) had no `IConnectionTester` implementation to call this from at all.

## Fix

Both members are cheap, and the doc comment already names the right API:

```csharp
public override string ServerVersion =>
    JavaSqlConnection.getMetaData().getDatabaseProductVersion();

public override string DataSource =>
    JavaSqlConnection.getMetaData().getURL();
```

`java.sql.DatabaseMetaData.getDatabaseProductVersion()`/`getURL()` are both defined to never fail once a
connection is open (unlike `setCatalog`, they're informational reads a driver has no reason to refuse),
so this doesn't need `ChangeDatabase`'s try/catch-and-translate treatment — but confirm that against pgJDBC
specifically (its own source, or empirically) rather than trusting the spec's silence on failure modes,
matching this project's own "prove it, don't assume it" pattern for every other JDBC edge case so far.

## Files touched

- `src/DbDataSync.Drivers.Jdbc/Imported/JdbcConnection.cs:67-73` — the two members above.

## How to verify when built

- A new test asserting `JdbcConnection.ServerVersion`/`.DataSource` return real, non-throwing values
  against the live Postgres container (`ServerVersion` containing a version-looking string, `DataSource`
  containing the JDBC URL or something derived from it).
- A new (or extended) `GenericDriverBase.TestAsync`-level test — construct a real `JdbcGenericDriver`,
  call `TestAsync` against a live, open connection, assert `ConnectionTestResult.Succeeded == true` and
  no exception escapes. This is the regression the other two tests can't catch by themselves: they'd
  pass even if some *other* caller of `TestAsync` still crashed for an unrelated reason, where this one
  pins the actual failure mode found here.
- Existing `JdbcCatalogTests`/`JdbcReaderParityTests`/`JdbcChangeDatabaseTests`/`JdbcDescriptorTests`
  unaffected — none of them go through `TestAsync`.

---

# Retrospective

Built exactly as designed above — `ServerVersion`/`DataSource` now call `getMetaData().getDatabaseProductVersion()`/
`.getURL()`, no try/catch needed (confirmed empirically: neither call failed against pgJDBC in any test
run here, consistent with the "informational, never refused" reasoning above).

New `tests/DbDataSync.Drivers.Jdbc.Tests/JdbcConnectionTests.cs` — five tests against the live Postgres
container: `ServerVersion`/`DataSource` return real values, and — the one that actually pins the
regression this phase exists to close — `TestAsync` on a real `JdbcGenericDriver` succeeds instead of
throwing. Also picked up the `ConnectionString`-password-persistence fix
(`follow-up-jdbcconnection-persists-the-password-in-connectionstring.md`) in the same pass, since both
touch `JdbcConnection.Open()`/the connection's own state — two of the five new tests cover that.

Full `DbDataSync.Drivers.Jdbc.Tests` suite green: 12/12, no regressions in the pre-existing tests.
