# Configurable connection and command timeouts

**Status: resolved — ready for an implementation phase doc.**

## What's there today, confirmed by reading the code

Nothing is configurable. Neither `ConnectTimeout` nor `CommandTimeout` is set anywhere in `src/` for
source or target connections:

- `MsSqlDriver.ApplyDefaults` (`MsSqlDriver.cs:95-116`) sets `TrustServerCertificate` and
  `MultipleActiveResultSets`, nothing timeout-related. `ConnectTimeout` stays at
  `Microsoft.Data.SqlClient`'s own default (15s).
- `PostgresDriver`'s builder (`PostgresDriver.cs:56-66`) sets `Host`/`Database`/`Port`, nothing
  timeout-related. Npgsql's own default applies.
- No `DbCommand.CommandTimeout` assignment exists anywhere in `src/` — every reader/writer/staging/
  provisioning query runs at whichever provider's default command timeout applies (30s for both
  `Microsoft.Data.SqlClient` and Npgsql).
- The one partial escape hatch: `ConnectionConfig.ConnectionString` (raw pass-through mode) lets an
  operator embed `Connect Timeout=N` themselves, since both connection-string builders accept it. There
  is no equivalent for command timeout — it is a runtime property on `DbCommand`, not a connection-string
  key, for either provider.

Both providers already treat `0` as "no limit" on their own timeout properties — `SqlConnection`'s
`Connect Timeout=0` and `SqlCommand.CommandTimeout = 0` are documented as infinite; Npgsql's
`Timeout=0`/`CommandTimeout=0` behave the same way. So "a value, or unlimited" doesn't need an invented
sentinel — `0` already means unlimited to both drivers this tool supports, and passing a config value of
`0` straight through requires no translation.

## Scope

Source and target connections only — `ConnectionConfig`, used by every driver's connection-string
builder and every command issued against a source or target. **Not** the state store (`DataSync.State`'s
own `StateDatabase`/`database.Command(...)` calls) — that's an internal, single-writer store this API
process owns outright, a different risk profile from a network hop to someone else's database, and out
of scope for this request.

## Design

Add two nullable fields to `ConnectionConfig` (`src/DataSync.Core/Config/ConnectionConfig.cs`):

- `ConnectTimeoutSeconds` — `null` means "use the default" (30s); `0` means unlimited; a positive value
  is seconds.
- `CommandTimeoutSeconds` — same shape; default 30 minutes (1800s).

**Connect timeout** is mechanical: one line in each driver's `ApplyDefaults`-equivalent, setting
`ConnectTimeout`/`Timeout` on the connection-string builder from the resolved value (config value if
set, else the 30s default). Same shape phase changes have used before for per-driver defaults.

**Command timeout** is the real work. There is no central "create a command" chokepoint today — every
reader, writer, staging provider, and provisioner calls `connection.CreateCommand()` (or
`DbCommand`-returning helpers) directly, scattered across `DataSync.Drivers.MsSql`,
`DataSync.Drivers.Postgres`, `DataSync.Drivers.Generic`, and `DataSync.Scripting`. Setting
`CommandTimeout` at every call site individually is the literal fix but a large, error-prone blast
radius with no way to guarantee a future new call site remembers to do it.

**Recommended approach**: a single extension method (e.g. `DbConnection.CreateTimedCommand()` or
similar) that wraps `CreateCommand()` and stamps `CommandTimeout` from a value carried on the connection
— e.g. a `ConditionalWeakTable<DbConnection, int>` keyed by the connection instance and set once when
the connection is opened (wherever `IDriver.OpenAsync`/equivalent already resolves `ConnectionConfig`),
or a value threaded alongside the connection the way `SourceTableRef`/`ConnectionName` already are.
Whichever mechanism, the implementation phase should audit every `CreateCommand()` call site first (a
`grep -rn "CreateCommand()" src/` across the driver projects) to confirm the chosen mechanism actually
covers all of them rather than assuming — this repo's own established discipline (see phase 72's command-
timeout-adjacent "audit rather than an assumption" precedent).

## What this phase should not do

- Touch the state store's own command/connection handling — explicitly out of scope, above.
- Invent a new "unlimited" representation — `0` is what both drivers already mean by it.
- Apply a timeout retroactively to an already-open connection — this only affects new connections/
  commands going forward.

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-076-connection-command-timeouts.md`.
