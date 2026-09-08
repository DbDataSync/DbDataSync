# Phase 109b — a public GenericDriver

**Status**: Done.
**Plan reference**: `architecture/planning/todo/nuget-loaded-drivers.md`. Depended on 109a (string
driver id, `DriverIds`). Nothing depends on 109b until 109d; it de-risked that phase by landing the
reusable core on its own with its own tests.

## What this built

`src/DbDataSync.Drivers.Generic/GenericDriver.cs` — a `public sealed class GenericDriver : IDriver,
IConnectionTester, IDialectProvider, ITableCatalogProvider`, constructed from a new
`GenericDriverSpec` record (`GenericDriverSpec.cs`, same file's neighbor): driver id, `SqlDialect`,
`DbProviderFactory`, an `InformationSchemaQueries` catalog, which of `GenericDriverKinds`' Kinds to
register (as `IReadOnlyList<string>`, matching how every other Kind in the codebase is named — no new
enum), a `GenericConnectionStringKeys` record for the per-engine connection-string spellings, a
default database, an optional default port, and an optional `ISegmentValueBinder`.
`GenericDriverSpec.InformationSchema(id, dialect, factory, defaultDatabase, defaultPort)` is the
one-call common case — every generic Kind, `information_schema` catalog, default (SqlClient-shaped)
connection-string keys — with record `with`-expressions for anything that needs to differ (Postgres's
Npgsql test spec overrides `Username` and `ConnectTimeout`).

### The value-binder fallback (an open question, resolved)

`ISegmentValueBinder` types a segment bound to its column's real type so the engine seeks an index
instead of converting the column — every existing implementation (`MsSqlValueBinding`,
`PostgresValueBinding`) does this through the provider's own type enum (`SqlDbType`, `NpgsqlDbType`),
which is exactly the "no common ancestor" `ISegmentValueBinder`'s own doc comment names. `GenericDriver`
needed a fallback for when a spec supplies none, and the plan doc flagged this as worth prototyping
here rather than assuming an answer.

**Built**: `GenericValueBinder(SqlDialect, DbProviderFactory)`. It routes a column's native type through
`SqlDialect.ToCanonicalType` — a table every dialect already implements for provisioning — to a
`CanonicalTypeKind`, maps that to a BCL `System.Data.DbType`, and creates the parameter through
`DbProviderFactory.CreateParameter()` rather than a provider-typed constructor. This works for any
engine with a real `DbProviderFactory` and loses real precision (no `NpgsqlDbType.Money` vs
`.Numeric` distinction, no `tinyint(1)`-is-boolean branching) — which is exactly the signal, stated
directly in the plan doc, for when an engine needs a compiled driver instead of a descriptor.

### `ListDatabasesAsync` (not named in the plan doc at all)

`GenericDriverSpec.Catalog` is typed as the concrete `InformationSchemaQueries`, not the narrower
`ITableCatalog` interface, because `IDriver.ListTablesAsync` needs `InformationSchemaQueries`'s own
`ListTablesAsync` — a method the pipeline-facing `ITableCatalog` interface doesn't declare (Oracle has
no `information_schema`; a catalog strategy that does gets it for free, per that class's own doc
comment). But **`information_schema` itself cannot answer "what databases exist on this server"** — it
describes the current database, not the server — and the plan doc's "Catalog methods delegate to
spec.Catalog" sentence doesn't mention this method at all. Resolved with
`DbConnection.GetSchema("Databases")` — the ADO.NET-standard schema collection every
`DbProviderFactory`-based provider implements — reading the first column of whatever it returns rather
than naming a column (Npgsql's is `database_name`; the shape is not part of the documented contract).
Verified end-to-end against the real Postgres container, not just built and hoped.

### Where it lives

`src/DbDataSync.Drivers.Generic/`, per the doc's own reasoning: it belongs with the components it
composes, and `Generic` already references `Abstractions` and `Core`. The `Generic` →
`Scripting.Abstractions` reference the doc flagged as a maybe-blocker turned out not to matter — nobody
needed `GenericDriver` isolated from that dependency yet, so it stayed where it naturally sits rather
than being split into a new leaf project pre-emptively.

`PostgresDriver` was **not** rewired to `new GenericDriver(...)` — the doc explicitly allowed this as
an optional follow-up, and rewiring a driver already in production for no behavioral gain (nothing
consumes `GenericDriver` yet) would have been risk with no corresponding benefit this phase.

## What this phase does not build

- The descriptor (`driver.yaml`) or its deserialiser — 109d.
- The provider layer — 109c. This phase's tests construct `GenericDriver` with `NpgsqlFactory.Instance`
  directly; `Npgsql` is a hard `PackageReference` on the new test project.
- Rewiring `MsSqlDriver`/`PostgresDriver` to use `GenericDriver` (allowed as a follow-up, not required).
- `IProvisioner` on `GenericDriver` — provisioning stays out of the descriptor's initial scope, exactly
  as planned.

## How it was verified

- `dotnet build DbDataSync.slnx` clean; existing suite green (nothing adopted `GenericDriver`, so
  nothing moved) — full non-integration and `Category=Integration` runs both green, including every
  driver, API, TaskRunner and State test.
- **New — `tests/DbDataSync.Drivers.Generic.Tests/GenericDriverTests.cs`** (`Category=Integration`,
  against the existing `postgres` container, via a new `Npgsql` package reference and its own
  `GenericDriverTestDatabase` fixture — no `ProjectReference` to `DbDataSync.Drivers.Postgres`, which is
  the point):
  - `CreateConnection` assembled from host/port/database/credential opens a real connection;
  - `TestAsync` succeeds against it;
  - `ListDatabasesAsync` finds the fixture database (added beyond the phase doc's own test list, since
    the method turned out to need a real design decision — see above — and an untested `IDriver` member
    is exactly the kind of thing that goes unnoticed until an operator hits it);
  - `ListTablesAsync` / `ListColumnsAsync` return the same shape `PostgresDriver` does for a seeded
    table;
  - a watermark read after an insert yields the row and advances the position, and a second read past
    that watermark yields only the newly-inserted row;
  - a `BatchReload` + `DeleteInsert` round-trip lands both source rows on a target table.
- The assertion that matters, per the phase doc: this is byte-identical behaviour to `PostgresDriver`'s
  generic Kinds (same `WatermarkReader`/`BatchReloadReader`/`BatchInsertStagingProvider`/
  `DeleteInsertWriter` classes, just constructed by `GenericDriver` instead of by hand), proving the
  extraction lost nothing.

## Decisions

- **`GenericDriverSpec.Catalog` is `InformationSchemaQueries`, not `ITableCatalog`.** The narrower
  interface (`GetColumnsAsync` only) can't serve `ListTablesAsync`; a different catalog strategy
  (Oracle's `ALL_*`, ODBC's `GetSchema`) is out of scope until a later phase needs one, at which point
  `GenericDriverSpec` gains a second catalog-strategy shape rather than this one growing branches.
- **A traditional constructor, not a primary constructor**, for `GenericDriver` itself — the class needs
  to resolve the value-binder fallback once and reuse it across `Readers` and `Writers`'
  field-initializer-style construction; a primary constructor's implicit parameter capture would have
  either recomputed the fallback binder per list (wasteful, and briefly was a bug caught before this was
  committed — a `static readonly` field closing over an instance-level primary-constructor parameter,
  which doesn't compile-correctly per-instance) or needed an extra field just to hold it. An explicit
  constructor body is one line clearer.
- **Kind names, not a new enum**, for which generic strategies a spec registers — matches
  `GenericDriverKinds`' own existing string constants and every other Kind in the codebase; an enum
  here would be the only place they weren't already-loose strings.

## Open questions — resolved or deferred

- **`GenericDriverSpec` as a record vs. a builder**: built as a record with a static factory
  (`InformationSchema`), as leaned. `with`-expressions handle every override this phase needed.
- **Whether `IValueBinding` can fall back to a generic `DbType` mapping**: resolved — yes, via
  `GenericValueBinder` as described above. This directly de-risks 109d, which needs exactly this
  fallback for a descriptor driver that supplies no per-type binding of its own.
- **Splitting `GenericDriver` + spec into a leaf `DbDataSync.Drivers.Generic.Core`** (raised in case the
  `Scripting.Abstractions` reference mattered to a plugin author): still open, still not a blocker —
  nothing in this phase needed it.
