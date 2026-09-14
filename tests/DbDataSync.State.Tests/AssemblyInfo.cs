using Xunit;

// One test at a time in this assembly.
//
// Phase 109g put MsSqlStateDialect/PostgresStateDialect on an injected LibraryRegistry instead of a
// stateless static singleton, resolved into StateDialectRegistry.Default — a single, process-wide,
// mutable slot per engine (StateDialectRegistry.RegisterLibraryBackedEngines just replaces whichever
// dialect instance was there). Two tests in this assembly that legitimately want *different*
// LibraryRegistry instances (one with the library installed, one without, say) running concurrently
// could each register over the other between one test's RegisterLibraryBackedEngines call and its own
// StateDialect.For(engine) lookup a few lines later — a real race, not a hypothetical one, now that a
// per-call LibraryRegistry backs what used to be a stateless singleton. xUnit runs test classes in
// parallel by default; every other integration-heavy assembly in this repo (Api.Tests, Cli.Tests,
// MsSql.Tests, Postgres.Tests, TaskRunner.Tests) already disables it for its own reasons, and this one
// now needs it for this one.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
