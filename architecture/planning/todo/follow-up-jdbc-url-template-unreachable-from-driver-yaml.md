# Follow-up: `UrlTemplate`/`ConnectionStringKeys` are unreachable from any `driver.yaml` today — no schema, and the web editor would lose them if there were

**Status: fixed (2026-09-23), phase 178N.** Both parts implemented as suggested: `JdbcDescriptorYaml`
gained `UrlTemplate`/`ConnectionStringKeys`, `JdbcGenericDriver.FromDescriptor` reads and passes them
(the `null`-not-`GenericConnectionStringKeys()` fallback preserved exactly as this doc specifies), and
`driverYamlAssembly.ts` gained `jdbcExtra` so a hand-authored `urlTemplate`/`connectionStringKeys` block
round-trips through an edit-and-save cycle. Proven end to end against the real Postgres container via
`JdbcDescriptorTests.ADriverYamlsUrlTemplateAndConnectionStringKeys_ReachTheBuiltDriver` (through
`PreviewConnection`, not a live connect) and a web unit test round-tripping the block through an unrelated
field edit. See `phase-178N-jdbc-descriptor-schema-for-url-template-and-keys.md`.

Phase 175M built `JdbcDriverSpec.UrlTemplate`/`ConnectionStringKeys` and the unification logic that uses
them — but checked, not assumed, while scoping this follow-up: **nothing in this repo can actually set
either field from a `driver.yaml`.** The mechanism only exists reachable from direct C# construction
(every `DbDataSync.Drivers.Jdbc.Tests` fixture). This is a bigger gap than "the authoring UI doesn't have
a field for it yet" — the backend descriptor layer doesn't read these fields at all, hand-authored file or
not. Two layers, in order: the schema gap (blocking, real work), then the web editor's own separate
data-loss risk once the schema exists.

## 1. The descriptor schema gap — `JdbcDescriptorYaml` has no `UrlTemplate`/`ConnectionStringKeys` fields, and `FromDescriptor` never passes them

`JdbcDescriptorYaml` (`src/DbDataSync.Drivers.Descriptor/DriverDescriptorYaml.cs`) — the entire schema for
a `driver.yaml`'s `jdbc:` block:

```csharp
public sealed class JdbcDescriptorYaml
{
    public required string DriverClass { get; set; }
    public required List<string> DriverJarPaths { get; set; }
}
```

Two fields. `JdbcGenericDriver.FromDescriptor` (`JdbcGenericDriver.cs:227-250`) builds its `JdbcDriverSpec`
from exactly those two plus the dialect/catalog/capabilities every base already carries — `UrlTemplate`
and `ConnectionStringKeys` are never mentioned, so they stay at their constructor defaults (`null`, always)
for every descriptor-built JDBC driver that has ever existed or ever will, until this changes. Writing
`urlTemplate: "jdbc:postgresql://{host}:{port}/{database}"` into a hand-authored `driver.yaml`'s `jdbc:`
block today does *nothing* — `YamlDotNet` would either ignore the unknown key or (depending on this
project's own deserializer strictness elsewhere) reject the whole file, but either way `FromDescriptor`
never looks for it.

**The ADO.NET side already solved this exact problem — mirror it.** `DriverDescriptorReader.ToSpec`
(`DriverDescriptorReader.cs:61-82`) reads `descriptor.Dialect.ConnectionStringKeys`
(`DescriptorConnectionStringKeysYaml?`, already a real, working YAML field on `DescriptorDialectYaml`) and
builds a real `GenericConnectionStringKeys` from it, falling back to the type's own defaults when absent:

```csharp
var keys = descriptor.Dialect.ConnectionStringKeys is { } k
    ? new GenericConnectionStringKeys(k.Host, k.Port, k.Database, k.Username, k.Password, k.ConnectTimeout, k.IntegratedSecurity)
    : new GenericConnectionStringKeys();
```

**Suggested fix**: add `UrlTemplate` (a plain `string?`) and `ConnectionStringKeys`
(`DescriptorConnectionStringKeysYaml?` — the *same* type the ADO.NET side already uses, not a JDBC-specific
copy, for schema consistency across both bases) to `JdbcDescriptorYaml`, and update
`JdbcGenericDriver.FromDescriptor` to read and pass them the same way `ToSpec` does:

```csharp
var keys = jdbc.ConnectionStringKeys is { } k
    ? new GenericConnectionStringKeys(k.Host, k.Port, k.Database, k.Username, k.Password, k.ConnectTimeout, k.IntegratedSecurity)
    : null; // JdbcGenericDriver.DefaultConnectionStringKeys applies when null — see its own doc comment

return new JdbcGenericDriver(new JdbcDriverSpec(
    descriptor.Id, dialect, catalog, jdbc.DriverClass, jarPaths,
    Readers: ..., Staging: ..., Writers: ...,
    UrlTemplate: jdbc.UrlTemplate,
    ConnectionStringKeys: keys,
    DisplayName: descriptor.DisplayName));
```

Note the one real divergence from the ADO.NET pattern: `ToSpec`'s fallback is `new
GenericConnectionStringKeys()` (that type's own ADO.NET-flavoured defaults), but JDBC's own default lives
on `JdbcGenericDriver.DefaultConnectionStringKeys` (the `host`/`port`/`database`/`user`/`password`
spellings a real JDBC driver reads) — passing `null` through when the YAML omits the block, not a fresh
`GenericConnectionStringKeys()`, is what keeps that the effective default. Getting this backwards would
silently apply the wrong (ADO.NET-shaped) key spellings to every JDBC descriptor that doesn't explicitly
override them.

## 2. Once the schema exists: the web editor round-trips the `jdbc:` block lossily, by design today

`driverYamlAssembly.ts` treats `jdbc` as one of `STRUCTURED_KEYS`
(`driverYamlAssembly.ts:17`) — fully owned by the structured form, nothing from it flows into the raw-YAML
passthrough. `parseDriverYaml` only ever extracts `driverClass`/`driverJarPaths` out of the `jdbc:` block
(`driverYamlAssembly.ts:84-85`); `assembleDriverYaml` always *regenerates* the whole block from exactly
those two values (`driverYamlAssembly.ts:150-155`):

```ts
if (form.base === 'jdbc') {
  lines.push('base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc')
  lines.push('jdbc:')
  lines.push(`  driverClass: ${form.driverClass}`)
  lines.push(`  driverJarPaths: [${form.driverJarPaths.join(', ')}]`)
}
```

Once part 1 above ships, this becomes a real data-loss bug, not just a missing feature: opening an
existing JDBC `driver.yaml` that has a hand-authored `urlTemplate`/`connectionStringKeys` for editing
*any* other field through this form (rename the display name, add a capability) and saving would silently
strip both on write — the loaded values are never read into form state, and the save path never emits
them back out. This is true regardless of whether the UI ever grows a dedicated field for either: the
fix below is a correctness fix, not a feature.

**Suggested fix, cheapest first**: extend `ParsedDriverYaml`/`assembleDriverYaml` to preserve whatever the
`jdbc:` block's own extra lines were, verbatim, round-tripping them the same way `rawBody` already
round-trips dialect/typeMap/metadataQueries — e.g. a `jdbcExtra: string` field capturing every line of the
original `jdbc:` block *except* the `driverClass:`/`driverJarPaths:` ones the structured fields already
own, spliced back in on assemble. This alone closes the data-loss risk with no new UI at all — a driver.yaml
hand-authored with a `urlTemplate` still round-trips through an edit-and-save cycle in the console
unchanged, the same "structured fields + raw passthrough for what isn't" split the rest of this form
already uses for dialect/typeMap.

**Whether `urlTemplate` deserves an actual structured input** (a labeled text field, likely beside "Driver
class") **is a separate, smaller design call, not blocking**: `ConnectionStringKeys` already has no
structured UI for *either* base today — it lives inside the `dialect:` raw block for ADO.NET descriptors
too (`DescriptorDialectYaml.ConnectionStringKeys` isn't a `STRUCTURED_KEYS` member, `Dialect` wasn't ever
split out), so JDBC's own `ConnectionStringKeys` following the identical "raw YAML only" precedent is
consistent, not a regression. `UrlTemplate` is more prominent than that (closer in spirit to `driverClass`
than to a rarely-touched key-spelling override) and might earn a real field later — worth doing only if
operators actually find the raw-YAML round-trip clunky for it, not speculatively here.

## Where this applies

Both pieces are prerequisites for anything resembling "JDBC URL templating, usable from the web console"
— today an operator's only path to using `UrlTemplate` at all is hand-editing a `driver.yaml` file
directly *and* the schema change in part 1 landing first; the console can't help with either half yet.
