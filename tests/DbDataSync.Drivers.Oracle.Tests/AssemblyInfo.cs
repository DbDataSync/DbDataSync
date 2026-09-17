using Xunit;

// One test at a time in this assembly — see DbDataSync.Drivers.Postgres.Tests/AssemblyInfo.cs's
// identical comment. This suite runs against one shared Oracle instance and its one shared app schema
// (Oracle has no cheap per-test-class database the way Postgres/MySQL do — see OracleTestDatabase's own
// doc comment), so serializing matters even more here than for the other engines' suites.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
