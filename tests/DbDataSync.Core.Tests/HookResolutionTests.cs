using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Core.Tests;

/// <summary>Mapping, then replication, then connection — the same walk as <see cref="ScriptResolution"/>
/// (via the shared <see cref="HierarchicalBinding"/> helper), applied to a hook point's list instead of
/// a single binding.</summary>
public sealed class HookResolutionTests
{
    private const string Point = HookPoints.BeforeLoad;

    private static ConnectionConfig Connection(params (string Point, List<HookConfig>? Hooks)[] hooks)
    {
        var connection = new ConnectionConfig
        {
            Name = "tgt", DriverType = ConnectionDriverType.MsSql, Host = "h", AuthMode = AuthMode.SqlAuth,
        };
        foreach (var (point, list) in hooks)
            connection.Hooks[point] = list;
        return connection;
    }

    private static ReplicationTaskConfig Task(params (string Point, List<HookConfig>? Hooks)[] hooks)
    {
        var task = new ReplicationTaskConfig
        {
            Name = "sync",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "r" },
                Cache = new CacheConfig { Kind = "c" },
                Writer = new WriterConfig { Kind = "w" },
            },
        };
        foreach (var (point, list) in hooks)
            task.Hooks[point] = list;
        return task;
    }

    private static TableMappingConfig Mapping(params (string Point, List<HookConfig>? Hooks)[] hooks)
    {
        var mapping = new TableMappingConfig
        {
            Name = "orders", Sources = [new SourceTableSpec { Table = "Orders" }], Targets = [new TableSpec { Table = "Orders" }],
        };
        foreach (var (point, list) in hooks)
            mapping.Hooks[point] = list;
        return mapping;
    }

    private static List<HookConfig> Hooks(string sql) => [new HookConfig { Sql = sql }];

    [Fact]
    public void NothingBoundAnywhere_ResolvesToNull()
    {
        Assert.Null(HookResolution.Resolve(Point, Connection(), Task(), Mapping()));
        Assert.Equal(BindingLevel.Unbound, HookResolution.LevelOf(Point, Connection(), Task(), Mapping()));
    }

    [Fact]
    public void AConnectionLevelList_IsInheritedByEverythingBelow()
    {
        var resolved = HookResolution.Resolve(Point, Connection((Point, Hooks("A"))), Task(), Mapping());

        Assert.Equal("A", Assert.Single(resolved!).Sql);
        Assert.Equal(BindingLevel.Connection, HookResolution.LevelOf(Point, Connection((Point, Hooks("A"))), Task(), Mapping()));
    }

    [Fact]
    public void AReplicationLevelList_ReplacesTheConnectionsRatherThanAppending()
    {
        var resolved = HookResolution.Resolve(Point, Connection((Point, Hooks("A"))), Task((Point, Hooks("B"))), Mapping());

        Assert.Equal("B", Assert.Single(resolved!).Sql);
    }

    [Fact]
    public void AMappingLevelList_BeatsEverything()
    {
        var resolved = HookResolution.Resolve(
            Point, Connection((Point, Hooks("A"))), Task((Point, Hooks("B"))), Mapping((Point, Hooks("C"))));

        Assert.Equal("C", Assert.Single(resolved!).Sql);
        Assert.Equal(BindingLevel.Mapping,
            HookResolution.LevelOf(Point, Connection((Point, Hooks("A"))), Task((Point, Hooks("B"))), Mapping((Point, Hooks("C")))));
    }

    [Fact]
    public void AMappingCanTurnAnInheritedHookListOffWithAnExplicitNull()
    {
        var resolved = HookResolution.Resolve(Point, Connection((Point, Hooks("A"))), Task(), Mapping((Point, null)));

        Assert.Null(resolved);
        Assert.Equal(BindingLevel.Mapping, HookResolution.LevelOf(Point, Connection((Point, Hooks("A"))), Task(), Mapping((Point, null))));
    }

    [Fact]
    public void PointsResolveIndependently()
    {
        var connection = Connection((HookPoints.BeforeLoad, Hooks("A")), (HookPoints.AfterLoad, Hooks("Z")));
        var mapping = Mapping((HookPoints.BeforeLoad, Hooks("C")));

        Assert.Equal("C", Assert.Single(HookResolution.Resolve(HookPoints.BeforeLoad, connection, Task(), mapping)!).Sql);
        Assert.Equal("Z", Assert.Single(HookResolution.Resolve(HookPoints.AfterLoad, connection, Task(), mapping)!).Sql);
    }
}
