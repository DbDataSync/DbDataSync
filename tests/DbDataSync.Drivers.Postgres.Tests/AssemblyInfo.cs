using Xunit;

// One test at a time in this assembly.
//
// These tests work against a real SQL Server (and Postgres) container, and several of them create and
// drop databases, enable Change Tracking, and enable CDC — server-scoped operations that take locks in
// master and msdb. xUnit runs test classes in parallel by default, so they deadlocked each other:
// error 1205, "chosen as the deadlock victim", in a different test on every run. A suite that fails
// one run in two has stopped telling anyone anything.
//
// The cost is wall-clock. The alternative — retrying 1205 everywhere — would be scattering a
// workaround for a test-harness problem across the fixtures, and would still leave two tests fighting
// over the same server and calling it a pass.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
