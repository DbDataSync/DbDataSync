# Follow-up: retire the hand-written JDBC path in favor of the descriptor, or keep both?

Phase 168V (`architecture/implementation/todo/phase-168V-generic-driver-base-and-jdbc-descriptor.md`)
built a second way to stand up a JDBC-backed engine — `JdbcGenericDriver.FromDescriptor`, driven by a
`driver.yaml` naming it as `base` — alongside the first: hand-constructing a `JdbcGenericDriver` directly
with a `JdbcDriverSpec` (what phase 165V's original `JdbcReaderParityTests`/`JdbcCatalogTests` still do,
via `JdbcDialect.Instance`/`JdbcCatalog.Instance`, phase 165V's own compiled, Postgres-scoped classes).

Both work today, side by side, and nothing forces a choice between them.

## The question

Should a real, shipped JDBC engine always go through a `driver.yaml` descriptor (one per vendor, its own
`typeMap`, per phase 168V's actual point), with the hand-written `JdbcDialect`/`JdbcCatalog` path retired
once that's proven out — or is there a legitimate reason to keep both, the way `GenericDriver` (descriptor
path) and a hand-written driver like `PostgresDriver` already coexist for ADO.NET engines?

Worth noting the ADO.NET precedent cuts both ways: `PostgresDriver` stays hand-written specifically
*because* Postgres gets bespoke value-binding (`PostgresValueBinding`, `NpgsqlDbType`) and provisioning
(`PostgresProvisioner`) that a descriptor can't express — `DescriptorDialect.RenderColumnType` throws
outright, by design (see its own doc comment). Phase 165V's own `JdbcDriver`/`JdbcGenericDriver` has none
of that (reuses `GenericValueBinder`, has no provisioner at all, per its own doc comment on why) — so the
ADO.NET precedent may actually argue *for* retiring the hand-written JDBC path rather than for keeping it,
unlike Postgres/MsSql/MySql/Oracle's own compiled drivers.

## What to decide, not resolved here

- Does `JdbcDialect`/`JdbcCatalog` (`DbDataSync.Core.Sql.JdbcDialect`, `DbDataSync.Drivers.Jdbc.JdbcCatalog`
  used directly rather than through a descriptor) do anything a `driver.yaml` + `typeMap` genuinely
  cannot express, the way Postgres's own bespoke binder/provisioner does?
- If not, is retiring it worth doing now, or only once a second real JDBC vendor descriptor
  (`oracle-via-jdbc.driver.yaml` or similar) has actually proven the `typeMap`-per-vendor shape holds up
  outside Postgres — the same "prove it before generalizing" discipline phase 165V's own spike already
  used?
- `JdbcReaderParityTests`/`JdbcCatalogTests` construct a `JdbcGenericDriver` by hand today. If the
  hand-written path is retired, do these move to constructing one through `DriverDescriptorReader.BuildDriver`
  instead (closer to how an operator would actually reach it), or stay as a lower-level unit check
  regardless of which path ships?

## Resolved, 2026-09-23 — there was never a real dual-path choice to make

Checked directly: `JdbcDialect.Instance`/`JdbcCatalog.Instance` (hand-constructed) are used **only** by
`DbDataSync.Drivers.Jdbc.Tests` — every one of `JdbcReaderParityTests`, `JdbcCatalogTests`,
`JdbcConnectionTests`, `JdbcWriterParityTests`, `JdbcUrlTemplateTests`, `JdbcMultipleJarsTests`,
`JdbcChangeDatabaseTests`, `JdbcSqlExceptionTests` — as deliberate low-level unit scaffolding. No
production registration path (`KnownDrivers`, `DriverLoader`, the driver-authoring UI phases 178N-182N)
ever hand-constructs a `JdbcGenericDriver`; the only way an operator can actually stand one up is a
`driver.yaml` with `base: JdbcGenericDriver`, through the descriptor path phase 168V built. `JdbcCatalog`
itself isn't even a competing mechanism — it's the descriptor path's own default catalog
(`JdbcGenericDriver.FromDescriptor`'s `@default: JdbcCatalog.Instance`), reused, not duplicated.

So this doc's own question answers itself: the hand-written path was never a second, competing way to
*ship* a JDBC engine — it's test scaffolding that happens to construct the same types the descriptor path
also uses internally. Nothing to retire (there's no operator-facing path to retire), and the tests stay
exactly as they are (a lower-level check, deliberately below the descriptor layer, per their own original
design) — this doc's last open question, resolved in its "stay" branch.
