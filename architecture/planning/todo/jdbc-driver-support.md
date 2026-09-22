# JDBC source support — IKVM, reader first

Support JDBC-only engines by running a real JDBC driver inside DbDataSync, as a **replication source**.
The immediate driver for this is a **proprietary JDBC driver already working under IKVM** — so this is
not a survey, it is writing down a route that is already partly proven and the differences against the
alternative, in case we ever have to revisit.

**Decided going in:**

- **IKVM, not a JVM.** The proprietary driver works under IKVM today. The real-JVM route
  (`JdbcWrapper.Ado.Data`) is documented below as the alternative and deliberately not pursued.
- **Import the source, don't reference the package.** `ClrKernel.Database.Provider.Jdbc` is the author's
  own code; it comes into this repo so it can be changed to suit a generic replication source rather
  than a notebook query tool.
- **Reader first.** No JDBC writer is required. A writer stays possible and the shapes should stay
  symmetrical where that costs nothing, but it is explicitly out of scope here and can ship separately.

## What reader-only changes, and what it doesn't

Reader-only is a much smaller problem than it first looks — but not for the reason it seems, and the
correction matters because the first draft of this doc got it wrong.

**Transactions are a writer concern, and drop out of scope.** Every generic *writer* opens one —
`DeleteInsertWriter`, `KeyReconcileDeleteWriter`, `KeyReconcileScd2CloseWriter`, `Scd2Writer` — so a
provider whose `BeginDbTransaction` throws could never write. But the generic **readers** use none:
`WatermarkReader`, `BatchReloadReader` and `TriggerAuditReader` contain **zero** `BeginTransaction` calls.
The only reader that takes one is `MsSqlChangeTrackingReader`, for SQL Server snapshot isolation
specifically, which has nothing to do with JDBC. So `JdbcConnection.BeginDbTransaction`'s
`NotImplementedException` is simply not on the reader path.

**The blocker moves, it does not disappear.** `ClrKernel.Database.Provider.Jdbc`'s `JdbcCommand` has:

```csharp
protected override DbParameterCollection DbParameterCollection => throw new NotImplementedException();
protected override DbTransaction         DbTransaction         => throw new NotImplementedException();
public    override void                  Prepare()             => throw new NotImplementedException();
```

and `WatermarkReader` — the most likely first reader for a JDBC source — does exactly this:

```csharp
cmd.Parameters.Add(binder.CreateParameter(...));
```

So **parameter binding is the thing that has to be built for a reader**, not transactions.
`BatchReloadReader` and `TriggerAuditReader` show no parameter use today, so a narrow first cut could
avoid it — but the watermark reader is the one that makes a JDBC source useful for incremental work, and
it needs parameters.

Two things travel with that:

- **JDBC placeholders are positional `?`**, not named `@p`. `planning/done/additional-database-drivers.md`
  already flagged this in its parameter-placeholder row. `SqlDialect` has to express it, and
  `GenericValueBinder` (which binds through `SqlDialect.ToCanonicalType` and a generic `DbType`) has to
  produce something a `java.sql.PreparedStatement` can take.
- `JdbcCommand` today executes through `java.sql.Statement`; parameters mean `PreparedStatement`, which
  is a real change to that class rather than filling in one override.

**Also still needed for a reader**: `ServerVersion` and `DataSource` currently throw
(`DatabaseMetaData.getDatabaseProductVersion()` / `getURL()` — cheap), and connection *testing* (phase 19)
will want them.

## The connection model — `ConnectionConfig` → JDBC URL + properties

This is the question `planning/done/additional-database-drivers.md` left open:

> **`ConnectionConfig` does not fit ODBC or JDBC.** It models Host/Port/Database. ODBC wants a DSN or a
> full connection string; JDBC wants a URL.

The imported provider already answers half of it. `JdbcConnectionStringBuilder` reserves two keys and
turns **everything else into `java.util.Properties`**:

```csharp
private const string _jdbcDriver = "JdbcDriver";   // driver class
private const string _jdbcUrl    = "JdbcUrl";      // the JDBC URL
// every other key -> result.setProperty(key, value)
```

That maps onto DbDataSync's existing shape with no new operator-facing concept:

| DbDataSync | JDBC |
| --- | --- |
| `ConnectionConfig.Properties` (already has a UI) | `java.util.Properties`, 1:1 |
| `UserId` / `Password` (resolved via `SecretStore`) | the `user` / `password` properties — **never** URL query parameters |
| `Host` / `Port` / `Database` | composed into the URL by a per-engine template |
| `AddressMode = connectionString` (exists today) | the operator supplies the whole JDBC URL verbatim |
| `ConnectTimeoutSeconds` / `CommandTimeoutSeconds` | no universal JDBC property — per-engine property, and `Statement.setQueryTimeout` |

The missing half is the **URL template**: the direct analogue of `GenericConnectionStringKeys`, something
like `jdbc:postgresql://{host}:{port}/{database}` declared per engine alongside its driver class, so "how
this engine is addressed" stays a declared property of the engine rather than logic in a driver. The
`connectionString` addressing mode is the escape hatch for URLs no template can express — it already
exists in `DriverParameters`, so only the label needs to read "JDBC URL" for these drivers.

Keeping `user`/`password` as properties rather than URL query parameters is deliberate: it keeps the
credential out of anything that gets logged, echoed in a connection test, or committed. Note the
real-JVM route's own README example does the opposite
(`jdbc:mysql://127.0.0.1/sakila?user=root&password=12345`), which is one more small reason to prefer this
shape.

## Where driver artifacts live

Both forms must work:

- **`.jar`** — IKVM converts bytecode to CIL on the fly, so a vendor jar is usable as shipped. This is
  the default and the more robust path.
- **IKVM-compiled `.dll`** — via `ikvmc` / `IkvmReference` / `MavenReference`. Faster startup and no
  runtime translation, but **pinned to the IKVM version that produced it**: IKVM does not guarantee API
  stability between statically compiled assemblies and `IKVM.Java`/`IKVM.Runtime` across versions. So
  `.dll` is an optimisation, `.jar` is the default.

  **Spiked 2026-09-22, confirmed rather than assumed.** A throwaway probe project
  (`<IkvmReference Include="postgresql-42.7.13.jar" />`, IKVM 8.11.2, `net10.0`) built clean — 0 errors,
  23 warnings — and the resulting statically-compiled `org.postgresql.Driver` connected to a live Postgres
  container and ran a real query:
  ```
  Driver major version: 42, minor: 7
  Connected. Catalog: postgres
  SELECT 1 -> 1
  ```
  Build cost was ~8.6s for this one jar. Warnings broke down as: ~18 `IKVM0100` "class not found" for
  optional dependencies pgJDBC references but doesn't need (OSGi, JNA, Waffle/Windows-SSPI,
  checkerframework annotations — none of which this repo's usage touches), ~3 `IKVM0141` annotation-load
  warnings (same checkerframework classes), and 2 `IKVM0101` warnings — pgJDBC's jar is a **multi-release
  jar**; the two skipped classes are a `META-INF/versions/11/...LazyCleanerImpl` override IKVM's Java
  8-only compiler can't parse (class format `55.0`), so IKVM silently falls back to the base (Java 8)
  class. Didn't break this probe, but it's a real, silent divergence from what a real JVM would pick for
  a multi-release jar, worth knowing before trusting `.dll` compilation of a jar that leans on that
  mechanism for anything load-bearing.

  This confirms both things this section already asserted as caveats rather than measurements — the
  version-pinning risk is real (not just theoretical; see IKVM issue #519 below) and multi-release jars
  are a genuine, silent gap, not a hypothetical one — without changing the conclusion: `.jar` stays the
  default, `.dll` stays a possible future optimisation, not adopted here. See
  `architecture/planning/todo/jdbc-driver-feature-gaps.md` for where this could go next.

`libraries/` is NuGet-package-shaped (`library.json`, a `DbProviderFactory` type, an
`AssemblyDependencyResolver` per directory, plus phase 109j's surface checking). A `.jar` is none of
those. A sibling **`jars/`** root, enumerated the way `drivers/` already is, keeps two genuinely
different artifact kinds apart. The IKVM-compiled `.dll` case is the interesting one: at that point the
artifact *is* a managed assembly, so `libraries/` may genuinely be right for it — worth deciding
deliberately rather than by accident.

## Testing — a Java 8 driver for an engine we already test

Rather than stand up a new engine, point a JDBC source at one of the engines the suite already covers
(**MSSQL, Postgres, MySQL, MariaDB, Oracle**) and run it as a **generic reader source**. All five have
published Java 8-compatible JDBC drivers (`mssql-jdbc` jre8 builds, pgJDBC, Connector/J, MariaDB
Connector/J 2.x, `ojdbc8`) — worth confirming the exact build before picking, but none is a blocker.

This is a better test than a new engine for a reason worth stating: **the same engine is already
replicating through its native ADO.NET driver**, so a JDBC read can be compared against a known-good
result on identical data rather than against nothing. It isolates the JDBC layer from engine behaviour.

Postgres is the obvious first pick — pgJDBC is pure Java with no native components and a permissive
licence, and the Postgres path through the generic pipeline is already exercised.

## The alternative, documented rather than taken: a real JVM

`JdbcWrapper.Ado.Data` — recorded here so a revisit doesn't start from scratch.

- **3.9.0, published 2024-11-25, ~4.2K downloads**, `net6.0`/`netstandard2.1`. A stated **fork of
  `JDBC.NET.Data`** (chequer-io).
- Depends on `Google.Protobuf`, `Grpc.Net.Client`, **`J2NET`**; upstream setup requires a platform-specific
  **"J2NET Runtime"** package, i.e. a *bundled* Java runtime rather than an operator-installed JRE.
- **Out-of-process.** The gRPC + protobuf dependencies indicate a Java-side server the .NET side talks
  to, rather than in-process JNI. The upstream README does not state the mechanism outright — this is
  strongly indicated, not read.
- Its builder takes the jar directly:
  `DriverPath = "mysql-connector-java-8.0.21.jar"`, `DriverClass`, `JdbcUrl`.

**The one thing that would make us revisit it: IKVM is Java SE 8 only.** A vendor driver that requires
Java 11 or 17 will not load under IKVM at all, and "use an older driver build" is not always available.
That is a hard exclusion rather than a performance trade — it is the specific circumstance under which
the JVM route stops being the worse option and becomes the only one.

Against it: a child process and a gRPC hop per connection inside a product that already supervises
worker processes, a second runtime to ship and patch, and a small single-maintainer fork of an upstream
that is not busy.

**`IKVM.Jdbc.Data` was also considered and rejected.** The packages are real (ikvmnet, MIT), but the
README states `JdbcConnection` must be handed a `java.sql.Connection` instance because the JDBC SPI does
not survive IKVM's class-loader hierarchy — so driver-by-name resolution, the thing the package is named
for, does not work. We would write that part regardless, which is an argument for importing code that
already does it.

## Risks

- **Java SE 8 ceiling** — see above. The single biggest constraint, and the one that would force a route
  change rather than a fix.
- **`IkvmConfiguration` resolves `IKVM.Home` by walking the NuGet cache** and its own comment defaults the
  architecture folder to `win-x64`. Per the author this is a workaround for interactive-notebook loading,
  not an inherent constraint — but DbDataSync ships a RID-agnostic nupkg and Linux containers with no
  NuGet cache at runtime, so the imported copy needs its own resolution, and it must work on Linux. This
  is one of the concrete reasons to import rather than reference.
- **Type round-trip is unproven.** `GenericValueBinder` binds through `SqlDialect.ToCanonicalType` and a
  generic `DbType`; values come back through `java.sql.Types` and `JdbcDataReader`. How `DATE`,
  `TIMESTAMP`, `NUMERIC` and `CLOB` survive that is unchecked in both directions, and change-data
  correctness depends on it. The "same engine, two drivers" test above is what makes this checkable.
- **Metadata browsing shape is still open** — the same question `additional-database-drivers.md` left for
  ODBC/JDBC. `DatabaseMetaData` has catalogs *and* schemas and vendors disagree about which maps to what,
  while the SPA's cascading pickers assume `ListDatabases` means something.
- **No change-tracking strategy comes free.** A JDBC source gets the generic readers — watermark,
  batch-reload, trigger-audit. Nothing CDC-like.

## Open questions

1. **Is a JDBC engine a `GenericDriverSpec`, or its own `JdbcDriverSpec`?** A spec carrying a URL
   template, driver class and jar/dll reference looks like the honest shape, reusing `GenericDriver`'s
   readers underneath.
2. **`jars/` or `libraries/`** for each artifact form — see above.
3. **Import boundary.** Which of `ClrKernel.Database.Provider.Jdbc`'s files come across, and does the
   parameter/`PreparedStatement` work get upstreamed back? The author can have it either way, so this is
   a release-cadence question, not a technical one.
4. **How much writer symmetry to preserve now.** A writer is out of scope, but the connection model, the
   spec shape and the artifact layout are shared. Worth not designing them in a way that a writer would
   have to undo.

## What to do first

**Read one table from an already-tested engine over JDBC, through the generic pipeline, with a `.jar`.**
Postgres via pgJDBC, using `BatchReloadReader` (no parameters) to get a first read working, then
`WatermarkReader` to force the `PreparedStatement`/parameter work. Compare the rows against the same
table read through Npgsql.

That one spike answers the Java 8 question, the Linux `IKVM.Home` question, the type round-trip and the
parameter design at once — and every one of those is currently an assumption.

## Sources

- [ClrKernel/ClrKernel](https://github.com/ClrKernel/ClrKernel) — `src/ClrKernel.Database.Provider.Jdbc`
- [ikvmnet/ikvm](https://github.com/ikvmnet/ikvm) — Java SE 8, .NET 6+, jar-at-runtime and AOT
- [ikvmnet/ikvm-jdbc](https://github.com/ikvmnet/ikvm-jdbc) — `IKVM.Jdbc` / `IKVM.Jdbc.Data`
- [JdbcWrapper.Ado.Data on NuGet](https://www.nuget.org/packages/JdbcWrapper.Ado.Data)
- [chequer-io/JDBC.NET](https://github.com/chequer-io/JDBC.NET) — the upstream it forks
- [ikvmnet/ikvm#519](https://github.com/ikvmnet/ikvm/issues/519) — a real report of an `IkvmReference`-compiled
  assembly breaking across an IKVM version bump, cited for the version-pinning risk above
