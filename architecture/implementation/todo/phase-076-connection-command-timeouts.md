# Phase 76 — Configurable connect and command timeouts for source/target connections

**Status**: Not started.
**Plan reference**: `architecture/planning/done/source-target-connection-timeouts.md`

## The gap

No source or target connection has a configurable timeout today. `ConnectTimeout` isn't set by either
driver's connection-string builder (`MsSqlDriver.ApplyDefaults`, `PostgresDriver`'s builder), so it
sits at each provider's own default (15s). `CommandTimeout` is never set on any `DbCommand` anywhere in
`src/`, so every query runs at each provider's default command timeout (30s for both
`Microsoft.Data.SqlClient` and Npgsql). Neither is reachable through DataSync's own config.

## What to build

### `ConnectionConfig`

Add two nullable fields (`src/DataSync.Core/Config/ConnectionConfig.cs`):

- `ConnectTimeoutSeconds` — `null` = default (30s), `0` = unlimited, positive = seconds.
- `CommandTimeoutSeconds` — `null` = default (1800s / 30 minutes), `0` = unlimited, positive = seconds.

Both providers already treat `0` as unlimited on their native properties (`Connect Timeout=0` and
`SqlCommand.CommandTimeout = 0` for SqlClient; the Npgsql equivalents behave the same) — pass the
resolved value straight through, no sentinel translation needed.

### Connect timeout — mechanical

One line in each driver's default-application step (`MsSqlDriver.ApplyDefaults`, and the equivalent spot
in `PostgresDriver`), setting `ConnectTimeout`/`Timeout` on the connection-string builder from the
resolved value (config value if set, else 30).

### Command timeout — the real work

There is no existing "create a command" chokepoint. Start by auditing every `DbCommand`-creation call
site across the driver projects (`grep -rn "CreateCommand()" src/` scoped to
`DataSync.Drivers.MsSql`, `DataSync.Drivers.Postgres`, `DataSync.Drivers.Generic`,
`DataSync.Scripting` — not `DataSync.State`, which is out of scope) to know the actual blast radius
before choosing a mechanism.

Build a single chokepoint rather than touching every call site's timeout assignment by hand — something
like a `CreateTimedCommand()` extension on `DbConnection` that every reader/writer/staging/provisioner
routes through instead of raw `CreateCommand()`, with the resolved `CommandTimeoutSeconds` carried
alongside the connection from wherever `ConnectionConfig` is first resolved (driver `OpenAsync` or
equivalent). The goal: a future new call site that uses the chokepoint gets the right timeout by
construction, rather than by remembering to set it.

## What this phase should not do

- Touch `DataSync.State`'s own command/connection handling — a different risk profile (internal,
  single-writer store) and explicitly out of scope per the planning doc.
- Invent a different "unlimited" representation than `0` — both drivers already mean that.
- Apply retroactively to an already-open connection.

## How to verify

- A test asserting `ConnectTimeoutSeconds = 0` produces a connection string with `Connect Timeout=0` (or
  the Postgres equivalent), and a positive value produces that exact value.
- A test asserting a configured `CommandTimeoutSeconds` reaches an actual `DbCommand.CommandTimeout` for
  at least one reader and one writer, not just the connection-string layer — the mechanism needs to prove
  it reaches commands, not just connections.
- A test for the unset (`null`) case landing on the stated defaults (30s connect, 1800s command).
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean if the
  connection-editing UI needs new fields for these (check `ConnectionsController`/the SPA's connection
  form before assuming no UI change is needed — an operator needs a way to set these without hand-editing
  YAML).
