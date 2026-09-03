using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// The replication says what its mappings may do to their targets; a mapping overrides it. Same shape
/// phase 16 established for endpoints, and each setting falls back independently — a mapping can
/// override one and inherit the other.
/// </summary>
public sealed class ProvisioningResolutionTests
{
    private static ReplicationTaskConfig Task(bool? create = null, bool? alter = null) => new()
    {
        Name = "sales",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
        Provisioning = new ProvisioningConfig
        {
            CreateTargetTableIfMissing = create,
            AlterTargetTableColumnsIfMissingOrChanged = alter,
        },
    };

    private static TableMappingConfig Mapping(bool? create = null, bool? alter = null) => new()
    {
        Name = "orders",
        Sources = [new SourceTableSpec { Table = "Orders" }],
        Targets = [new TableSpec { Table = "Orders" }],
        Provisioning = new ProvisioningConfig
        {
            CreateTargetTableIfMissing = create,
            AlterTargetTableColumnsIfMissingOrChanged = alter,
        },
    };

    /// <summary>Off is the answer nobody has to think about, and the one that changes no database.</summary>
    [Fact]
    public void WhenNobodyHasSaid_BothAreOff()
    {
        Assert.False(ProvisioningResolution.CreateTargetTableIfMissing(Task(), Mapping()));
        Assert.False(ProvisioningResolution.AlterTargetTableColumns(Task(), Mapping()));
    }

    [Fact]
    public void AMappingThatSaysNothing_TakesTheReplicationsAnswer()
    {
        Assert.True(ProvisioningResolution.CreateTargetTableIfMissing(Task(create: true), Mapping()));
        Assert.True(ProvisioningResolution.AlterTargetTableColumns(Task(alter: true), Mapping()));
    }

    [Fact]
    public void AMappingThatSaysSomething_Wins()
    {
        Assert.False(ProvisioningResolution.CreateTargetTableIfMissing(Task(create: true), Mapping(create: false)));
        Assert.True(ProvisioningResolution.CreateTargetTableIfMissing(Task(create: false), Mapping(create: true)));
    }

    /// <summary>
    /// The case a single combined setting would get wrong: a replication that creates missing tables
    /// everywhere, and one mapping that additionally wants its columns kept in step.
    /// </summary>
    [Fact]
    public void EachSettingFallsBackIndependently()
    {
        var task = Task(create: true, alter: false);
        var mapping = Mapping(alter: true);

        Assert.True(ProvisioningResolution.CreateTargetTableIfMissing(task, mapping));
        Assert.True(ProvisioningResolution.AlterTargetTableColumns(task, mapping));
    }

    [Fact]
    public void TheLevelSaysWhereTheAnswerCameFrom()
    {
        Assert.Equal(BindingLevel.Replication, ProvisioningResolution.LevelOfCreate(Mapping()));
        Assert.Equal(BindingLevel.Mapping, ProvisioningResolution.LevelOfCreate(Mapping(create: false)));
        Assert.Equal(BindingLevel.Replication, ProvisioningResolution.LevelOfAlter(Mapping(create: true)));
        Assert.Equal(BindingLevel.Mapping, ProvisioningResolution.LevelOfAlter(Mapping(alter: false)));
    }
}
