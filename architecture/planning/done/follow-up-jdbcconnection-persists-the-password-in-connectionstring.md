# Follow-up: `JdbcConnection.ConnectionString` keeps the password in plaintext for the connection's whole life

**Resolved 2026-09-22**, built alongside phase 171V (same file, same `Open()` method). Found while
answering a question about JDBC credential handling, documented to fix later — "later" turned out to be
the same session.

## What's true today

The password never touches the JDBC URL or a query string — `java.sql.Driver.connect(url, properties)` is
called with the credential as a `java.util.Properties` entry, url and credential kept apart the whole way
down:

```csharp
// JdbcConnection.cs:80-96 (Open())
var jdbcUrl = builder.JdbcUrl ?? throw ...;                    // bare URL, no credentials
_connection = factory.GetJdbcConnection(jdbcUrl, builder.GetProperties());  // password is a Properties entry
```

```csharp
// JdbcProviderFactory.cs:52-53
public java.sql.Connection GetJdbcConnection(string url, java.util.Properties? properties = null) =>
    JdbcDriver.connect(url, properties ?? new java.util.Properties());
```

So there's exactly one place the password is stored in plaintext, not several — but that one place is held
for the life of the connection object with no attempt to clear it:

```csharp
// JdbcConnection.cs:20
[AllowNull] public override string ConnectionString { get; set; } = "";
```

`JdbcGenericDriver.CreateConnection` sets this once, from a `JdbcConnectionStringBuilder` that has
`user`/`password` in it (`builder["password"] = credential`). `DbConnectionStringBuilder.ConnectionString`
reconstructs the full `key=value;...` string on read, password included. Nothing clears or masks it after
`Open()`.

## Why this is a real gap, not just a theoretical one

`SqlConnection` — the driver every DbDataSync operator is most likely to compare this against — does not
have this problem, by default: `Persist Security Info=false` (SqlClient's default) makes
`SqlConnection.ConnectionString` **strip the password after `Open()`** specifically so a caller holding an
already-open connection can't read the credential back out.
[Microsoft's own docs](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/protecting-connection-information):
*"the connection string that is returned is the same as the user-set ConnectionString, minus security
information if the Persist Security Info value is set to false (default)... This property should only be
set to true if your application has a specific need to read the password out of an already-opened database
connection... using true opens your application to security risks, such as accidentally logging or tracing
the database password."*

`JdbcConnection` has no equivalent of that default. And unlike most drivers, `JdbcConnection` is
**deliberately public** (phase 167V) so a `metadataProvider` script can hold a direct reference to it via
`MetadataContext.Connection` — the exact "caller holding an already-open connection" scenario SqlClient's
default exists to defend against. A script with that reference can read `.ConnectionString` and get the
plaintext password back.

## Not exploited today

Confirmed by grep: nothing in `DbDataSync.Api`/`DbDataSync.TaskRunner` reads `JdbcConnection.ConnectionString`
back for logging, tracing, or display. This is a soft spot in the object's own posture, not a live leak.

## Suggested fix, for whenever this gets picked up

Strip `user`/`password` from the stored `ConnectionString` once `Open()` has consumed them — the same shape
SqlClient's `Persist Security Info=false` produces:

```csharp
public override void Open()
{
    ...
    _connection = factory.GetJdbcConnection(jdbcUrl, builder.GetProperties());
    builder.Remove("user");
    builder.Remove("password");
    ConnectionString = builder.ConnectionString;   // password no longer readable back out
}
```

Worth checking whether anything relies on re-reading `JdbcConnection.ConnectionString` post-`Open()` for a
legitimate reason (e.g. a retry path that reopens from the stored string) before doing this — a quick grep
of call sites, not a design question.

## Resolution

Built exactly as suggested above. The grep came back empty — nothing in this repo reads
`JdbcConnection.ConnectionString` back after `Open()` — so the fix landed with no other call site to
reconcile. Two new tests in `JdbcConnectionTests.cs` cover it: the password is gone from the stored
string, and the rest of the round-trippable state (`JdbcUrl`/`JdbcDriver`) survives the strip.
