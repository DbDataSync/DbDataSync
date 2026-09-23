# JDBC URL templating, connect-time validation, and connection-test diagnostics

**Status, 2026-09-22**: implemented — all four phase docs done (174M-177M). Two real corrections surfaced
building it that this design didn't anticipate, both documented in their own phase docs, not here:

175M found the scratch unification builder can't seed itself from a JDBC `ConnectionString` the way
`GenericDriver`'s does (it's a URL, not a `key=value` string) and dropped connect-timeout unification for
the same reason.

176M's correction had two rounds. The design's own implicit placement — one shared `Diagnostics` class,
pattern-matching `java.sql.SQLException` directly, called from both `GenericDriverBase.TestAsync` and
`ConnectionsController.Test` — doesn't compile where `TestAsync` lives (no IKVM visibility, can't gain any
without a dependency cycle). The *first* fix for that (java.sql-aware code in `DbDataSync.Api`, guarded
against IKVM.Java not being loaded) was itself architecturally wrong, caught on review: no shared,
non-JDBC-aware code should need to know `java.sql` types exist at all, guard or no guard. That guard's own
JIT-ordering subtlety was real too (found by an ordinary MsSql closed-port test, not hypothesized — as
designed, it would have broken Test Connection for every driver, not just JDBC's), but fixing the guard
would have papered over the actual mistake. The real fix moves translation to where it belongs: every
`java.sql.SQLException` a JDBC call can throw is caught and turned into `JdbcSqlException` (a plain
`DbException` subtype, no `java.sql` type anywhere in its own signature) inside `DbDataSync.Drivers.Jdbc`
itself, at the point each one is thrown. Nothing outside that project touches `java.sql` again.

Design closing three related gaps found while chasing a real "Connection is closed." report during a
JDBC connection test: the message was misleading because nothing in the JDBC path validates that a
connection actually opened, `JdbcDriverSpec` has no URL-template mechanism (`jdbc-driver-feature-gaps.md`'s
"No URL template" section already named this), and connection-test error handling in general drops
diagnostic detail it shouldn't. This doc supersedes that section of `jdbc-driver-feature-gaps.md` with a
concrete design; the entry there now points here.

## The bug that started this: `driver.connect()` can return null, silently

`JdbcProviderFactory.GetJdbcConnection` (`src/DbDataSync.Drivers.Jdbc/Imported/JdbcProviderFactory.cs:63-64`)
calls a specific `java.sql.Driver` instance's `connect(url, info)` directly rather than going through
`DriverManager.getConnection()`:

```csharp
public java.sql.Connection GetJdbcConnection(string url, java.util.Properties? properties = null) =>
    JdbcDriver.connect(url, properties ?? new java.util.Properties());
```

The JDBC spec allows (and expects, when a `DriverManager` is trying several drivers in turn) `connect()` to
return `null` rather than throw when the driver doesn't recognize the URL. `JdbcConnection.Open()`
(`Imported/JdbcConnection.cs:122-147`) never checks for that — `_connection` ends up `null`, `Open()`
returns normally, and the next command hits:

```csharp
public override ConnectionState State =>
    _connection != null && !_connection.isClosed() ? ConnectionState.Open : ConnectionState.Closed;

protected override DbCommand CreateDbCommand() =>
    State == ConnectionState.Open
        ? new JdbcCommand(this)
        : throw new InvalidOperationException("Connection is closed.");
```

— reporting a closed connection when the real problem was a URL the driver never accepted in the first
place, with no diagnostic pointing at that.

## `JdbcDriverSpec`: add a URL template, reuse `GenericConnectionStringKeys` as-is

`JdbcGenericDriver.CreateConnection` (`src/DbDataSync.Drivers.Jdbc/JdbcGenericDriver.cs:49-72`) today
requires `ConnectionConfig.ConnectionString` to already be the finished JDBC URL — "Host/Port addressing has
no URL template for this driver." The first instinct was to give `JdbcDriverSpec` its own small
`Username`/`Password` key-name record, on the theory that `GenericConnectionStringKeys`
(`src/DbDataSync.Drivers.Generic/GenericDriverSpec.cs:16-23`) is an ADO.NET-specific concept — it exists to
rename a flat `key=value;key=value` string's keywords per real provider (Npgsql's `Timeout` vs SqlClient's
`Connect Timeout`), and a JDBC URL is a positional, per-vendor-shaped string, not a flat key bag.

That's true of the *final* JDBC URL, but not of the intermediate representation. The design that survived
review reuses `GenericConnectionStringKeys` verbatim on `JdbcDriverSpec`, because the ADO.NET-style
unification step (below) never leaves this codebase — it's not handed to a real provider, so its key
spellings are free to be chosen as whatever's convenient, including the literal JDBC property names
(`user`/`password`) the driver will eventually receive. One record, one mechanism, no second mapping:

```csharp
public sealed record JdbcDriverSpec(
    ...
    string? UrlTemplate = null,                              // e.g. "jdbc:{prefix}://{host}:{port}/{database}"
    GenericConnectionStringKeys? ConnectionStringKeys = null, // defaults: Host="host", Port="port",
    ...                                                       // Database="database", Username="user", Password="password"
```

## `JdbcGenericDriver.CreateConnection`: unify exactly like `GenericDriver` does, then place-or-fallback

`GenericDriver.CreateConnection` (`src/DbDataSync.Drivers.Generic/GenericDriver.cs:28-80`) builds one plain
`DbConnectionStringBuilder`: raw `ConnectionString` loaded first if present, else `Host`; then `Database`,
`Port`, `ConnectTimeout`, the `AuthMode` branch, then arbitrary `connection.Properties` — all layered onto
the same builder via `Spec.ConnectionStringKeys`. `JdbcGenericDriver.CreateConnection` should run the
identical sequence, using the same key record, before doing anything JDBC-specific. This is what makes the
result "act like any other driver" from an operator's perspective: the same fields, resolved the same way,
regardless of what happens to them afterward.

Only once that builder is fully populated do the resolved values get pulled back out by key name — never
by parsing a JDBC URL, and never read straight off `connection.Host`/`connection.Port`, since those may be
unset when the operator chose connection-string addressing instead.

The step that's actually new: **host, port, database, and username can each be placed in the URL template,
or fall back to a JDBC property if the template doesn't reference them** — nothing gets resolved and then
silently discarded because a template happened not to mention it. `Password` is the one exception: it is
*never* a template placeholder, unconditionally a property, matching the existing rule already documented
on `ConnectionConfig.ConnectionString` (a credential never appears in the URL).

```csharp
var urlText = connection.ConnectionString ?? Spec.UrlTemplate
    ?? throw new InvalidOperationException(
        $"'{Spec.Id}': no ConnectionString and no UrlTemplate — nothing to build a JDBC URL from.");

var props = new java.util.Properties();
string PlaceOrFallback(string url, string placeholder, string? value, string propertyKey)
{
    if (value is null) return url;
    var token = "{" + placeholder + "}";
    if (url.Contains(token)) return url.Replace(token, value);
    props.setProperty(propertyKey, value);
    return url;
}

var jdbcUrl = urlText;
jdbcUrl = PlaceOrFallback(jdbcUrl, "host", host, keys.Host);
jdbcUrl = PlaceOrFallback(jdbcUrl, "port", port, keys.Port);
jdbcUrl = PlaceOrFallback(jdbcUrl, "database", database, keys.Database);
jdbcUrl = PlaceOrFallback(jdbcUrl, "username", username, keys.Username);
if (password is not null) props.setProperty(keys.Password, password);
```

This also covers the existing literal-URL-override path for free: a hand-pasted JDBC URL has no
`{placeholder}` tokens in it at all, so every resolved value falls back to a property automatically — the
same outcome as today, not a behavior change for operators who already type the whole URL by hand.

`JdbcConnectionStringBuilder.CreateConnectionString(Spec.DriverClass, jdbcUrl, props)`
(`Imported/JdbcConnectionStringBuilder.cs:53-60`) already exists and needs no change — `JdbcConnection.Open()`
keeps parsing `JdbcDriver`/`JdbcUrl`/properties back out of it exactly as it does today.

## Validate before and after connecting, instead of trusting silence

`JdbcProviderFactory.GetJdbcConnection` should check `driver.acceptsURL(url)` before calling `connect()`,
and treat a null result from `connect()` as a hard error rather than a valid-looking closed connection:

```csharp
public java.sql.Connection GetJdbcConnection(string url, java.util.Properties? properties = null)
{
    if (!JdbcDriver.acceptsURL(url))
        throw new InvalidOperationException($"Driver '{_driverClass}' does not accept URL '{url}'.");
    return JdbcDriver.connect(url, properties ?? new java.util.Properties())
        ?? throw new InvalidOperationException($"Driver '{_driverClass}' returned no connection for URL '{url}'.");
}
```

`JdbcConnection.Open()` should additionally call `java.sql.Connection.isValid(int)` after assigning
`_connection`, but **softly** — not every vendor driver implements it reliably, and this codebase already
hit this exact trap once before: the phase-171V doc comment on `ChangeDatabase`
(`Imported/JdbcConnection.cs:64-73`) describes `ServerVersion`/`DataSource` throwing `NotImplementedException`
until implemented, breaking every JDBC connection test because a non-`DbException` escaped
`GenericDriverBase.TestAsync`'s catch. `isValid` needs the same defensive posture already used for
`ChangeDatabase`/`BeginDbTransaction` in this file — catch `java.sql.SQLException` and treat it as
inconclusive, not fatal:

```csharp
try
{
    if (!_connection.isValid(JdbcValidateTimeoutSeconds))
        throw new InvalidOperationException($"The JDBC driver reported the new connection to '{jdbcUrl}' as invalid.");
}
catch (java.sql.SQLException)
{
    // isValid support is inconsistent across vendors. acceptsURL + the null check above already did
    // the real work; an exception here means "can't tell," not "connection is bad."
}
```

`JdbcValidateTimeoutSeconds` should be its own small constant, not a reuse of
`ConnectionTimeouts.DefaultConnectSeconds` — `isValid`'s `timeout` parameter means "0 = block with no
limit" per spec (not "skip the check"), and it pays its own round trip on top of the connect that already
happened, so reusing the full connect timeout doubles worst-case latency for no reason.

## Connection preview: show the resolved connection string and JDBC URI on Test

`ConnectionTestReport` (`src/DbDataSync.Api/Controllers/ConnectionsController.cs:330-331`) gains fields for
what actually got resolved, redacted, not just whether the test passed:

```csharp
public sealed record ConnectionTestReport(
    bool Succeeded, double ConnectMs, double ProbeMs, string? ServerVersion, string? Error,
    string? LibraryWarning = null,
    string? ResolvedConnectionString = null,
    string? JdbcUri = null,
    IReadOnlyDictionary<string, string>? OutsideProperties = null);
```

This is valuable for every driver, not only JDBC — seeing the resolved ADO.NET connection string clarifies
how Host/Port/Database/Properties actually resolved even for a plain `GenericDriver`. A new `IDriver`
member (same opt-in-interface shape as `IConnectionTester`) computes it:

```csharp
public sealed record ConnectionPreview(string ConnectionString, string? JdbcUri, IReadOnlyDictionary<string,string> Properties);
```

`GenericDriver.CreateConnection` and `JdbcGenericDriver.CreateConnection` both need their builder-assembly
logic factored so a preview call can run the identical unification with a placeholder standing in for the
real credential — never the real secret, so a redaction bug after the fact can't leak it. A private
`BuildUnifiedConnectionString(ConnectionConfig, string? credential)` in each, called with the real resolved
credential from `CreateConnection` and with a literal masked placeholder from `PreviewConnection`, covers
both callers without duplicating the key-mapping logic twice.

`ConnectionsController.Test` calls `driver.PreviewConnection(connection)` and folds the result into
`ConnectionTestReport` on **both** the success and failure paths — especially valuable on failure, since
seeing what was actually resolved and attempted is the diagnostic value operators need most when a test
fails.

## Connection-test exception handling: catch broadly, redact by value, never by omission

Today's catches are an *allowlist of exception types*, which is the wrong shape for a diagnostic endpoint:

```csharp
// GenericDriverBase.TestAsync
catch (DbException ex) { ... }

// ConnectionsController.Test
catch (Exception ex) when (ex is DbException or InvalidOperationException or SocketException) { ... }
```

Anything outside that allowlist — notably a raw `java.sql.SQLException`, which is not a `DbException`
subtype — escapes both catches entirely and becomes an unhandled 500, directly contradicting the
controller's own stated goal ("reported as `succeeded: false` ... not as a 500"). Both should widen to
catch everything **except** `OperationCanceledException` (a cancelled test request is not a "connection
failed" answer, and conflating the two misreports what happened):

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, Diagnostics.Describe(ex));
}
```

Widening the catch must not narrow what gets reported. `java.sql.SQLException` carries `getSQLState()`,
`getErrorCode()`, and a chain via `getNextException()` (distinct from `.Cause`/`InnerException`) that vendor
drivers use for multi-part failures — today only `ex.Message` survives. A shared helper should walk that
chain, and fall back to `ex.ToString()` (not `ex.Message`) for everything else, so an `InvalidOperationException`
wrapping an inner exception (as every throw introduced above does) doesn't lose the inner message:

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
        return ex.ToString();
    }
}
```

Redaction is by literal secret value, applied once at the boundary, never by hiding a whole field or
restricting which exception types are allowed through (that's the mechanism that was dropping the real
diagnostic in the first place):

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
actual resolved credential(s) for that attempt. Everything else — SQLState, error codes, the real
host/port/database/URL shape, chained messages — stays intact.

## Open questions

- **Prove the template shape against a second vendor before generalizing.** `jdbc-driver-feature-gaps.md`
  already flagged this reasoning for the `typeMap`-per-vendor question; the same caution applies here —
  this design is informed by one "standard `jdbc:{prefix}://host:port`" shape. A vendor whose URL puts
  database or auth in a structurally different place (Oracle's `@//host:port/service`, SQL Server's
  `;databaseName=`) should be tried against this template mechanism before it's considered proven generally.
- Should a template author be actively blocked (at descriptor-load time) from writing a `{password}`
  placeholder, rather than just having it silently never matched? Leaning yes — a hard validation error is
  more honest than a template that looks like it supports it but never will. **Still open as implemented**
  — 175M's `PlaceOrFallback` is simply never called for password, so a `{password}` token in a template
  today just sits there unsubstituted rather than being rejected; no descriptor-load-time validation was
  added. Given its own follow-up doc:
  `architecture/planning/done/follow-up-jdbc-url-template-password-placeholder-validation.md`.
- `OutsideProperties` is always empty for a plain `GenericDriver` today (nothing routes through it) — worth
  confirming that's fine to leave as "always empty, not removed" rather than making it JDBC-only, since a
  future ADO.NET driver with its own out-of-connection-string properties could reuse the same field.
  **Confirmed, as implemented** — 176M kept it exactly this shape (`GenericDriver.PreviewConnection`
  always returns an empty dictionary), per this note's own reasoning.
- **Found after "done," not anticipated by this design**: nothing built here is reachable from a
  `driver.yaml` at all — the descriptor schema never grew `UrlTemplate`/`ConnectionStringKeys` fields, so
  every phase 174M-177M mechanism only exists from direct C# construction (every test fixture). Its own
  follow-up: `architecture/planning/done/follow-up-jdbc-url-template-unreachable-from-driver-yaml.md`.
- **Also found after "done"**: `isValid`'s two negative branches and every JDBC-through-the-console
  Playwright flow have no real-failure test coverage (same root cause — no jar/`ikvm` fixture in the
  Web.Tests scratch repo). Its own follow-up:
  `architecture/planning/todo/follow-up-jdbc-connection-failure-test-coverage-gaps.md`.

## Cross-references

- `jdbc-driver-feature-gaps.md` — "No URL template" section, now superseded by this doc.
- `jdbc-driver-support.md` — "The connection model" section this design fills in the other half of.
- `architecture/implementation/todo/phase-171V-jdbc-connection-testing-completeness.md` — the earlier,
  narrower instance of the same "non-`DbException` escapes the test path" bug class.
- `driver-yaml-authoring-ui.md` — the JDBC create-path screen this eventually needs a `urlTemplate` field
  and connection-string-keys editor added to, once this design is implemented.

## Phase docs

Split into four small, ordered phases rather than one large one — namespace cleanup, then the driver-layer
mechanics, then the shared backend surface, then the frontend that shows it:

- `architecture/implementation/done/phase-174M-jdbc-namespace-rename-away-from-imported.md` — `Jdbc.Imported`
  → `Jdbc.Ado` (a separate, unrelated naming cleanup raised in the same conversation, sequenced first since
  it touches the same files phase 175M does).
- `architecture/implementation/done/phase-175M-jdbc-url-template-and-connect-validation.md` — this doc's
  `JdbcDriverSpec`/`JdbcGenericDriver`/`JdbcProviderFactory`/`JdbcConnection` sections.
- `architecture/implementation/done/phase-176M-connection-test-preview-and-diagnostics-backend.md` — this
  doc's connection-preview and exception-handling sections.
- `architecture/implementation/todo/phase-177M-connection-test-preview-frontend.md` — surfacing 176M's new
  fields in the web console.

## Closed out, 2026-09-23

174M, 175M, 176M all independently verified (real test runs, not just code reading) and moved to
`architecture/implementation/done/`. Both real gaps this design's own retrospectives found "after done" —
a `{password}` placeholder in a `UrlTemplate` never being rejected, and a hand-authored `driver.yaml`
having no way to set `urlTemplate`/`connectionStringKeys` at all — are themselves now fixed, by
`phase-178N`; see `follow-up-jdbc-url-template-password-placeholder-validation.md` and
`follow-up-jdbc-url-template-unreachable-from-driver-yaml.md`, both closed.

**177M stays in `todo/`.** Its backend half is solid, but the phase's own point — a JDBC connection
test's `jdbcUri` actually showing up in the console on a rejected-URL failure — has never been observed
running in a real browser; no Playwright spec opens a real JDBC connection through the console at all.
Closing 177M needs that real run, not another code review.
