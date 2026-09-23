using System.Data;
using System.Data.Common;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using LibGit2Sharp;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 105 — one provisioning plan for the whole replication.
/// <para>
/// Everything here is a unit test in the ordinary sense: no real database, no <c>TestApiFactory</c>
/// HTTP pipeline (which fails in this sandbox under Kestrel/Negotiate — a pre-existing, unrelated
/// issue). <see cref="ProvisioningService"/> is constructed directly with a fake
/// <see cref="IConnectionFactory"/> and fake <see cref="IDriver"/>/<see cref="IProvisioner"/>
/// implementations, exactly the seam phase 105 §5 asked for so the "one connection per endpoint" claim
/// could be pinned with a counting fake rather than merely asserted.
/// </para>
/// <para>
/// The "API" behaviours the phase doc's verification bar asks for — subset apply, no-longer-needed ids,
/// stop-at-first-failure, the warn-never-block prerequisite rule — are exercised by calling
/// <see cref="ProvisioningService.ApplyReplicationPlanAsync"/> directly rather than through
/// <c>ReplicationProvisioningController</c> over HTTP, for the same environment reason. The controller
/// itself is three lines of pass-through (see <c>ReplicationProvisioningController.cs</c>) and is
/// covered by <c>dotnet build</c> succeeding against it plus the routing conventions
/// <c>ProvisioningRoutingTests</c> already established for its sibling endpoint.
/// </para>
/// </summary>
public sealed class ReplicationProvisioningPlanTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private const string Replication = "r";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-repl-provisioning-").FullName;
    private readonly ConfigRepository _config;

    public ReplicationProvisioningPlanTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));

        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = Replication,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, Author);
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    // ---- Test scaffolding -----------------------------------------------------------------------

    private void SaveMapping(
        string name, string srcConnection, string srcDatabase, string srcTable,
        string tgtConnection, string tgtDatabase, string tgtTable, ProvisioningConfig? provisioning = null) =>
        _config.SaveTableMapping(Replication, new TableMappingConfig
        {
            Name = name,
            Sources = [new SourceTableSpec { ConnectionName = srcConnection, Database = srcDatabase, Table = srcTable }],
            Targets = [new TableSpec { ConnectionName = tgtConnection, Database = tgtDatabase, Table = tgtTable }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
            Provisioning = provisioning ?? new ProvisioningConfig(),
        }, Author);

    private static ProvisioningPlan EnableChangeCapturePlan(ProvisioningRequest request, string database) =>
        new(ProvisioningActions.EnableSourceChangeCapture, ProvisioningState.Missing,
        [
            new ProvisioningStep(
                $"Enable Change Tracking on database [{database}]",
                $"ALTER DATABASE [{database}] SET CHANGE_TRACKING = ON;", null, ProvisioningStepScope.Database),
            new ProvisioningStep(
                $"Enable Change Tracking on table [{request.Table.Table}]",
                $"ALTER TABLE [dbo].[{request.Table.Table}] ENABLE CHANGE_TRACKING;", null, ProvisioningStepScope.Table),
        ],
        []);

    private static ProvisioningPlan SatisfiedPlan(string action) =>
        new(action, ProvisioningState.Satisfied, [], []);

    private static ProvisioningPlan CreateTablePlan(ProvisioningRequest request) =>
        request.Action == ProvisioningActions.CreateTargetTable
            ? new ProvisioningPlan(ProvisioningActions.CreateTargetTable, ProvisioningState.Missing,
                [
                    new ProvisioningStep(
                        $"Create table [dbo].[{request.Table.Table}]",
                        $"CREATE TABLE [dbo].[{request.Table.Table}] (Id INT NOT NULL PRIMARY KEY);", null,
                        ProvisioningStepScope.Table),
                ],
                [])
            : SatisfiedPlan(ProvisioningActions.AlterTargetTable);

    private static ProvisioningPlan UnsupportedPlan(string action, string reason) =>
        new(action, ProvisioningState.Unsupported, [], [reason]);

    /// <summary>A source driver whose Change Tracking plan always includes the database-scope step,
    /// following phase 25's own design — see the class doc's dedup test.</summary>
    private static FakeProvisioningDriver ChangeTrackingSourceDriver(string database) =>
        new(request => EnableChangeCapturePlan(request, database));

    private static FakeProvisioningDriver CreateTableTargetDriver() => new(CreateTablePlan);

    private static FakeProvisioningDriver SatisfiedTargetDriver() =>
        new(request => SatisfiedPlan(request.Action));

    // ---- 1. Dedup: N mappings on one source database collapse to one ALTER DATABASE step ---------

    [Fact]
    public async Task NMappingsSharingOneSourceDatabase_CollapseToOneDatabaseStep_NamingEveryMapping()
    {
        var sourceDriver = ChangeTrackingSourceDriver("Sales");
        var factory = new FakeConnectionFactory(connectionName => connectionName == "src" ? sourceDriver : SatisfiedTargetDriver());

        SaveMapping("a", "src", "Sales", "Orders", "tgt", "DW", "Orders");
        SaveMapping("b", "src", "Sales", "Customers", "tgt", "DW", "Customers");
        SaveMapping("c", "src", "Sales", "Invoices", "tgt", "DW", "Invoices");

        var service = CreateService(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);

        var sourceGroup = Assert.Single(plan.Groups, g => g.Side == ProvisioningEndpointSide.Source);
        var databaseSteps = sourceGroup.Steps.Where(s => s.Scope == ProvisioningStepScope.Database).ToList();

        var databaseStep = Assert.Single(databaseSteps);
        Assert.Equal("ALTER DATABASE [Sales] SET CHANGE_TRACKING = ON;", databaseStep.CommandText);
        Assert.Equal(["a", "b", "c"], databaseStep.ContributingMappings);

        // Three distinct tables, so three distinct table-scope steps — dedup collapses the identical
        // statement, not the mapping.
        Assert.Equal(3, sourceGroup.Steps.Count(s => s.Scope == ProvisioningStepScope.Table));
    }

    [Fact]
    public async Task DatabaseScopeSteps_PrecedeTableScopeSteps_InEveryGroupAfterTheMerge()
    {
        var sourceDriver = ChangeTrackingSourceDriver("Sales");
        var factory = new FakeConnectionFactory(connectionName => connectionName == "src" ? sourceDriver : SatisfiedTargetDriver());

        SaveMapping("a", "src", "Sales", "Orders", "tgt", "DW", "Orders");
        SaveMapping("b", "src", "Sales", "Customers", "tgt", "DW", "Customers");

        var service = CreateService(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);

        var sourceGroup = Assert.Single(plan.Groups, g => g.Side == ProvisioningEndpointSide.Source);
        var scopes = sourceGroup.Steps.Select(s => s.Scope).ToList();

        Assert.Equal(ProvisioningStepScope.Database, scopes[0]);
        Assert.All(scopes.Skip(1), s => Assert.Equal(ProvisioningStepScope.Table, s));
    }

    // ---- 2. Grouping across more than one target connection ----------------------------------------

    [Fact]
    public async Task Steps_LandInTheCorrectGroup_WhenMappingsSpanMoreThanOneTargetConnection()
    {
        var sourceDriver = SatisfiedTargetDriver();
        var factory = new FakeConnectionFactory(
            connectionName => connectionName == "src" ? sourceDriver : CreateTableTargetDriver());

        SaveMapping("a", "src", "Sales", "Orders", "tgt1", "DW1", "Orders");
        SaveMapping("b", "src", "Sales", "Customers", "tgt2", "DW2", "Customers");

        var service = CreateService(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);

        var targetGroups = plan.Groups.Where(g => g.Side == ProvisioningEndpointSide.Target).ToList();
        Assert.Equal(2, targetGroups.Count);

        var group1 = Assert.Single(targetGroups, g => g.ConnectionName == "tgt1" && g.Database == "DW1");
        Assert.Equal(["a"], Assert.Single(group1.Steps).ContributingMappings);

        var group2 = Assert.Single(targetGroups, g => g.ConnectionName == "tgt2" && g.Database == "DW2");
        Assert.Equal(["b"], Assert.Single(group2.Steps).ContributingMappings);
    }

    // ---- 3. Unsupported mappings are excluded, with reasons, and contribute nothing ----------------

    [Fact]
    public async Task AnUnsupportedMapping_AppearsExcludedWithItsReason_AndContributesNoSteps()
    {
        var sourceDriver = SatisfiedTargetDriver();
        var unsupportedTarget = new FakeProvisioningDriver(
            request => UnsupportedPlan(request.Action, "'NoKey' has no primary key to key an upsert on."));
        var factory = new FakeConnectionFactory(
            connectionName => connectionName == "src" ? sourceDriver : unsupportedTarget);

        SaveMapping("nokey", "src", "Sales", "NoKey", "tgt", "DW", "NoKey");

        var service = CreateService(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);

        Assert.Empty(plan.Groups);
        var excluded = Assert.Single(plan.Excluded);
        Assert.Equal("nokey", excluded.MappingName);
        Assert.Equal(ProvisioningEndpointSide.Target, excluded.Side);
        Assert.Contains("no primary key", excluded.Reason);
    }

    [Fact]
    public async Task AMappingThatIsNotOneToOne_IsExcludedRatherThanThrowing()
    {
        _config.SaveTableMapping(Replication, new TableMappingConfig
        {
            Name = "twoTargets",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "Sales", Table = "Orders" }],
            Targets =
            [
                new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders1" },
                new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders2" },
            ],
        }, Author);

        var factory = new FakeConnectionFactory(_ => SatisfiedTargetDriver());
        var service = CreateService(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);

        Assert.Empty(plan.Groups);
        var excluded = Assert.Single(plan.Excluded);
        Assert.Equal("twoTargets", excluded.MappingName);
        Assert.Null(excluded.Side);
        Assert.Contains("1:1", excluded.Reason);
    }

    // ---- 4. One connection per endpoint, not per mapping -------------------------------------------

    [Fact]
    public async Task GetReplicationPlanAsync_OpensOneConnectionPerDistinctEndpoint_NotPerMapping()
    {
        var sourceDriver = SatisfiedTargetDriver();
        var targetDriver = CreateTableTargetDriver();
        var factory = new FakeConnectionFactory(
            connectionName => connectionName == "src" ? sourceDriver : targetDriver);

        // Three mappings: two share both endpoints exactly, the third shares the source but has its
        // own target. Naively (source + target per mapping) that is 6 opens; deduplicated by
        // (connectionName, database) it is 2 distinct sources... no — 1 distinct source pair and 2
        // distinct target pairs = 3.
        SaveMapping("a", "src", "Sales", "Orders", "tgt", "DW", "Orders");
        SaveMapping("b", "src", "Sales", "Customers", "tgt", "DW", "Customers");
        SaveMapping("c", "src", "Sales", "Invoices", "tgt2", "DW2", "Invoices");

        var service = CreateService(factory);
        await service.GetReplicationPlanAsync(Replication, CancellationToken.None);

        Assert.Equal(3, factory.OpenCount);
    }

    // ---- 5. Step ids: stable across replans, and content-derived --------------------------------

    [Fact]
    public void ProvisioningStepId_IsStableForTheSameInputs_AndChangesWithTheStatement()
    {
        var first = ProvisioningStepId.Compute("src", "Sales", ProvisioningStepScope.Database, "ALTER DATABASE [Sales] SET CHANGE_TRACKING = ON;");
        var same = ProvisioningStepId.Compute("src", "Sales", ProvisioningStepScope.Database, "ALTER DATABASE [Sales] SET CHANGE_TRACKING = ON;");
        var differentStatement = ProvisioningStepId.Compute("src", "Sales", ProvisioningStepScope.Database, "ALTER DATABASE [Sales] SET CHANGE_TRACKING = OFF;");
        var differentDatabase = ProvisioningStepId.Compute("src", "Warehouse", ProvisioningStepScope.Database, "ALTER DATABASE [Sales] SET CHANGE_TRACKING = ON;");
        var differentScope = ProvisioningStepId.Compute("src", "Sales", ProvisioningStepScope.Table, "ALTER DATABASE [Sales] SET CHANGE_TRACKING = ON;");

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentStatement);
        Assert.NotEqual(first, differentDatabase);
        Assert.NotEqual(first, differentScope);
    }

    [Fact]
    public async Task TwoPlansOfUnchangedState_ProduceTheSameStepIds()
    {
        var sourceDriver = ChangeTrackingSourceDriver("Sales");
        var factory = new FakeConnectionFactory(connectionName => connectionName == "src" ? sourceDriver : SatisfiedTargetDriver());
        SaveMapping("a", "src", "Sales", "Orders", "tgt", "DW", "Orders");

        var service = CreateService(factory);
        var first = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);
        var second = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);

        var firstIds = first.Groups.SelectMany(g => g.Steps).Select(s => s.Id).OrderBy(id => id).ToList();
        var secondIds = second.Groups.SelectMany(g => g.Steps).Select(s => s.Id).OrderBy(id => id).ToList();
        Assert.Equal(firstIds, secondIds);
    }

    // ---- 6. AUTOMATIC labelling -------------------------------------------------------------------

    [Fact]
    public async Task ATargetStepTheAutoFlagWouldApplyAnyway_IsLabelledAutomatic_AndStillTicked()
    {
        var sourceDriver = SatisfiedTargetDriver();
        var targetDriver = CreateTableTargetDriver();
        var factory = new FakeConnectionFactory(connectionName => connectionName == "src" ? sourceDriver : targetDriver);

        SaveMapping("auto", "src", "Sales", "Orders", "tgt", "DW", "Orders",
            provisioning: new ProvisioningConfig { CreateTargetTableIfMissing = true });
        SaveMapping("manual", "src", "Sales", "Customers", "tgt", "DW", "Customers");

        var service = CreateService(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);

        var group = Assert.Single(plan.Groups, g => g.Side == ProvisioningEndpointSide.Target);
        var autoStep = Assert.Single(group.Steps, s => s.ContributingMappings.Contains("auto"));
        var manualStep = Assert.Single(group.Steps, s => s.ContributingMappings.Contains("manual"));

        Assert.True(autoStep.Automatic);
        Assert.False(manualStep.Automatic);
    }

    // ---- Apply: subset, no-longer-needed, stop-at-first-failure, prerequisite warning -------------

    [Fact]
    public async Task Apply_WithASubsetSelected_RunsExactlyThoseStatements()
    {
        var sourceDriver = SatisfiedTargetDriver();
        var targetDriver = CreateTableTargetDriver();
        var recordingConnection = new FakeDbConnection();
        var factory = new FakeConnectionFactory(
            connectionName => connectionName == "src" ? sourceDriver : targetDriver,
            connectionName => connectionName == "tgt" ? recordingConnection : new FakeDbConnection());

        SaveMapping("a", "src", "Sales", "Orders", "tgt", "DW", "Orders");
        SaveMapping("b", "src", "Sales", "Customers", "tgt", "DW", "Customers");

        var service = CreateService(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);
        var group = Assert.Single(plan.Groups, g => g.Side == ProvisioningEndpointSide.Target);
        var ordersStep = Assert.Single(group.Steps, s => s.CommandText.Contains("Orders"));
        var customersStep = Assert.Single(group.Steps, s => s.CommandText.Contains("Customers"));

        var result = await service.ApplyReplicationPlanAsync(Replication, [ordersStep.Id], CancellationToken.None);

        var applied = Assert.Single(result.Steps);
        Assert.Equal(ordersStep.Id, applied.Id);
        Assert.Equal(ProvisioningStepOutcome.Applied, applied.Outcome);
        Assert.Equal([ordersStep.CommandText], recordingConnection.Executed);
        Assert.DoesNotContain(customersStep.CommandText, recordingConnection.Executed);
    }

    [Fact]
    public async Task Apply_WithAnIdAbsentFromTheFreshPlan_ReportsNoLongerNeeded_NotAFailure()
    {
        var factory = new FakeConnectionFactory(_ => SatisfiedTargetDriver());
        SaveMapping("a", "src", "Sales", "Orders", "tgt", "DW", "Orders");

        var service = CreateService(factory);
        var result = await service.ApplyReplicationPlanAsync(Replication, ["not-a-real-step-id"], CancellationToken.None);

        var entry = Assert.Single(result.Steps);
        Assert.Equal("not-a-real-step-id", entry.Id);
        Assert.Equal(ProvisioningStepOutcome.NoLongerNeeded, entry.Outcome);
        Assert.Null(entry.Error);
    }

    [Fact]
    public async Task Apply_StopsAtTheFirstFailure_AndReportsTheRestAsNotAttempted()
    {
        var sourceDriver = SatisfiedTargetDriver();
        var targetDriver = CreateTableTargetDriver();
        var failingConnection = new FakeDbConnection
        {
            OnExecute = commandText =>
            {
                if (commandText.Contains("Orders"))
                    throw new FakeDbException("permission denied");
            },
        };
        var factory = new FakeConnectionFactory(
            connectionName => connectionName == "src" ? sourceDriver : targetDriver,
            connectionName => connectionName == "tgt" ? failingConnection : new FakeDbConnection());

        // "a" sorts before "b", so its Orders table-scope step is planned — and therefore applied —
        // first; both land in the same group, both Table-scope, so first-appearance order decides.
        SaveMapping("a", "src", "Sales", "Orders", "tgt", "DW", "Orders");
        SaveMapping("b", "src", "Sales", "Customers", "tgt", "DW", "Customers");

        var service = CreateService(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);
        var group = Assert.Single(plan.Groups, g => g.Side == ProvisioningEndpointSide.Target);
        var ordersStep = Assert.Single(group.Steps, s => s.CommandText.Contains("Orders"));
        var customersStep = Assert.Single(group.Steps, s => s.CommandText.Contains("Customers"));

        var result = await service.ApplyReplicationPlanAsync(
            Replication, [ordersStep.Id, customersStep.Id], CancellationToken.None);

        var ordersResult = Assert.Single(result.Steps, r => r.Id == ordersStep.Id);
        Assert.Equal(ProvisioningStepOutcome.Failed, ordersResult.Outcome);
        Assert.Contains("permission denied", ordersResult.Error);

        var customersResult = Assert.Single(result.Steps, r => r.Id == customersStep.Id);
        Assert.Equal(ProvisioningStepOutcome.NotAttempted, customersResult.Outcome);
    }

    [Fact]
    public async Task Apply_ATargetTableStep_CachesTheMappingsTargetColumns_AndMarksItsRenamesApplied()
    {
        // The gap this pins: ApplyReplicationPlanAsync ran the DDL but never called the two side
        // effects the per-mapping Setup card's own ApplyAsync already does after creating a target
        // table — MarkRenamesApplied and CacheTargetColumnsAsync (phase 94/97). Unlike the other tests
        // in this file, this one uses a *working* IColumnCatalog and a real CurrentUser, so those two
        // side effects are actually observable rather than silently swallowed.
        var sourceDriver = SatisfiedTargetDriver();
        var targetDriver = CreateTableTargetDriver();
        var factory = new FakeConnectionFactory(
            connectionName => connectionName == "src" ? sourceDriver : targetDriver);

        _config.SaveTableMapping(Replication, new TableMappingConfig
        {
            Name = "a",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "Sales", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = "Orders" }],
            ColumnMappings =
            [
                new ColumnMapping
                {
                    SourceColumn = "Id", TargetColumn = "Id",
                    Renames = [new RenameStep { From = "OldId", To = "Id", Applied = false }],
                },
            ],
            Provisioning = new ProvisioningConfig(),
        }, Author);

        var service = CreateServiceWithSideEffects(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);
        var step = Assert.Single(
            plan.Groups.Single(g => g.Side == ProvisioningEndpointSide.Target).Steps,
            s => s.Scope == ProvisioningStepScope.Table);

        var result = await service.ApplyReplicationPlanAsync(Replication, [step.Id], CancellationToken.None);
        Assert.Equal(ProvisioningStepOutcome.Applied, Assert.Single(result.Steps).Outcome);

        var reloaded = _config.LoadTableMapping(Replication, "a");
        Assert.True(reloaded.ColumnMappings.Single().Renames.Single().Applied);
        Assert.Equal("Id", Assert.Single(reloaded.TargetColumns).Name);
        Assert.NotNull(reloaded.ColumnsCapturedUtc);
    }

    [Fact]
    public async Task Apply_ASelectedTableStep_WhoseDatabasePrerequisiteWasExcluded_StillRuns_WithAWarning()
    {
        var sourceDriver = ChangeTrackingSourceDriver("Sales");
        var recordingConnection = new FakeDbConnection();
        var factory = new FakeConnectionFactory(
            connectionName => connectionName == "src" ? sourceDriver : SatisfiedTargetDriver(),
            connectionName => connectionName == "src" ? recordingConnection : new FakeDbConnection());

        SaveMapping("a", "src", "Sales", "Orders", "tgt", "DW", "Orders");

        var service = CreateService(factory);
        var plan = await service.GetReplicationPlanAsync(Replication, CancellationToken.None);
        var group = Assert.Single(plan.Groups, g => g.Side == ProvisioningEndpointSide.Source);
        var databaseStep = Assert.Single(group.Steps, s => s.Scope == ProvisioningStepScope.Database);
        var tableStep = Assert.Single(group.Steps, s => s.Scope == ProvisioningStepScope.Table);

        // The database step is deliberately left out of the selection — the commonest reason being a
        // DBA already ran it out of band.
        var result = await service.ApplyReplicationPlanAsync(Replication, [tableStep.Id], CancellationToken.None);

        var applied = Assert.Single(result.Steps);
        Assert.Equal(ProvisioningStepOutcome.Applied, applied.Outcome);
        Assert.NotNull(applied.Warning);
        Assert.Contains("database-level prerequisite", applied.Warning);
        Assert.Equal([tableStep.CommandText], recordingConnection.Executed);
    }

    // ---- Fakes ------------------------------------------------------------------------------------

    // `CurrentUser` and the column reader are unused by most tests here: `ApplyReplicationPlanAsync`'s
    // rename/target-column side effects (see the fix below) only fire once a Table-scope, Target-side
    // step actually applies, and `UnusedColumnCatalog.ListColumnsAsync` throwing is caught and turned
    // into an "unreadable" result rather than propagating — so a test built with this factory never
    // touches `currentUser` even when it does exercise that path (nothing to rename, nothing to cache).
    // `CreateServiceWithSideEffects` below is the one test that needs those effects to actually happen.
    private ProvisioningService CreateService(FakeConnectionFactory factory) =>
        new(_config, factory, currentUser: null!, new MappingColumnReader(new UnusedColumnCatalog()), new DriverRegistry());

    private ProvisioningService CreateServiceWithSideEffects(FakeConnectionFactory factory) =>
        new(_config, factory,
            new CurrentUser(
                new HttpContextAccessor(), new AuthOptions(),
                new PasskeyOptions { RelyingPartyId = "localhost", RelyingPartyName = "DbDataSync", Origins = new HashSet<string>() }),
            new MappingColumnReader(new FakeColumnCatalog()), new DriverRegistry());

    private sealed class UnusedColumnCatalog : IColumnCatalog
    {
        public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
            string connectionName, string database, string schema, string table, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeColumnCatalog : IColumnCatalog
    {
        public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
            string connectionName, string database, string schema, string table, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ColumnMetadata>>([new ColumnMetadata("Id", "int", false, true, false)]);
    }

    private sealed class FakeConnectionFactory(
        Func<string, IDriver> driverFor, Func<string, FakeDbConnection>? connectionFor = null) : IConnectionFactory
    {
        public int OpenCount { get; private set; }
        public List<string> Opened { get; } = [];

        public Task<(DbConnection Connection, IDriver Driver)> OpenAsync(
            string connectionName, CancellationToken cancellationToken)
        {
            OpenCount++;
            Opened.Add(connectionName);
            DbConnection connection = connectionFor?.Invoke(connectionName) ?? new FakeDbConnection();
            return Task.FromResult((connection, driverFor(connectionName)));
        }
    }

    /// <summary>A driver whose provisioning behaviour is entirely delegate-driven, so each test states
    /// only the plan(s) it cares about. <see cref="DriverIds.MsSql"/> throughout — never
    /// exercised for its real behaviour here, only so <c>ResolveDialect</c> and the column-building
    /// helpers <see cref="DbDataSync.Api.Services.ProvisioningService"/> already runs for real have a
    /// dialect to translate through. <see cref="IDialectProvider"/> for the same reason
    /// <c>GenericDriverBase</c> implements it on every real driver — <c>ResolveDialect</c> resolves
    /// through that interface now, not a driver-type string switch, so a fake standing in for "a real
    /// driver" has to look like one on this point too.</summary>
    private sealed class FakeProvisioningDriver(Func<ProvisioningRequest, ProvisioningPlan> planFactory)
        : IDriver, IProvisioner, IDialectProvider
    {
        private static readonly IReadOnlyList<ColumnMetadata> DefaultColumns =
            [new ColumnMetadata("Id", "int", false, true, false)];

        public string DriverType => DriverIds.MsSql;
        public IReadOnlyList<IChangeReader> Readers { get; } = [];
        public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
        public IReadOnlyList<IChangeWriter> Writers { get; } = [];
        public SqlDialect Dialect => MsSqlDialect.Instance;

        public IReadOnlyList<string> SupportedActions { get; } =
        [
            ProvisioningActions.EnableSourceChangeCapture,
            ProvisioningActions.CreateTargetTable,
            ProvisioningActions.AlterTargetTable,
        ];

        public DbConnection CreateConnection(ConnectionConfig connection, string? credential) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
            DbConnection connection, string database, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
            DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
            Task.FromResult(DefaultColumns);

        public Task<ProvisioningPlan> PlanAsync(
            DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(planFactory(request));
    }

    /// <summary>A connection that records every statement handed to it and can be told to throw for a
    /// particular one — enough to drive the stop-at-first-failure and subset-apply tests without a real
    /// database. Every abstract member <see cref="DbConnection"/>/<see cref="DbCommand"/> declare is
    /// implemented, even where unused, because both are abstract classes and nothing here calls the
    /// unused ones.</summary>
    private sealed class FakeDbConnection : DbConnection
    {
        public List<string> Executed { get; } = [];
        public Action<string>? OnExecute { get; set; }

        private ConnectionState _state = ConnectionState.Closed;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new FakeDbCommand(this);

        internal Task<int> ExecuteAsync(string commandText)
        {
            Executed.Add(commandText);
            OnExecute?.Invoke(commandText);
            return Task.FromResult(1);
        }
    }

    private sealed class FakeDbCommand(FakeDbConnection connection) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            connection.ExecuteAsync(CommandText);
    }

    /// <summary>Never actually used — nothing in these tests binds a parameter — but
    /// <see cref="DbCommand.Parameters"/> is abstract, so something concrete has to exist.</summary>
    private sealed class FakeParameterCollection : DbParameterCollection
    {
        private readonly List<object> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _items.Add(value); return _items.Count - 1; }
        public override void AddRange(Array values) => throw new NotSupportedException();
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains(value);
        public override bool Contains(string value) => false;
        public override void CopyTo(Array array, int index) => throw new NotSupportedException();
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf(value);
        public override int IndexOf(string parameterName) => -1;
        public override void Insert(int index, object value) => _items.Insert(index, value);
        public override void Remove(object value) => _items.Remove(value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => throw new NotSupportedException();
        protected override DbParameter GetParameter(int index) => (DbParameter)_items[index];
        protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => throw new NotSupportedException();
    }

    /// <summary><see cref="DbException"/> is abstract with only protected constructors — this is the
    /// concrete stand-in every fake command throws to simulate a least-privileged connection rejecting a
    /// statement, exactly as <c>ProvisioningService.RunStepsAsync</c>'s real <c>catch (DbException)</c>
    /// expects.</summary>
    private sealed class FakeDbException(string message) : DbException(message);
}
