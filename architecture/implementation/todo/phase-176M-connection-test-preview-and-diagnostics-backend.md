# Phase 176M — Connection-test preview and broadened diagnostics (backend)

**Status**: Not started — design only.
**Plan reference**: `architecture/planning/todo/jdbc-url-template-and-connection-testing.md` (full
rationale and code sketches). **Depends on phase 175M** — `JdbcGenericDriver`'s side of
`PreviewConnection` reuses the unification logic 175M builds, and `GenericDriver.CreateConnection`
(`src/DbDataSync.Drivers.Generic/GenericDriver.cs:28-80`) needs the same builder-assembly factored out for
its own preview implementation, so this phase touches both driver projects, not just JDBC.

## Why

Two problems, one shared root: the Test Connection path tells an operator less than it could, in two
different ways.

1. **Nothing shows what actually got resolved.** An operator debugging a failed test has no way to see the
   connection string or JDBC URL DbDataSync actually built from their settings — only whether the attempt
   succeeded and a short error string. Seeing the resolved connection string has real diagnostic value for
   *every* driver, not just JDBC — it clarifies how Host/Port/Database/Properties actually resolved.
2. **Exception handling on this path is an allowlist, not a safety net.** `GenericDriverBase.TestAsync`
   (`src/DbDataSync.Drivers.Generic/GenericDriverBase.cs:147-161`) catches only `DbException`;
   `ConnectionsController.Test` (`src/DbDataSync.Api/Controllers/ConnectionsController.cs:132-191`) catches
   only `DbException`/`InvalidOperationException`/`SocketException`. A raw `java.sql.SQLException` (not a
   `DbException` subtype) escapes both, becoming an unhandled 500 — directly contradicting the controller's
   own doc comment ("reported as `succeeded: false` ... not as a 500"). This repo already paid for exactly
   this shape of bug once (phase 171V's `ServerVersion`/`DataSource` `NotImplementedException`); this phase
   closes the general case, not just that one instance.

## What changes

### `ConnectionPreview` — a new opt-in `IDriver` capability

```csharp
public sealed record ConnectionPreview(string ConnectionString, string? JdbcUri, IReadOnlyDictionary<string,string> Properties);
```

Same opt-in-interface shape `IConnectionTester` already uses. Both `GenericDriver` and `JdbcGenericDriver`
implement it by running their existing `CreateConnection` unification logic with a **masked placeholder**
standing in for the real credential — never the real secret, so a redaction bug downstream can't leak it.
Concretely: factor `CreateConnection`'s builder-assembly into a private `BuildUnifiedConnectionString
(ConnectionConfig, string? credential)` in each driver, called with the real resolved credential from
`CreateConnection` and with a literal masked value (e.g. `"••••••"`) from `PreviewConnection` — one
assembly path, two callers, no duplicated key-mapping logic.

For `GenericDriver`, `JdbcUri` is always `null` and `Properties` is always empty (nothing routes through an
out-of-connection-string channel there today — left as a real, reusable field rather than JDBC-only, per
the planning doc's own open question, in case a future ADO.NET driver needs it). For `JdbcGenericDriver`,
`JdbcUri` is the resolved JDBC URL and `Properties` is the `java.util.Properties` bag actually handed to
`driver.connect()`, both from phase 175M's unification output.

### `ConnectionTestReport` — three new fields

```csharp
public sealed record ConnectionTestReport(
    bool Succeeded, double ConnectMs, double ProbeMs, string? ServerVersion, string? Error,
    string? LibraryWarning = null,
    string? ResolvedConnectionString = null,
    string? JdbcUri = null,
    IReadOnlyDictionary<string, string>? OutsideProperties = null);
```

`ConnectionsController.Test` calls `driver.PreviewConnection(connection)` and folds the result in — on
**both** the success and failure paths (`ConnectionsController.cs:176-177` and `:183-184`). The failure path
matters most: seeing what was actually resolved and attempted is the diagnostic value an operator needs
precisely when a test fails.

### Broaden both catches to "everything except cancellation"

```csharp
// GenericDriverBase.TestAsync
catch (Exception ex) when (ex is not OperationCanceledException)

// ConnectionsController.Test
catch (Exception ex) when (ex is not OperationCanceledException)
```

`OperationCanceledException` is excluded deliberately — a cancelled test request is not a "connection
failed" answer, and reporting it as one would misreport what happened.

### `Diagnostics.Describe` — enrich, don't just widen

Widening the catch must not narrow what gets reported. `java.sql.SQLException` carries `getSQLState()`,
`getErrorCode()`, and a chain via `getNextException()` (distinct from `.Cause`/`.InnerException`) that
vendor drivers use for multi-part failures — today only `ex.Message` survives.

```csharp
internal static class Diagnostics
{
    public static string Describe(Exception ex)
    {
        if (ex is java.sql.SQLException sql)
        {
            var parts = new List<string>();
            for (var e = sql; e is not null; e = e.getNextException())
                parts.Add($"{e.Message} (SQLState={e.getSQLState()}, ErrorCode={e.getErrorCode()})");
            return string.Join(" | ", parts);
        }
        return ex.ToString(); // not ex.Message — an InvalidOperationException wrapping an inner
                               // exception (as every new phase-175M throw does) would otherwise lose it
    }
}
```

### `Redact` — by literal secret value, applied once at the boundary

```csharp
internal static string Redact(string text, params string?[] secrets)
{
    foreach (var secret in secrets)
        if (!string.IsNullOrEmpty(secret))
            text = text.Replace(secret, "••••••");
    return text;
}
```

Applied to `Error`, `ResolvedConnectionString`, `JdbcUri`, and `OutsideProperties` values, passing the
actual resolved credential(s) for that attempt. This is deliberately not "only show `DbException` messages"
or "omit the properties field" — either of those is exactly the mechanism that was dropping the real
diagnostic in the first place. Redact the one specific known-sensitive substring; keep everything else
(SQLState, error codes, the real host/port/database/URL shape, chained messages) intact.

## What this does not do

- Does not change anything JDBC-driver-internal — that's phase 175M, already landed by the time this phase
  starts.
- Does not add any new frontend surface for these fields — that's phase 177M.
- Does not attempt to redact anything beyond the literal known credential value(s) for the attempt in
  question — no heuristic scanning for "things that look like secrets" in arbitrary text.

## How to verify

- A successful test against a real (or fake, in unit tests) connection returns a populated
  `ResolvedConnectionString` with the real password replaced by the redaction marker, not omitted.
- A failing test — driven by each of: a `DbException`, an `InvalidOperationException` from phase 175M's new
  checks, and a raw unwrapped `java.sql.SQLException` — all return `Succeeded: false` with a populated
  `Error`, never an unhandled 500.
- A cancelled test request (cancel the `CancellationToken` mid-flight) does not produce a `Succeeded: false`
  report — confirm it propagates as a cancellation, not a reported failure.
- `Diagnostics.Describe` against a chained `java.sql.SQLException` (`getNextException()` returning more than
  one link) includes every link's message, SQLState, and error code in the final string.
- `Redact` leaves every part of a message untouched except the literal secret substring, confirmed against a
  message containing the secret in more than one place.
