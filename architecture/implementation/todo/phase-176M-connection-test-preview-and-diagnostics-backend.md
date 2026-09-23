# Phase 176M — Connection-test preview and broadened diagnostics (backend)

**Status**: Done, 2026-09-22. `ConnectionPreview`/`IConnectionPreviewer` (new, `DbDataSync.Drivers.Abstractions`),
implemented by both `GenericDriver` and `JdbcGenericDriver`; `ConnectionTestReport`'s three new fields;
both catches widened.

**Corrected the same day, on review.** The first version of this phase put java.sql-aware exception
translation in `DbDataSync.Api` (`ConnectionDiagnostics.Describe` pattern-matching `java.sql.SQLException`
directly, wrapped in a `FileNotFoundException`-catching guard once that was found to crash every driver's
Test Connection, not just JDBC's — see the old text this replaced, preserved in git history at
`b781ac6`). That was a real architecture mistake, not a style choice, flagged on review: *no* project
outside `DbDataSync.Drivers.Jdbc` should ever need to know `java.sql` types exist at all. The actual fix —
what "What changed from the design" now describes — moves the translation to where it belongs: every
`java.sql.SQLException` a JDBC call can throw is caught and translated into `JdbcSqlException` (a plain
`DbException` subtype, `Ado/JdbcSqlException.cs`) at the point it's thrown, inside
`DbDataSync.Drivers.Jdbc` itself. `ConnectionDiagnostics` in `DbDataSync.Api` now touches zero `java.sql`
types — no guard, no isolated-method JIT workaround, none needed, because there's nothing left to guard
against.
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

- **The design's own implicit placement — one shared `Diagnostics` class, pattern-matching
  `java.sql.SQLException` directly, called identically from `GenericDriverBase.TestAsync` and
  `ConnectionsController.Test` — is architecturally wrong, not just inconvenient for
  `GenericDriverBase.TestAsync` to reach.** `GenericDriverBase.TestAsync` lives in
  `DbDataSync.Drivers.Generic`, which has no compile-time visibility into `java.sql.SQLException` and
  can't gain any (would mean depending on `DbDataSync.Drivers.Jdbc`, which itself depends on
  `DbDataSync.Drivers.Generic` — a cycle) — that part of the original finding stood. But the fix shipped
  first (putting the java.sql-aware half in `DbDataSync.Api`, the layer that *does* transitively reference
  `DbDataSync.Drivers.Jdbc`) was itself wrong: it made an incidental transitive reference load-bearing for
  a raw Java exception type nothing about `DbDataSync.Api`'s own job requires it to know exists, and it
  put shared, provider-agnostic diagnostic code in the position of having to defend itself against IKVM.Java
  not being loaded — real complexity (a `FileNotFoundException`-catching guard, a JIT-compilation-order
  workaround splitting `Describe` into two methods) that only existed because the translation was happening
  in the wrong project. Caught on review, not by a test: no shared, non-JDBC-aware code should ever need
  to know `java.sql` types exist, full stop.
- **The actual fix: `java.sql.SQLException` never leaves `DbDataSync.Drivers.Jdbc` at all.** New
  `JdbcSqlException` (`Ado/JdbcSqlException.cs`) — a plain `System.Data.Common.DbException` subtype
  carrying `SqlState`/`ErrorCode`/message as `string`/`int` `JdbcSqlError` records, zero `java.sql` types
  in its own public signature. Every real `java.sql` call that can throw one is wrapped and translated at
  the point of the call, inside this project (the one place a live `java.sql.SQLException` is now ever
  allowed to exist): `JdbcProviderFactory.GetJdbcConnection` (`acceptsURL`/`connect`), `JdbcCommand.ExecuteNonQuery`/
  `ExecuteDbDataReader`/`Cancel`, `JdbcDataReader.Read`/its value-reading path. `GenericDriverBase.TestAsync`'s
  catch stays widened (a real, independently-useful safety net for any provider's non-`DbException`
  failure) but now needs no java.sql awareness at all: a JDBC failure already arrives as an ordinary
  `DbException`, so `ex.ToString()` already carries the full `SqlState`/`ErrorCode`/chain detail —
  `JdbcSqlException`'s own constructor builds that into its `Message` once, at the translation site.
  `ConnectionDiagnostics` in `DbDataSync.Api` shrank to `Describe(ex) => ex is JdbcSqlException jdbc ?
  jdbc.Message : ex.ToString()` — a plain type check against a type with no IKVM dependency in its
  signature, no guard, nothing that can fail to load, because `JdbcSqlException` is only ever constructed
  from a *live* `java.sql.SQLException` (meaning IKVM is necessarily already loaded whenever that
  construction happens) and never inspected as a Java type again after.
- **`GenericDriver.PreviewConnection`'s `Properties` is a fresh empty `Dictionary<string, string>` per
  call**, not a cached singleton — cheap enough (always empty) that this wasn't worth optimizing away, and
  keeps the record's own field genuinely a real, per-call value rather than one shared mutable instance an
  overly-clever caller could mutate.

## How to verify

- A successful test against a real connection returns a populated `ResolvedConnectionString` with the real
  password replaced by the redaction marker, not omitted:
  `GenericDriverTests.PreviewConnection_MasksTheCredential_AndNeverTouchesTheNetwork` (a real Postgres
  fixture) and `JdbcUrlTemplateTests.PreviewConnection_MasksTheCredential_AndReportsTheResolvedJdbcUri`.
- A failing test — driven by an `InvalidOperationException` (phase 175M's own checks) and an ordinary
  exception — returns `Succeeded: false` with a populated `Error`, never an unhandled 500:
  `ConnectionTestIntegrationTests`'s existing closed-port test (the regression proof: it's the test that
  caught the original architecture mistake, and still passes against the corrected one) plus
  `ConnectionDiagnosticsTests.Describe_*`.
- A cancelled test request does not produce a `Succeeded: false` report:
  `GenericDriverTests.TestAsync_WithAnAlreadyCancelledToken_PropagatesCancellation_InsteadOfReportingFailure`.
- `Redact` leaves every part of a message untouched except the literal secret substring, including a
  message containing the secret more than once and a call with multiple distinct secrets:
  `ConnectionDiagnosticsTests.Redact_*`.
- A real, chained `java.sql.SQLException` translates to `JdbcSqlException` with the right `SqlState`/chain
  — **now covered**, against genuinely bad SQL run through the real Postgres fixture, not a hand-built
  exception: `JdbcSqlExceptionTests.ExecuteNonQuery_WithInvalidSql_ThrowsJdbcSqlException_NotARawJavaException`
  (Postgres SQLState `42601`, syntax error) and `ExecuteReaderAsync_WithInvalidSql_ThrowsJdbcSqlException`
  (`42P01`, undefined table). `ConnectionDiagnostics.Describe`'s own chain/SQLState/ErrorCode formatting —
  also now covered, trivially, with a hand-built `JdbcSqlException` (no IKVM needed at all, since the type
  itself carries none):
  `ConnectionDiagnosticsTests.Describe_AJdbcSqlException_IncludesEveryChainedErrorsSqlStateAndErrorCode`.
  Both were undocumented gaps in this doc's first version, closed by the same fix that corrected the
  architecture — not a coincidence: the old design made this untestable without a live IKVM-loaded
  process; the corrected one makes it trivial.
- `isValid`'s two negative branches (phase 175M) — still not covered, for the reason already given there:
  needs a `java.sql.Connection` that reports `false`/throws on demand, a different problem than the one
  this correction solved (that needs mocking a live Java object's *behavior*, not just translating an
  exception it already threw).
