using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Abstractions.Tests;

/// <summary>
/// The capability flags are opt-in by interface, not by a list maintained here. This asserts the
/// opt-*out* actually works: a driver that does not implement <see cref="IConnectionTester"/> must be
/// reported as such, because the whole point of making it optional is that callers can see the answer
/// and hide an affordance that could never work.
/// </summary>
public sealed class DriverCapabilityOptInTests
{
    [Fact]
    public void ADriverThatCannotTestAConnection_SaysSo()
    {
        var registry = new DriverRegistry();
        registry.Register(new UntestableDriver());

        var capabilities = registry.Describe(ConnectionDriverType.MsSql);

        Assert.NotNull(capabilities);
        Assert.False(capabilities!.SupportsConnectionTest);
    }

    [Fact]
    public void ADriverThatCanTestAConnection_SaysSo()
    {
        var registry = new DriverRegistry();
        registry.Register(new TestableDriver());

        Assert.True(registry.Describe(ConnectionDriverType.MsSql)!.SupportsConnectionTest);
    }

    [Fact]
    public void DetectsDeletes_DefaultsToFalseForAReaderThatDoesNotDeclareIt()
    {
        // The safe default: overstating the guarantee is what loses data.
        var registry = new DriverRegistry();
        registry.Register(new UntestableDriver());

        Assert.False(Assert.Single(registry.Describe(ConnectionDriverType.MsSql)!.Readers).DetectsDeletes);
    }

    [Fact]
    public void SupportedIntents_EmptyForAReaderThatDoesNotDeclareThem()
    {
        // Phase 102: a reader absent from IReadIntentDeclaring — batch reload, say — reports no
        // supported intents at all rather than null, so a UI can offer InitialLoad (always available,
        // never in this set) plus nothing else without a null check.
        var registry = new DriverRegistry();
        registry.Register(new UntestableDriver());

        Assert.Empty(Assert.Single(registry.Describe(ConnectionDriverType.MsSql)!.Readers).SupportedIntents);
    }

    [Fact]
    public void SupportedIntents_ReflectsWhatAReaderDeclares_AndNeverIncludesInitialLoad()
    {
        var registry = new DriverRegistry();
        registry.Register(new IntentDeclaringDriver());

        var reader = Assert.Single(registry.Describe(ConnectionDriverType.MsSql)!.Readers);
        Assert.Equal([ReadIntent.Changes, ReadIntent.ChangesFromEarliest], reader.SupportedIntents);
        Assert.DoesNotContain(ReadIntent.InitialLoad, reader.SupportedIntents);
    }

    [Fact]
    public void ADriverThatCannotProvision_ReportsNoSupportedActions()
    {
        var registry = new DriverRegistry();
        registry.Register(new UntestableDriver());

        Assert.Empty(registry.Describe(ConnectionDriverType.MsSql)!.SupportedProvisioningActions);
    }

    [Fact]
    public void ADriverThatCanProvision_ReportsExactlyItsDeclaredActions()
    {
        var registry = new DriverRegistry();
        registry.Register(new ProvisioningDriver());

        Assert.Equal(
            [ProvisioningActions.CreateTargetTable],
            registry.Describe(ConnectionDriverType.MsSql)!.SupportedProvisioningActions);
    }

    private class UntestableDriver : IDriver
    {
        public ConnectionDriverType DriverType => ConnectionDriverType.MsSql;
        public IReadOnlyList<IChangeReader> Readers { get; } = [new SilentReader()];
        public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
        public IReadOnlyList<IChangeWriter> Writers { get; } = [];

        public DbConnection CreateConnection(ConnectionConfig connection, string? credential) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
            DbConnection connection, string database, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
            DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestableDriver : UntestableDriver, IConnectionTester
    {
        public Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, TimeSpan.Zero, "fake", null));
    }

    private sealed class ProvisioningDriver : UntestableDriver, IProvisioner
    {
        public IReadOnlyList<string> SupportedActions { get; } = [ProvisioningActions.CreateTargetTable];

        public Task<ProvisioningPlan> PlanAsync(
            DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SilentReader : IChangeReader
    {
        public string Kind => "Silent";

        public Task<ReadResult> ReadChangesAsync(
            DbConnection sourceConnection,
            SourceTableRef source,
            string? previousWatermark,
            ReadIntent intent,
            IReadOnlyList<ColumnMapping> columnMappings,
            string mappingName,
            IReadOnlyList<CachedColumn> sourceColumns,
            IReadOnlyDictionary<string, string> options,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class IntentDeclaringReader : IChangeReader, IReadIntentDeclaring
    {
        public string Kind => "IntentDeclaring";

        public IReadOnlySet<ReadIntent> SupportedIntents { get; } =
            new HashSet<ReadIntent> { ReadIntent.ChangesFromEarliest, ReadIntent.Changes };

        public Task<ReadResult> ReadChangesAsync(
            DbConnection sourceConnection,
            SourceTableRef source,
            string? previousWatermark,
            ReadIntent intent,
            IReadOnlyList<ColumnMapping> columnMappings,
            string mappingName,
            IReadOnlyList<CachedColumn> sourceColumns,
            IReadOnlyDictionary<string, string> options,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class IntentDeclaringDriver : IDriver
    {
        public ConnectionDriverType DriverType => ConnectionDriverType.MsSql;
        public IReadOnlyList<IChangeReader> Readers { get; } = [new IntentDeclaringReader()];
        public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];
        public IReadOnlyList<IChangeWriter> Writers { get; } = [];

        public DbConnection CreateConnection(ConnectionConfig connection, string? credential) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
            DbConnection connection, string database, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
            DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
