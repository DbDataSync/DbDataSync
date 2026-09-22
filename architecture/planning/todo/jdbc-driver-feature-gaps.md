# JDBC driver feature gaps — survey, 2026-09-22

Everything found while looking into "multiple jars, IKVM update, write support, and what else is
missing" that turned out to be real but **not** a contained, implementation-ready phase the way those
three were. Those three got their own docs:

- `architecture/implementation/todo/phase-169V-jdbc-multiple-jar-references.md`
- `architecture/implementation/todo/phase-170V-jdbc-ikvm-version-update.md`
- `architecture/implementation/todo/phase-171V-jdbc-connection-testing-completeness.md` (a live bug,
  found along the way — `ServerVersion`/`DataSource` throw `NotImplementedException`, and
  `GenericDriverBase.TestAsync` already calls the former unconditionally on success)
- `architecture/implementation/todo/phase-172V-jdbc-write-support.md`

Everything below is either a design question with more than one reasonable answer, or genuinely bigger
than one phase.

## No URL template — every JDBC connection needs its exact URL typed by hand

Every other driver in this repo addresses by `Host`/`Port`/`Database`, composed into a native connection
string by a per-engine template (`GenericConnectionStringKeys`, or a hand-written builder like
`OracleDriver`'s `builder.DataSource = $"{connection.Host}:{port}/{database}"`). `JdbcGenericDriver`
has none of this — `CreateConnection` requires `ConnectionConfig.ConnectionString` to already carry the
full JDBC URL verbatim (`AddressMode.connectionString`), because `JdbcDriverSpec` has no notion of a URL
template the way `GenericDriverSpec.ConnectionStringKeys` does. `jdbc-driver-support.md`'s own "connection
model" section already named this as "the missing half" — still missing:

```yaml
jdbc:
  driverClass: org.postgresql.Driver
  driverJarPaths: [...]
  # doesn't exist yet:
  urlTemplate: "jdbc:postgresql://{host}:{port}/{database}"
```

An operator configuring a JDBC engine today has to know the vendor's JDBC URL syntax by heart (or find it
in the vendor's docs) and paste the whole thing in, where every other driver just asks for host/port/
database. Worth closing once a second real JDBC vendor descriptor exists to prove the template shape
generalizes past Postgres — the same "prove it before generalizing" reasoning
`follow-up-phase-168-hand-written-jdbc-path-vs-descriptor.md` already applies to the `typeMap`-per-vendor
question.

## No operator-facing UI or CLI support for a JDBC connection at all

Grepped the SPA (`web/`) and `DriverTemplates.cs`: zero mentions of JDBC anywhere in either. Every JDBC
driver built so far (phase 165V onward) is exercised exclusively from `DbDataSync.Drivers.Jdbc.Tests` —
there is no `dbdatasync` CLI template, no web console form, no way for an operator to actually stand one
of these up today short of hand-authoring a `driver.yaml` and a `ConnectionConfig` with a raw JDBC URL in
`ConnectionString`, in JSON, by hand. This is the real "not ready to ship" gap underneath all the others:
even with 169V–172V built, a JDBC engine is only reachable by someone reading this repo's own tests. Not
scoped here — it depends on the URL-template question above (what fields a form would even show) and on
`jars/` vs `libraries/` (below — what a "pick your driver jar" control would point at).

**Partly addressed, 2026-09-22**: `architecture/planning/todo/driver-yaml-authoring-ui.md` designs a
general `driver.yaml`-authoring screen (base, capabilities, library/jar references, metadata queries),
JDBC included — it doesn't resolve the URL-template or `jars/`-vs-`libraries/` questions, both still
named as real dependencies inside that doc, but it's the "later planning doc" `drivers-and-libraries-in-the-web-ui.md`
itself deferred this to.

## `jars/` vs `libraries/` — still the open question `jdbc-driver-support.md` left

`JdbcDriverSpec.DriverJarPath`(s) is a literal filesystem path, unchanged by phase 169V's list support.
`jdbc-driver-support.md`'s own "Where driver artifacts live" section already raised this and left it
open: `libraries/` is NuGet-package-shaped (`library.json`, a `DbProviderFactory` type, an
`AssemblyDependencyResolver`, phase 109j's surface checking) and a `.jar` fits none of that; a sibling
`jars/` root was proposed, never built. This matters more once there's a UI (above) that needs to offer
"which jar" as a real, discoverable choice rather than a path the operator already knows.

## The `.dll`-precompilation alternative — spiked, still not adopted

Covered in full in `jdbc-driver-support.md`'s own "Where driver artifacts live" section (updated
2026-09-22 with this session's probe results): `IkvmReference` genuinely works — a statically-compiled
pgJDBC assembly built and ran a real query against a live Postgres container — but the version-pinning
risk and the multi-release-jar silent-skip behavior are both now *confirmed*, not just asserted from
IKVM's own docs. Conclusion unchanged: `.jar` (runtime `URLClassLoader` loading) stays the default. If
this is ever revisited, it composes with the multiple-jar-references work (169V) — `IkvmReference` also
accepts more than one jar — but nothing here proposes doing that now.

**Revisited, 2026-09-22**: `architecture/planning/todo/jdbc-ikvmreference-compile-button.md` designs
exactly this as an operator-triggered, per-driver action rather than something adopted by default — the
compiled `.dll` becomes an opt-in acceleration a driver can have, not a replacement for the jar path.
Found along the way: `JdbcProviderFactory.FromAssemblyPath` (the loading half of this) has existed,
unused, since phase 165V.

## Retiring the hand-written `JdbcDialect`/`JdbcCatalog` path

Already its own doc: `architecture/planning/todo/follow-up-phase-168-hand-written-jdbc-path-vs-descriptor.md`.
Not duplicated here — flagged only so this survey doesn't look like it missed it.

## No change-tracking strategy comes free (not a gap to close — a permanent characteristic)

Worth restating plainly rather than letting it hide: a JDBC source, with or without 172V's write support,
only ever gets the generic readers (`Watermark`, `BatchReload`, `TriggerAuditReader` if the target schema
supports the trigger/audit shape). Nothing CDC-like exists or can exist generically over an arbitrary JDBC
URL — `jdbc-driver-support.md`'s own "Risks" section already says this. Not actionable as a phase; listed
here so "other major gaps" doesn't imply this one has a fix pending.
