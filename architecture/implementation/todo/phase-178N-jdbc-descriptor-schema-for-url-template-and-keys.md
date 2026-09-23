# Phase 178N: make `UrlTemplate`/`ConnectionStringKeys` reachable from a `driver.yaml`, and close the `{password}` gap

**Status: done (2026-09-23), with one correction from phase 181N.** This doc's own §1 said to reuse
`DescriptorConnectionStringKeysYaml` "verbatim... not a JDBC-specific copy" — that turned out to be wrong:
that type's non-nullable ADO.NET-flavoured C# defaults leak into a *partial* JDBC override. Replaced with
a dedicated `JdbcConnectionStringKeysYaml` (all-nullable fields). See phase 181N's own "Applied" section
for the full story and the regression test. First of five phases for the JDBC/driver-editing UI round
requested 2026-09-23 —
prerequisite plumbing for 179N (the URL template editor) and 181N (validate/echo). Implements
[`follow-up-jdbc-url-template-unreachable-from-driver-yaml.md`](../../planning/todo/follow-up-jdbc-url-template-unreachable-from-driver-yaml.md)
in full (both its parts) and
[`follow-up-jdbc-url-template-password-placeholder-validation.md`](../../planning/todo/follow-up-jdbc-url-template-password-placeholder-validation.md) —
folded in here rather than its own phase because it's a few lines in the same constructor this phase
already has open, and the URL template editor (179N) needs a real error to demonstrate/report against.

## 1. `JdbcDescriptorYaml` gains the two fields, `FromDescriptor` wires them

`JdbcDescriptorYaml` (`src/DbDataSync.Drivers.Descriptor/DriverDescriptorYaml.cs:53-59`) today:

```csharp
public sealed class JdbcDescriptorYaml
{
    public required string DriverClass { get; set; }
    public required List<string> DriverJarPaths { get; set; }
}
```

Add, reusing `DescriptorConnectionStringKeysYaml` verbatim — same type the ADO.NET side already uses
(`DescriptorDialectYaml.ConnectionStringKeys`), for schema consistency, not a JDBC-specific copy:

```csharp
public string? UrlTemplate { get; set; }
public DescriptorConnectionStringKeysYaml? ConnectionStringKeys { get; set; }
```

`JdbcGenericDriver.FromDescriptor` (`src/DbDataSync.Drivers.Jdbc/JdbcGenericDriver.cs`, the
`FromDescriptor` static near the bottom) currently never mentions either. Mirror
`DriverDescriptorReader.ToSpec`'s own mapping (`DriverDescriptorReader.cs:68-70`), with the one
documented divergence: fall back to `null` (not a fresh `GenericConnectionStringKeys()`), so
`JdbcGenericDriver.DefaultConnectionStringKeys` (the `host`/`port`/`database`/`user`/`password`
spellings, not ADO.NET's) stays the effective default when the YAML omits the block:

```csharp
var keys = jdbc.ConnectionStringKeys is { } k
    ? new GenericConnectionStringKeys(k.Host, k.Port, k.Database, k.Username, k.Password, k.ConnectTimeout, k.IntegratedSecurity)
    : null;

return new JdbcGenericDriver(new JdbcDriverSpec(
    descriptor.Id, dialect, catalog, jdbc.DriverClass, jarPaths,
    Readers: descriptor.Capabilities.Readers, Staging: descriptor.Capabilities.Staging, Writers: descriptor.Capabilities.Writers,
    UrlTemplate: jdbc.UrlTemplate,
    ConnectionStringKeys: keys,
    DisplayName: descriptor.DisplayName));
```

## 2. Block `{password}` in a template, eagerly, in the constructor

`JdbcGenericDriver`'s constructor (`JdbcGenericDriver.cs`, right after the `base(...)` call) already
does one piece of load-bearing eager validation (`JdbcProviderFactory.FromJarPaths(...)`, which fails
fast on a bad jar/class). Add the check before it, so both a `driver.yaml` and a directly-constructed
spec (every existing test fixture) are covered — there's no path to a working `JdbcGenericDriver` that
skips this constructor:

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

Trivially testable in `DbDataSync.Drivers.Jdbc.Tests`: construct a spec with a `{password}` template,
assert the constructor throws `NotSupportedException` — no live connection needed.

## 3. The web editor's round-trip: stop silently dropping a hand-authored `jdbc:` block

`driverYamlAssembly.ts` treats `jdbc` as fully owned by the structured form
(`STRUCTURED_KEYS`, `driverYamlAssembly.ts:17`): `parseDriverYaml` only ever pulls `driverClass`/
`driverJarPaths` out of it (`driverYamlAssembly.ts:84-85`), and `assembleDriverYaml` always regenerates
the whole block from exactly those two (`driverYamlAssembly.ts:150-155`). Once part 1 lands, opening any
existing JDBC `driver.yaml` that has a hand-authored `urlTemplate`/`connectionStringKeys`, editing an
unrelated field, and saving silently strips both.

Cheapest real fix, matching the shape `rawBody` already uses for dialect/typeMap/metadataQueries: a
`jdbcExtra: string` field on `ParsedDriverYaml`/the save-time `form` shape, capturing every line of the
original `jdbc:` block **except** the `driverClass:`/`driverJarPaths:` lines the structured fields
already own (a filtered re-join of `jdbcBlock`'s own lines, skipping any line matching
`/^\s*(driverClass|driverJarPaths):/`), spliced back into the emitted `jdbc:` block on assemble, after
the two structured lines. This alone closes the data-loss risk with **no new UI** — 179N's structured
fields (below) are additive on top of this, not a replacement for it, since an operator can still
hand-write something 179N's inputs don't model (e.g. a third custom connection-string key) and expect it
to survive an edit of an unrelated field.

Note this phase deliberately does *not* add a `driver-edit-raw-body`-style single text field for
`urlTemplate`/`connectionStringKeys` — that's 179N's job, a real structured input. This phase only makes
the round-trip lossless for whatever's already on disk.

## How to verify when closed

- A `driver.yaml` with a hand-authored `jdbc.urlTemplate`/`connectionStringKeys` block, loaded through
  `DriverDescriptorReader.BuildDriver`, produces a `JdbcGenericDriver` whose `Spec.UrlTemplate`/
  `ConnectionStringKeys` are the YAML's values, not `null`.
- A `JdbcDriverSpec` (or a descriptor) with `urlTemplate: "jdbc:...{password}..."` throws
  `NotSupportedException` at construction, not at connect time.
- Loading that same file into `DriverEditPage`, changing `displayName` only, and saving reproduces the
  original `jdbc:` block's `urlTemplate`/`connectionStringKeys` lines unchanged.
