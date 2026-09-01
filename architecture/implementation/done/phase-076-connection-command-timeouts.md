# Phase 76 — Configurable connect and command timeouts for source/target connections

**Status**: Complete.
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

---

# Outcome

## What was built

### The two settings

`ConnectionConfig.ConnectTimeoutSeconds` and `CommandTimeoutSeconds`, both `int?`, both on
`ConnectionInput` and through `ConfigRepository.SaveConnection` as well. Null is the default, `0` is
unlimited, positive is seconds — and `0` reaches both providers untranslated, as the plan predicted.
`ConnectionTimeouts` in `DataSync.Core/Sql` holds the two defaults as constants (`30`, `1800`) and the
two resolvers, so "what does unset mean" is answered in one place rather than at each driver.

### Connect timeout, less mechanical than billed

The doc called this "one line in each driver's default-application step". It is one line only for a
connection configured by host and port. A connection using raw `ConnectionString` mode may already
carry `Connect Timeout=N`, and the operator who wrote it meant it — the same argument `ApplyDefaults`
already makes for `TrustServerCertificate` and MARS. So the rule is three-way: the setting wins if set,
the operator's own connection string wins if it names a timeout, and only a string silent on the
subject gets the 30s default.

**The obvious way to ask that question is wrong, and it failed first.**
`SqlConnectionStringBuilder.ShouldSerialize("ConnectTimeout")` and
`NpgsqlConnectionStringBuilder.ContainsKey("Timeout")` both answer `true` for a known key that nobody
set — they describe the builder's schema, not the string's contents. The default therefore never
applied, and Postgres sat at Npgsql's 15s while the test asserted 30. `ConnectionTimeouts.AddressCarriesOwnConnectTimeout`
parses the operator's string with a plain `DbConnectionStringBuilder`, which holds only keys actually
present. It takes the key spellings as a parameter because SqlClient accepts three (`Connect Timeout`,
`Connection Timeout`, `ConnectTimeout`) and checking only one would silently overwrite the other two.

### Command timeout: the chokepoint, and where it got stamped

`DbConnection.CreateTimedCommand()` — `CreateCommand()` plus the timeout — with the value carried on a
`ConditionalWeakTable<DbConnection, StrongBox<int>>` keyed by connection instance, exactly the
mechanism the plan floated.

The stamp goes in **each driver's `CreateConnection`**, not at the two places that call it. That was a
real choice. `driver.CreateConnection` has only two callers in `src/` (`DriverConnectionFactory` and
`RunExecutor`), so an `IDriver` extension wrapping both would have been driver-agnostic and impossible
for a future third driver to forget. It was rejected because the tests — and any other direct caller —
go through `CreateConnection` itself, and a connection built that way would have been unstamped and
silently back on 30 seconds. Putting it in the driver means the connection is correct however it was
obtained. The cost is that a third driver must remember two lines; the audit test below is what makes
that a failing build rather than a silent regression.

### The call-site audit, and what it found beyond the doc

The doc scoped the audit to `DataSync.Drivers.MsSql`, `.Postgres`, `.Generic` and `DataSync.Scripting`.
`grep -rn "CreateCommand()" src/` found four more projects issuing commands against a source or target:
`DataSync.Core/Sql/MsSqlIdentityInsert`, `DataSync.Api/Services` (`ProvisioningService`,
`ScriptTestService`), `DataSync.TaskRunner/RunExecutor`, and `DataSync.Verification/VerificationExecutor`.
All were converted — a provisioning DDL statement or a verification aggregate is exactly the kind of
long query the phase exists to stop cutting off at 30 seconds.

Two files keep `CreateCommand()` deliberately: `ConnectionTimeouts` itself, and
`VerificationResultQuery`, which queries DuckDB over a local parquet file — no `ConnectionConfig`, no
network hop, nothing to resolve a timeout from. `DataSync.State` is untouched, as scoped.

### The UI, which turned out to be a declaration

The doc asked to check before assuming no UI change was needed. It was needed, and it was
five lines rather than a form: the connection screen has been driver-declared since the
`ParameterDescriptor` work, so both settings are declared once in `DriverParameters.ForConnection`
beside addressing and auth, and both drivers render them without knowing they exist. Only the SPA's
`toValues`/`fromValues` needed real edits, because that bag maps named fields and the properties
dictionary and would otherwise have dropped two unrecognized keys on save.

Neither parameter declares a `Default`, unlike `Port`. A default here would be written into every
connection that never thought about it, freezing today's number in config and making a later change to
the default reach nothing. Blank means "whatever the default currently is" — the null the field
documents.

## How it was verified

- `MsSqlTimeoutTests` / `PostgresTimeoutTests` — connect timeout for unset (lands on 30, not the
  providers' 15), `0`, and a positive value, on the actual connection-string builder; plus both
  connection-string-mode cases, the one that defers to the operator and the one where the setting wins.
- The same two files assert **`DbCommand.CommandTimeout` itself** for unset/`0`/`120`, raised through
  `CreateTimedCommand()` — the call every reader and writer now makes. This is the assertion the doc
  insisted on, and the reason it insisted: there is no connection-string key for command timeout, so
  the connection-string layer can be entirely correct while every query still runs at 30 seconds.
- `CommandTimeoutChokepointTests.EverySourceOrTargetCommandGoesThroughCreateTimedCommand` scans `src/`
  for surviving `.CreateCommand()` calls outside the two exempt files. **This is a deviation** — the doc
  asked for a test covering "at least one reader and one writer", and there was no precedent in this
  repo for a test that reads source. It was written anyway because the phase's actual claim is "a future
  new call site gets the right timeout by construction", and no per-class test says anything about the
  call site nobody has written yet. Sixty-odd individual assertions would have cost more and covered
  less.
- Full suite green — `Category!=Integration` **927 passed, 0 failed**; `Category=Integration`
  **201 passed, 0 failed**. `tsc -b` clean, SPA build clean.

  An earlier integration pass showed two failures — `BackfillIntegrationTests.PrimaryAndBackfill_TriggeredConcurrently_BothSucceed`
  and `RunExecutorIntegrationTests.TwoMappingsOnOneSourceTable_WithDifferentWatermarkColumns_KeepTheirOwnPositions`.
  Both passed in isolation and both passed on the clean re-run above. They are contention against the
  shared test containers, not this change; recorded because "it passed the second time" deserves to be
  written down rather than quietly re-run.

## Decisions

- **Stamped in each driver's `CreateConnection`, not in a wrapper around it** — see above. Correctness
  for every caller, including tests, over unforgettability for a hypothetical third driver.
- **The default defers to an operator's own connection string, and the setting beats both.** Not in the
  doc, but the alternative silently overwrites a deliberate `Connect Timeout=5`, and this file already
  had a rule for exactly this situation.
- **`AddressCarriesOwnConnectTimeout` parses rather than asking the typed builder.** Forced by the bug
  above; recorded because both typed builders answer this question confidently and wrongly.
- **A source-scanning audit test**, in place of the per-reader/per-writer tests the doc suggested.
- **No `Default` on either declared parameter**, unlike `Port`.
- **30s connect rather than the providers' 15s.** The doc specified it; worth noting it is a behaviour
  change for every existing connection, not only for ones that set the new field.

## What this phase did not build

- Anything in `DataSync.State` — scoped out, and its `StateDatabase.Command(...)` helper is already the
  chokepoint it would need if it ever wants one.
- A timeout applied to an already-open connection. New connections and new commands only.
- Any per-mapping or per-replication timeout override. The setting lives on the connection, which is
  what the plan asked for; a long-running mapping on a short-timeout connection has no answer today.
- Validation of the values. A negative `CommandTimeoutSeconds` is rejected by the provider at command
  construction rather than at save time, which is a worse place to find out. `ParameterValidation` has
  no numeric range support to hang it on, and adding one is its own change.

## Notes

The blast radius was the interesting part, and it was larger than the doc's four projects — eight, once
the grep was actually run rather than assumed, which is the precedent the plan cited phase 72 for. The
mechanism itself is small; what makes it hold is that `CreateCommand()` is still sitting there on every
`DbConnection`, compiling fine and quietly meaning 30 seconds. That is why the audit is a test and not
a paragraph.
