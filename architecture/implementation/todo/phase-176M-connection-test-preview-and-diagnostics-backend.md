# Phase 176M — Connection-test preview and broadened diagnostics (backend)

**Status**: Done, 2026-09-22. `ConnectionPreview`/`IConnectionPreviewer` (new, `DbDataSync.Drivers.Abstractions`),
implemented by both `GenericDriver` and `JdbcGenericDriver`; `ConnectionTestReport`'s three new fields;
both catches widened; `ConnectionDiagnostics.Describe`/`Redact` (`DbDataSync.Api`). Real, load-bearing
corrections found building it, not part of the design above — see "What changed from the design" below,
especially the IKVM one: the design as written would have taken down **every** driver's Test Connection,
not just JDBC's, the first time it hit an ordinary failure. Caught by the existing integration suite, not
invented as a hypothetical — see that section for how.
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

## What changed from the design, found building it

- **`Diagnostics` can't live where the design's own code sketch implies (one shared class both
  `GenericDriverBase.TestAsync` and `ConnectionsController.Test` call identically).** `Describe` touches
  `java.sql.SQLException` by name, which needs a compile-time reference to IKVM's Java surface.
  `GenericDriverBase.TestAsync` lives in `DbDataSync.Drivers.Generic`, which has no such reference and
  can't gain one — that would mean depending on `DbDataSync.Drivers.Jdbc`, which itself depends on
  `DbDataSync.Drivers.Generic`, a cycle. Resolved by *not* sharing one class: `GenericDriverBase.TestAsync`'s
  own catch widened to "everything except cancellation" and now returns `ex.ToString()` directly (no
  java.sql-aware enrichment — it structurally can't have any), while the full `ConnectionDiagnostics`
  class (`Describe`/`Redact`, java.sql-aware) lives in `DbDataSync.Api`, the one layer that already
  references `DbDataSync.Drivers.Jdbc` (to host the driver at all) and so can actually see that type. A
  JDBC probe failure inside `TestAsync` itself (as opposed to a JDBC *connect* failure, caught in the
  controller) gets the plainer `ex.ToString()` treatment as a result — a real, accepted scope reduction,
  not an oversight.
- **Even with that split, the design's own literal code sketch (`if (ex is java.sql.SQLException sql)`
  inline inside a try/catch) is a real bug, not a style choice — found by the existing integration
  suite, not invented.** `ConnectionTestIntegrationTests.Test_AgainstAClosedPort_ReportsFailureRatherThanThrowing`
  (an ordinary MsSql closed-port test, no JDBC driver anywhere in that process) started failing with an
  unhandled `FileNotFoundException` for `IKVM.Java` the moment `Describe`'s widened catch first ran. Root
  cause: the JIT resolves every type a method's IL references while compiling that *whole method*, before
  any of its own try/catch executes — so a type-load failure for a type referenced inside a method's own
  try block still isn't catchable by that same try/catch. Confirmed directly: the inline version, wrapped
  in the exact catch clause the fix ended up using, reproduced the identical unhandled exception. Fixed by
  moving the `java.sql.SQLException` touch into its own private method (`TryDescribeSqlException`), called
  from inside `Describe`'s try — because *that* method's JIT compilation is deferred to its first call,
  which happens inside the try, the resulting load failure is a call-site exception the surrounding catch
  genuinely sees. This means the *design's* own sketch, if implemented literally, would have broken Test
  Connection for **every** driver — not a JDBC-only edge case — the first time any connection failed for
  an ordinary reason in a process that had never touched IKVM.
- **`GenericDriver.PreviewConnection`'s `Properties` is a fresh empty `Dictionary<string, string>` per
  call**, not a cached singleton — cheap enough (always empty) that this wasn't worth optimizing away, and
  keeps the record's own field genuinely a real, per-call value rather than one shared mutable instance an
  overly-clever caller could mutate.

## How to verify

- A successful test against a real connection returns a populated `ResolvedConnectionString` with the real
  password replaced by the redaction marker, not omitted — proven at the driver level (not yet wired
  through a live API-level test — see below) by `GenericDriverTests.PreviewConnection_MasksTheCredential_AndNeverTouchesTheNetwork`
  (a real Postgres fixture) and `JdbcUrlTemplateTests.PreviewConnection_MasksTheCredential_AndReportsTheResolvedJdbcUri`.
- A failing test — driven by an `InvalidOperationException` (phase 175M's own checks) and an ordinary
  exception — returns `Succeeded: false` with a populated `Error`, never an unhandled 500:
  `ConnectionTestIntegrationTests`'s existing closed-port test (unchanged assertions, now exercising the
  widened catch) plus the two new `ConnectionDiagnosticsTests.Describe_*` tests. **Not covered**: a raw
  unwrapped `java.sql.SQLException` specifically, end to end through the API — no fixture in this test
  suite opens a real JDBC connection through `DbDataSync.Api` (same gap phase 175M's own doc already
  names for `isValid`). The non-JDBC path this bug actually broke *is* covered, which is what the real
  incident needed.
- A cancelled test request does not produce a `Succeeded: false` report:
  `GenericDriverTests.TestAsync_WithAnAlreadyCancelledToken_PropagatesCancellation_InsteadOfReportingFailure`.
- `Redact` leaves every part of a message untouched except the literal secret substring, including a
  message containing the secret more than once and a call with multiple distinct secrets:
  `ConnectionDiagnosticsTests.Redact_*`.
- `Diagnostics.Describe` against a chained `java.sql.SQLException` — **not covered**, for the same reason
  as the API-level JDBC gap above: this test project has no IKVM.Java loaded to construct one, and
  fabricating that instance without a real JDBC driver behind it isn't practical over IKVM interop (no
  precedent anywhere in this codebase for mocking a `java.sql.*` type). What *is* covered, and is the more
  load-bearing property: `Describe` doesn't throw when IKVM.Java is absent, which is the actual failure
  this phase's own build surfaced.
