using DataSync.Core.Config;
using Xunit;

namespace DataSync.Core.Tests;

/// <summary>
/// Table mapping, then replication, then connection — and the one thing endpoints never needed: a way
/// to say "explicitly none".
/// </summary>
public sealed class ScriptResolutionTests
{
    private const string Slot = "sqlColumnExpression";

    private static ConnectionConfig Connection(params (string Slot, ScriptBinding? Binding)[] scripts)
    {
        var connection = new ConnectionConfig
        {
            Name = "src", DriverType = ConnectionDriverType.MsSql, Host = "h", AuthMode = AuthMode.SqlAuth,
        };
        foreach (var (slot, binding) in scripts)
            connection.Scripts[slot] = binding;
        return connection;
    }

    private static ReplicationTaskConfig Task(params (string Slot, ScriptBinding? Binding)[] scripts)
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
        foreach (var (slot, binding) in scripts)
            task.Scripts[slot] = binding;
        return task;
    }

    private static TableMappingConfig Mapping(params (string Slot, ScriptBinding? Binding)[] scripts)
    {
        var mapping = new TableMappingConfig
        {
            Name = "orders", Sources = [new SourceTableSpec { Table = "Orders" }], Targets = [new TableSpec { Table = "Orders" }],
        };
        foreach (var (slot, binding) in scripts)
            mapping.Scripts[slot] = binding;
        return mapping;
    }

    private static ScriptBinding Bind(string name) => new() { ScriptName = name };

    [Fact]
    public void NothingBoundAnywhere_ResolvesToNothing()
    {
        Assert.Null(ScriptResolution.Resolve(Slot, Connection(), Task(), Mapping()));
        Assert.Equal(BindingLevel.Unbound, ScriptResolution.LevelOf(Slot, Connection(), Task(), Mapping()));
    }

    [Fact]
    public void AConnectionBinding_IsInheritedByEverythingBelow()
    {
        var resolved = ScriptResolution.Resolve(Slot, Connection((Slot, Bind("upper"))), Task(), Mapping());

        Assert.Equal("upper", resolved!.ScriptName);
        Assert.Equal(BindingLevel.Connection,
            ScriptResolution.LevelOf(Slot, Connection((Slot, Bind("upper"))), Task(), Mapping()));
    }

    [Fact]
    public void AReplicationBinding_BeatsTheConnection()
    {
        var resolved = ScriptResolution.Resolve(
            Slot, Connection((Slot, Bind("upper"))), Task((Slot, Bind("lower"))), Mapping());

        Assert.Equal("lower", resolved!.ScriptName);
    }

    [Fact]
    public void AMappingBinding_BeatsEverything()
    {
        var resolved = ScriptResolution.Resolve(
            Slot, Connection((Slot, Bind("upper"))), Task((Slot, Bind("lower"))), Mapping((Slot, Bind("pad"))));

        Assert.Equal("pad", resolved!.ScriptName);
        Assert.Equal(BindingLevel.Mapping,
            ScriptResolution.LevelOf(Slot, Connection((Slot, Bind("upper"))), Task((Slot, Bind("lower"))), Mapping((Slot, Bind("pad")))));
    }

    [Fact]
    public void AMappingCanTurnAnInheritedScriptOff()
    {
        // The case endpoints never had. "Absent" cannot mean both *inherit* and *none*, so a key present
        // with a null value is how a mapping says "not here" — and a dictionary is what makes the two
        // distinguishable at all.
        var resolved = ScriptResolution.Resolve(Slot, Connection((Slot, Bind("upper"))), Task(), Mapping((Slot, null)));

        Assert.Null(resolved);
        // Still *bound* at the mapping — it is an override, not an absence, and a UI should say so.
        Assert.Equal(BindingLevel.Mapping,
            ScriptResolution.LevelOf(Slot, Connection((Slot, Bind("upper"))), Task(), Mapping((Slot, null))));
    }

    [Fact]
    public void AReplicationCanTurnAConnectionScriptOffForAllItsMappings()
    {
        Assert.Null(ScriptResolution.Resolve(Slot, Connection((Slot, Bind("upper"))), Task((Slot, null)), Mapping()));
    }

    [Fact]
    public void ABindingIsAtomic_ParametersNeverMergeAcrossLevels()
    {
        // Merging would mean reading a mapping does not tell you what runs — you would have to read
        // three files and combine them in your head.
        var connection = Connection((Slot, new ScriptBinding { ScriptName = "pad", Parameters = { ["width"] = "10", ["pad"] = "0" } }));
        var mapping = Mapping((Slot, new ScriptBinding { ScriptName = "pad", Parameters = { ["width"] = "20" } }));

        var resolved = ScriptResolution.Resolve(Slot, connection, Task(), mapping)!;

        Assert.Equal("20", resolved.Parameters["width"]);
        Assert.DoesNotContain("pad", resolved.Parameters.Keys);
    }

    [Fact]
    public void SlotsResolveIndependently()
    {
        var connection = Connection((Slot, Bind("upper")), ("other", Bind("fromConnection")));
        var mapping = Mapping((Slot, Bind("pad")));

        Assert.Equal("pad", ScriptResolution.Resolve(Slot, connection, Task(), mapping)!.ScriptName);
        Assert.Equal("fromConnection", ScriptResolution.Resolve("other", connection, Task(), mapping)!.ScriptName);
    }
}
