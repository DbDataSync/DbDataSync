# `OracleFlashbackReader` is outside `ChangeReaderFirstPassContractTests`' sweep

**Status: open.** Found while merging `main` into the phases-034/035/038/145 branch on 2026-09-17, not
while building phase 148 — so it is written here rather than added to that phase's own retrospective
after the fact. Filed per `architecture/implementation/README.md`'s "Follow-up work gets its own doc."

## What is wrong

`ChangeReaderFirstPassContractTests` enumerates every `IChangeReader` in a **hand-listed** set of driver
assemblies, and requires each one to be either in `Declaring` (with the test that proves its declared
intents) or in `Exempt` (with the reason it has nothing honest to declare).

The list is hand-written on purpose. Its own doc comment says why:

> a sweep of what happens to be loaded would quietly cover fewer readers than it appears to, which is
> the failure mode this whole file exists to prevent.

Phase 148 added `DbDataSync.Drivers.Oracle` with `OracleFlashbackReader` — which implements
`IChangeReader`, `IReadIntentDeclaring` and `IPositionCapturing` — and did not add that assembly to the
list. So the reader is uncovered: it declares its supported intents, and nothing checks that the
declaration is non-empty, `InitialLoad`-free, or backed by a test that proves it.

**The file is currently green while covering one fewer reader than it appears to**, which is precisely
the thing it was built to make impossible.

(Phase 147's MySQL/MariaDB driver is *not* affected. It registers the generic readers rather than any
of its own, and `DbDataSync.Drivers.Generic` is already in the list.)

## What closing it needs

More than one line, which is why this is a doc rather than a drive-by fix in a merge commit:

1. `typeof(Drivers.Oracle.OracleDriver).Assembly` added to `DriverAssemblies` — and a project reference
   from `DbDataSync.Api.Tests` to the Oracle driver if it does not already resolve one, plus possibly an
   un-excluded `Oracle.ManagedDataAccess` package reference, since phase 109h's `ExcludeAssets="runtime"`
   means reflecting over a driver assembly needs its library present (the same gap phase 38 hit for
   Npgsql, and 109h/109i each hit once before that).
2. An entry in `Declaring` naming **the integration test that proves `OracleFlashbackReader`'s declared
   intent set against a real server**. That is the part someone who built phase 148 should choose, not
   someone merging past it: the entry is a claim that a specific test covers a specific behaviour, and
   `EveryDeclaredProofNamesATestThatExists` checks the name resolves but cannot check that it proves the
   right thing.
3. A construction recipe in `CreateUninitialized`, since `SupportedIntents` is a property initialised in
   the class body and an uninitialized instance reads as an empty set.

## How to verify when closed

`ChangeReaderFirstPassContractTests` fails before the change and passes after — and fails again if the
`Declaring` entry is removed. Adding the assembly without the other two steps is what failing looks
like here, which is the test working correctly.
