using Xunit;

// One test at a time in this assembly — see DbDataSync.Drivers.Postgres.Tests/AssemblyInfo.cs's
// identical comment. This suite runs against two live containers (MySQL and MariaDB) and several tests
// create/drop databases and triggers; serializing avoids the same class of flaky contention that file
// exists to document, rather than rediscovering it here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
