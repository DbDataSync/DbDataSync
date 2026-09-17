# `MySqlConnector` is no longer the "restored, never compiled against" canary

**Status: open, and it is the only thing failing `dotnet-integration` on `main`.** Found while rebasing
the phases-034/035/038/145 branch onto `main` on 2026-09-17. Filed per
`architecture/implementation/README.md`'s "Follow-up work gets its own doc, not a paragraph."

## What is wrong

`DbDataSync.Libraries.Tests.LibraryLoadTests` exists to prove one thing, in its own words:

> Proves the whole point: `MySqlConnector` is referenced by **no** `.csproj` in this solution, yet
> `library install` restores it, `LibraryRegistry` loads it, and a factory it hands out opens a real
> connection to a MySQL container the build never compiled against.

Phase 147 added `DbDataSync.Drivers.MySql`, a real compiled driver project with a
`<PackageReference Include="MySqlConnector" …>`. So the premise is false, and
`MySqlConnector_IsReferencedByNoCsprojInTheSolution` fails — on `main`, on every branch, every run.

Its two siblings still pass, and that is the trap: `Install_RestoresTheClosure_AndWritesTheManifest`
and `GetFactory_OpensARealConnectionToMySql_WithNoCompileTimeReference` install into a throwaway repo
root and load through `LibraryRegistry`, which works regardless. **They pass while their stated claim —
"with no compile-time reference" — is no longer true of this engine.** A green test asserting something
false is worse than the red one beside it.

## Why this is a decision, not a one-line fix

Deleting the failing assertion would leave two tests whose names and doc comment claim a property the
suite no longer demonstrates anywhere. The real question is what the canary should be now, and that is
phase 147's author's call:

1. **Move the canary to an engine that genuinely is not compiled against.** After phases 147 and 148 the
   built-ins are MsSql, Postgres, MySql, Oracle and DuckDb, so it would have to be something else
   entirely — and whatever is chosen needs a container in `dotnet-integration` for the
   `GetFactory_OpensARealConnection…` half to keep meaning anything, which is a CI change on top.
2. **Reframe the suite as "the loader works", dropping the never-compiled claim.** Cheaper and honest,
   and it gives up the property phase 109c built these tests to demonstrate: that a driver's library can
   be absent at build time and present at run time. That property is the whole descriptor-driver story,
   so giving up its only end-to-end proof is a real loss, not a tidy-up.

**The recommendation is (1)**, because (2) retires a proof rather than fixing it — but it is a
recommendation, not a decision, and it costs a container either way.

## How to verify when closed

- `dotnet-integration` is green on `main` with no other change.
- Whatever the suite claims in its doc comment is true of the engine it names — checked by reading the
  comment against the `.csproj` list, which is what this test does and should keep doing.
