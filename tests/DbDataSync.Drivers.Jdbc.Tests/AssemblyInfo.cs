using Xunit;

// One test at a time in this assembly — see DbDataSync.Drivers.Postgres.Tests/AssemblyInfo.cs's
// identical comment. This project's tests create/drop a database on the same real Postgres container.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
