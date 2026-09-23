# Phase 175M — JDBC URL templating, host/port/database/user placement, and connect-time validation

**Status**: Not started — design only.
**Plan reference**: `architecture/planning/todo/jdbc-url-template-and-connection-testing.md` (full
rationale, design discussion, and code sketches — this phase doc scopes exactly the driver-layer slice of
it). **Depends on phase 174M** landing first — every file this phase touches (`JdbcConnection.cs`,
`JdbcProviderFactory.cs`) moves namespace there; writing this phase against the old `Imported` namespace
would just create rebase noise.

## Why

Two real problems, found chasing a live "Connection is closed." connection-test report back to its root
cause:

1. `JdbcProviderFactory.GetJdbcConnection` (`Ado/JdbcProviderFactory.cs:63-64`) calls `java.sql.Driver
   .connect(url, info)` directly. The JDBC spec allows `connect()` to return `null` when the driver doesn't
   recognize the URL — normal when `DriverManager` is trying several drivers in turn, silent and wrong here,
   since exactly one driver is ever asked. `JdbcConnection.Open()` (`Ado/JdbcConnection.cs:122-147`) never
   checks for that: `_connection` ends up `null`, `Open()` returns normally, and the next command throws a
   flatly misleading `"Connection is closed."` — the real problem (a URL the driver never accepted) leaves
   no trace.
2. `JdbcDriverSpec` has no URL-template mechanism at all — `JdbcGenericDriver.CreateConnection`
   (`JdbcGenericDriver.cs:49-72`) requires `ConnectionConfig.ConnectionString` to already be the complete,
   final JDBC URL. Every other driver in this repo addresses by Host/Port/Database; a JDBC engine forces an
   operator to hand-type vendor URL syntax from memory. Already named as a gap in
   `architecture/planning/todo/jdbc-driver-feature-gaps.md`'s "No URL template" section.

## What changes

### `JdbcDriverSpec` — two new fields

```csharp
string? UrlTemplate = null,                              // e.g. "jdbc:{prefix}://{host}:{port}/{database}"
GenericConnectionStringKeys? ConnectionStringKeys = null, // reused type — see rationale below
```

**Reuses `GenericConnectionStringKeys`** (`src/DbDataSync.Drivers.Generic/GenericDriverSpec.cs:16-23`)
rather than inventing a JDBC-specific record. That type exists to rename an ADO.NET connection string's
flat `key=value` keywords per real provider — a JDBC URL isn't that shape, so the instinct was a smaller,
JDBC-only `Username`/`Password` record. That's unnecessary: the intermediate unification step below never
leaves this codebase (it's not handed to a real provider), so its key spellings are free to be chosen as
whatever's most convenient — including the literal `java.util.Properties` keys (`user`/`password`) JDBC
will eventually receive. One mechanism, one type, no second mapping. Sensible JDBC defaults:
`Host="host"`, `Port="port"`, `Database="database"`, `Username="user"`, `Password="password"`.

### `JdbcGenericDriver.CreateConnection` — unify like every other driver, then place-or-fallback

Mirrors `GenericDriver.CreateConnection` (`src/DbDataSync.Drivers.Generic/GenericDriver.cs:28-80`) exactly
through the unification step: one plain `DbConnectionStringBuilder`, raw `ConnectionString` loaded first if
present else `Host`, then `Database`/`Port`/`ConnectTimeout`/the `AuthMode` branch/`connection.Properties`,
all via `Spec.ConnectionStringKeys` — so a JDBC connection resolves its fields exactly the way every ADO.NET
driver's does, before anything JDBC-specific happens to the result.

Only then are values read back out of that builder by key (never by parsing a URL, never straight off
`connection.Host`/`Port`, since those may be unset under connection-string addressing). The new part: host,
port, database, and username can each be placed into the URL template, **or fall back to a JDBC property if
the template doesn't reference them** — nothing resolved is ever silently discarded because a template
happened not to mention it. `Password` is never a template placeholder under any circumstance — always a
property, matching the existing "credential never in the URL" rule already documented on
`ConnectionConfig.ConnectionString`.

```csharp
string PlaceOrFallback(string url, string placeholder, string? value, string propertyKey)
{
    if (value is null) return url;
    var token = "{" + placeholder + "}";
    if (url.Contains(token)) return url.Replace(token, value);
    props.setProperty(propertyKey, value);
    return url;
}
```

This also covers the existing hand-typed-URL path for free: a literal URL has no `{placeholder}` tokens at
all, so every resolved value falls back to a property automatically — the same outcome as today for anyone
who already types the whole URL, not a behavior change for them.

`JdbcConnectionStringBuilder.CreateConnectionString(Spec.DriverClass, jdbcUrl, props)`
(`Ado/JdbcConnectionStringBuilder.cs:53-60`) already exists and needs no change — `JdbcConnection.Open()`
keeps parsing `JdbcDriver`/`JdbcUrl`/properties back out of it exactly as it does today.

### `JdbcProviderFactory.GetJdbcConnection` — fail loudly, not silently

```csharp
public java.sql.Connection GetJdbcConnection(string url, java.util.Properties? properties = null)
{
    if (!JdbcDriver.acceptsURL(url))
        throw new InvalidOperationException($"Driver '{_driverClass}' does not accept URL '{url}'.");
    return JdbcDriver.connect(url, properties ?? new java.util.Properties())
        ?? throw new InvalidOperationException($"Driver '{_driverClass}' returned no connection for URL '{url}'.");
}
```

### `JdbcConnection.Open()` — verify the connection is actually valid, softly

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

Soft by design — this repo already hit the "unconditionally trusting a JDBC optional feature" trap once,
in `ServerVersion`/`DataSource` (see `Ado/JdbcConnection.cs:64-73`'s own phase-171V doc comment: a
`NotImplementedException` that wasn't a `DbException` broke every JDBC connection test until it shipped).
`isValid` gets the same defensive treatment already used for `ChangeDatabase`/`BeginDbTransaction` in this
file. `JdbcValidateTimeoutSeconds` is its own small constant, not a reuse of
`ConnectionTimeouts.DefaultConnectSeconds` — `isValid`'s `timeout` parameter means "0 = block with no
limit" per spec (not "skip the check"), and it pays its own round trip on top of the connect that already
happened.

## What this does not do

- Does not touch `ConnectionTestReport`, `IDriver.PreviewConnection`, or any exception-handling/redaction
  work — that's phase 176M, and depends on this phase's unification logic existing first.
- Does not add operator-facing UI for a `urlTemplate` field or a connection-string-keys editor —
  `architecture/planning/todo/driver-yaml-authoring-ui.md` owns that surface; this phase only makes the
  mechanism exist and work from a hand-authored `driver.yaml`.
- Does not attempt a second real JDBC vendor descriptor to prove the template shape generalizes past
  Postgres — named as an open question in the planning doc, deliberately out of scope here.

## How to verify

- A `JdbcDriverSpec` built with a `UrlTemplate` and `Host`/`Port`/`Database` addressing produces the
  expected JDBC URL and property set for at least two shapes: a template that references every placeholder,
  and one that references only some (proving the fallback-to-property path).
- A `JdbcDriverSpec` built with no `UrlTemplate` and a hand-typed `ConnectionString` still connects exactly
  as before (regression check against the existing behavior).
- A URL that a real loaded driver's `acceptsURL` rejects produces a clear `InvalidOperationException`
  naming the URL, not a downstream "Connection is closed."
- `isValid` returning `false` fails `Open()` with a clear message; `isValid` throwing `SQLException` does
  not fail `Open()` at all.
