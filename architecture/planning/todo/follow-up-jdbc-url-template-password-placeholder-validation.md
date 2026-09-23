# Follow-up: block a `{password}` placeholder in a JDBC URL template instead of silently ignoring it

**Status: fixed (2026-09-23), phase 178N.** Implemented exactly as suggested — the eager check in
`JdbcGenericDriver`'s constructor, before `JdbcProviderFactory.FromJarPaths`. Covered by
`JdbcUrlTemplateTests.Constructor_WithAPasswordPlaceholderInTheTemplate_ThrowsNotSupported`, no live
connection needed, as this doc predicted.

Open question `jdbc-url-template-and-connection-testing.md` raised and left open — phases 174M-177M
shipped everything else that design covers, this one piece never got picked up. Documented to fix later,
not fixed here.

## What's true today

`JdbcGenericDriver`'s unified connection assembly (`BuildUnifiedJdbcUrlAndProperties`, `JdbcGenericDriver.cs`)
places `host`/`port`/`database`/`username` into `JdbcDriverSpec.UrlTemplate` via `PlaceOrFallback`, but
`password` is handled completely separately and never offered to that function at all:

```csharp
jdbcUrl = PlaceOrFallback(jdbcUrl, "host", host, keys.Host);
if (keys.Port is not null) jdbcUrl = PlaceOrFallback(jdbcUrl, "port", portText, keys.Port);
jdbcUrl = PlaceOrFallback(jdbcUrl, "database", database, keys.Database);
jdbcUrl = PlaceOrFallback(jdbcUrl, "username", username, keys.Username);
if (password is not null) props.setProperty(keys.Password, password);
```

That's deliberate — a credential must never end up in a URL that gets logged, and `PlaceOrFallback`'s
whole mechanism is "does the template mention this token." The gap is what happens when an operator
writes `{password}` into `UrlTemplate` anyway, on the reasonable assumption that it works like the other
four placeholders (there's nothing in the shape of the DSL that would tell them otherwise). Nothing
checks for it. The literal text `{password}` stays in the assembled JDBC URL, unsubstituted, and gets
handed to the real driver — `resultSet`/`connect()` then fails with whatever confusing message a vendor
driver gives for a URL containing a stray `{...}` token, with no indication the real problem was an
operator writing a placeholder this DSL doesn't support.

## Why this is worth a real fix, not just documentation

Two things make this more than an ordinary rough edge:

- **It's silent, not loud.** Every other unsupported-thing in this design fails clearly — a missing
  `UrlTemplate` and `ConnectionString` both absent throws immediately (`"no ConnectionString and no
  UrlTemplate — nothing to build a JDBC URL from"`), an unaccepted URL fails at `acceptsURL`. A `{password}`
  token is the one case that just quietly does nothing, discovered only when the resulting connection
  fails downstream for an unrelated-looking reason.
- **It's credential-adjacent.** An operator who believes `{password}` is a supported placeholder (by
  symmetry with `{host}`/`{port}`/`{database}`/`{username}`) might structure a template around that belief
  — e.g. putting it somewhere a URL-based auth scheme expects a credential — and get a URL that looks
  right but never actually carries one, which is a worse failure mode than an ugly error.

## Suggested fix

Validate eagerly, at construction time, not lazily at connect time. `JdbcGenericDriver`'s constructor
already does one piece of load-bearing validation this early (`JdbcProviderFactory.FromJarPaths(spec.DriverJarPaths,
spec.DriverClass)`, which fails fast if the jar/driver class doesn't load) — the same place fits:

```csharp
public JdbcGenericDriver(JdbcDriverSpec spec)
    : base(spec, spec.ValueBinder ?? new GenericValueBinder(spec.Dialect, new JdbcProviderFactoryHandle()))
{
    if (spec.UrlTemplate?.Contains("{password}") == true)
        throw new NotSupportedException(
            $"'{spec.Id}': UrlTemplate contains a {{password}} placeholder, which is never substituted " +
            "— a credential is always sent as a JDBC property, never placed in the URL. Remove the " +
            "placeholder; the password is added automatically.");
    JdbcProviderFactory.FromJarPaths(spec.DriverJarPaths, spec.DriverClass);
}
```

Firing in the constructor (rather than, say, only at descriptor-load time) covers both a `driver.yaml`
that names one and a directly-constructed `JdbcDriverSpec` (the shape every existing JDBC test uses)
equally — there's no code path that builds a working `JdbcGenericDriver` without going through here.

Trivially testable once built — no live connection needed, just constructing a spec with a `{password}`
`UrlTemplate` and asserting the constructor throws.

## Where this applies

JDBC-only. The ADO.NET side (`GenericDriver`) never had a templating mechanism for password to begin with
— it's always a property there too, but there's no `UrlTemplate` concept on that side for an operator to
mistakenly reach for in the first place.
