# Phase 174M — Rename `Jdbc.Imported` to `Jdbc.Ado`

**Status**: Not started — design only.
**Plan reference**: none — a naming cleanup raised directly in conversation while designing
`architecture/planning/todo/jdbc-url-template-and-connection-testing.md`, split out as its own phase
because it touches every file phase 175M also needs to touch and should land first, cleanly, on its own.

## Why

`src/DbDataSync.Drivers.Jdbc/Imported/` holds `JdbcConnection`, `JdbcCommand`, `JdbcConnectionStringBuilder`,
`JdbcDataReader`, `JdbcParameter`, `JdbcProviderFactory`, `JdbcTransaction` — the ADO.NET `DbConnection`/
`DbCommand`/`DbDataReader` wrapper around `java.sql.*`, adapted from `ClrKernel.Database.Provider.Jdbc`
(each file's own header comment already says so — see `Imported/JdbcConnection.cs:5-10` for the pattern
every file in the folder repeats). "Imported" names *how the code arrived*, not *what it is or does* — a
fact about this repo's git history, not the code's role. It reads as a staging area or a to-be-cleaned-up
pile, which this code isn't; it's the permanent ADO.NET provider layer for JDBC, exactly as load-bearing as
`DbDataSync.Drivers.Postgres`'s use of Npgsql's own types.

Everything at the top level of `DbDataSync.Drivers.Jdbc` (`JdbcDriverSpec.cs`, `JdbcGenericDriver.cs`,
`JdbcCatalog.cs`) is DbDataSync-authored driver-integration code — the descriptor/spec/catalog layer that
makes a JDBC engine speak `IDriver`. The folder split already exists and already draws the right line; only
its name is wrong.

## What changes

- `src/DbDataSync.Drivers.Jdbc/Imported/` → `src/DbDataSync.Drivers.Jdbc/Ado/` (folder rename, `git mv`,
  so history follows each file).
- `namespace DbDataSync.Drivers.Jdbc.Imported;` → `namespace DbDataSync.Drivers.Jdbc.Ado;` in every file
  that currently declares it (`JdbcConnection.cs`, `JdbcCommand.cs`, `JdbcConnectionStringBuilder.cs`,
  `JdbcDataReader.cs`, `JdbcParameter.cs`, `JdbcProviderFactory.cs`, `JdbcTransaction.cs`).
- `using DbDataSync.Drivers.Jdbc.Imported;` → `using DbDataSync.Drivers.Jdbc.Ado;` everywhere it's
  referenced — at minimum `JdbcGenericDriver.cs` and `JdbcCatalog.cs`, and any test project
  (`DbDataSync.Drivers.Jdbc.Tests`) or metadataProvider-script-facing code that names `JdbcConnection`/
  `JavaSqlConnection`/`JavaSqlDriver` directly (phase 167V made those two public specifically so a script
  could hold a direct reference — grep for the fully qualified old namespace before assuming the search is
  done).
- No behavior change, no type renames, no public API renamed beyond the namespace itself — this is a pure
  namespace/folder move. The "adapted from ClrKernel..." provenance comments at the top of each file stay;
  they're history, not the folder name, and the plan is not to erase where the code came from, only to stop
  using that fact as its address.

## What this does not do

- Does not touch anything designed in phase 175M/176M/177M — this is deliberately sequenced first so those
  phases are written against the final namespace, not a moving target.
- Does not rename `JdbcProviderFactory`, `JdbcConnectionStringBuilder`, or any other type — "mechanical
  term" applies to the *namespace*, which described provenance; the type names already describe function
  fine (`JdbcConnection` **is** a connection, `JdbcProviderFactory` **is** a provider factory) and aren't in
  scope.
- Does not touch `DbDataSync.Drivers.Jdbc`'s own top-level namespace (`JdbcDriverSpec`/`JdbcGenericDriver`/
  `JdbcCatalog`) — already correctly named for what it is.

## How to verify

- Full solution build succeeds with zero references to the old `Imported` namespace anywhere (grep the
  whole repo, not just `src/`, since a metadataProvider script fixture or a doc-comment code sample could
  still name it).
- `DbDataSync.Drivers.Jdbc.Tests` (and any integration suite touching JDBC) passes unchanged — this phase
  should produce a zero-behavior-change diff, so the exact same tests that passed before should pass after
  with no new or updated assertions needed.
