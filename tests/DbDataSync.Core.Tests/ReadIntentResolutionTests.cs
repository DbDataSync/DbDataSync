using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// What a mapping's next pass defaults to when it has never stored one of its own — mapping over
/// replication over the application default, each falling back independently. See phase 100.
/// </summary>
public sealed class ReadIntentResolutionTests
{
    private static ReplicationTaskConfig Task(ReadIntent? defaultReadIntent = null) => new()
    {
        Name = "sales",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
        DefaultReadIntent = defaultReadIntent,
    };

    private static TableMappingConfig Mapping(ReadIntent? defaultReadIntent = null) => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { Table = "Orders" }],
        Targets = [new TableSpec { Table = "Orders" }],
        DefaultReadIntent = defaultReadIntent,
    };

    /// <summary>Today's behaviour, unchanged for anybody who configures nothing.</summary>
    [Fact]
    public void WhenNobodyHasSaid_TheDefaultIsInitialLoad()
    {
        Assert.Equal(ReadIntent.InitialLoad, ReadIntentResolution.Default(Task(), Mapping()));
    }

    [Fact]
    public void AMappingThatSaysNothing_TakesTheReplicationsDefault()
    {
        Assert.Equal(
            ReadIntent.ChangesFromLatest,
            ReadIntentResolution.Default(Task(ReadIntent.ChangesFromLatest), Mapping()));
    }

    [Fact]
    public void AMappingThatSaysSomething_Wins()
    {
        Assert.Equal(
            ReadIntent.ChangesFromEarliest,
            ReadIntentResolution.Default(
                Task(ReadIntent.ChangesFromLatest), Mapping(ReadIntent.ChangesFromEarliest)));
    }

    /// <summary>No mapping at all — a lag or default report asking about the replication in general —
    /// resolves the same way a mapping that states nothing does.</summary>
    [Fact]
    public void NoMappingAtAll_ResolvesToTheReplicationsDefault()
    {
        Assert.Equal(ReadIntent.ChangesFromEarliest, ReadIntentResolution.Default(Task(ReadIntent.ChangesFromEarliest), null));
    }

    [Fact]
    public void TheLevelSaysWhereTheAnswerCameFrom()
    {
        Assert.Equal(BindingLevel.Replication, ReadIntentResolution.LevelOf(Mapping()));
        Assert.Equal(BindingLevel.Mapping, ReadIntentResolution.LevelOf(Mapping(ReadIntent.InitialLoad)));
    }

    /// <summary>
    /// The phase 46 regression, at the resolution layer: a mapping explicitly choosing the same value
    /// the application default already is must still be reported as "the mapping said so", not as
    /// "nobody said anything" — which is exactly what would happen if this resolved by comparing values
    /// instead of by asking whether the property is null.
    /// </summary>
    [Fact]
    public void AMappingExplicitlyChoosingTheApplicationDefault_StillReadsAsAMappingLevelChoice()
    {
        var mapping = Mapping(ReadIntent.InitialLoad);

        Assert.Equal(ReadIntent.InitialLoad, ReadIntentResolution.Default(Task(ReadIntent.ChangesFromLatest), mapping));
        Assert.Equal(BindingLevel.Mapping, ReadIntentResolution.LevelOf(mapping));
    }
}
