using DataSync.Core.Config;
using Xunit;

namespace DataSync.Core.Tests;

/// <summary>
/// A replication owns its endpoints and a table mapping inherits them unless it says otherwise. These
/// pin the merge, and — most importantly — that config written before endpoints existed still resolves
/// to exactly what it used to mean.
/// </summary>
public sealed class EndpointResolutionTests
{
    private static ReplicationTaskConfig Task(EndpointRef? source = null, EndpointRef? target = null) => new()
    {
        Name = "crm-sync",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 15 },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "Watermark" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
        Endpoints = new TaskEndpoints { Source = source, Target = target },
    };

    private static readonly EndpointRef Src = new() { ConnectionName = "src-conn", Database = "AppDb" };
    private static readonly EndpointRef Tgt = new() { ConnectionName = "tgt-conn", Database = "WarehouseDb" };

    [Fact]
    public void MappingWithNoEndpoint_InheritsTheReplications()
    {
        var resolved = EndpointResolution.ResolveSource(Task(Src, Tgt), new SourceTableSpec { Table = "Orders" });

        Assert.Equal("src-conn", resolved.ConnectionName);
        Assert.Equal("AppDb", resolved.Database);
        Assert.Equal("dbo", resolved.Schema);
        Assert.Equal("Orders", resolved.Table);
    }

    [Fact]
    public void MappingWithItsOwnEndpoint_OverridesTheReplications()
    {
        var resolved = EndpointResolution.ResolveSource(
            Task(Src, Tgt),
            new SourceTableSpec { ConnectionName = "other-conn", Database = "OtherDb", Table = "Orders" });

        Assert.Equal("other-conn", resolved.ConnectionName);
        Assert.Equal("OtherDb", resolved.Database);
    }

    /// <summary>Each field falls back on its own, so a mapping can point at a different database on
    /// the replication's connection without restating the connection.</summary>
    [Fact]
    public void FieldsFallBackIndependently()
    {
        var resolved = EndpointResolution.ResolveTarget(
            Task(Src, Tgt),
            new TableSpec { Database = "ArchiveDb", Table = "Orders" });

        Assert.Equal("tgt-conn", resolved.ConnectionName);
        Assert.Equal("ArchiveDb", resolved.Database);
    }

    [Fact]
    public void SourceAndTargetResolveSeparately()
    {
        var task = Task(Src, Tgt);
        var source = EndpointResolution.ResolveSource(task, new SourceTableSpec { ConnectionName = "s2", Database = "D2", Table = "T" });
        var target = EndpointResolution.ResolveTarget(task, new TableSpec { Table = "T" });

        Assert.Equal("s2", source.ConnectionName);
        Assert.Equal("tgt-conn", target.ConnectionName);
    }

    [Fact]
    public void Filter_SurvivesResolution()
    {
        var resolved = EndpointResolution.ResolveSource(
            Task(Src, Tgt), new SourceTableSpec { Table = "Orders", Filter = "Status = 'Active'" });

        Assert.Equal("Status = 'Active'", resolved.Filter);
    }

    [Fact]
    public void NothingToInheritAndNothingSet_SaysWhichSideAndField()
    {
        var ex = Assert.Throws<ConfigValidationException>(
            () => EndpointResolution.ResolveSource(Task(), new SourceTableSpec { Table = "Orders" }));

        Assert.Contains("source", ex.Message);
        Assert.Contains("connection", ex.Message);
        Assert.Contains("crm-sync", ex.Message);
    }

    [Fact]
    public void PartiallyInheritable_StillNamesTheMissingField()
    {
        var task = Task(new EndpointRef { ConnectionName = "src-conn" }, Tgt);

        var ex = Assert.Throws<ConfigValidationException>(
            () => EndpointResolution.ResolveSource(task, new SourceTableSpec { Table = "Orders" }));

        Assert.Contains("database", ex.Message);
    }

    /// <summary>
    /// The compatibility case, and the reason the override is two nullable fields rather than a nested
    /// object: every config written before endpoints existed carries a full connection and database on
    /// every mapping and no endpoints on the replication. That has to keep meaning what it meant.
    /// </summary>
    [Fact]
    public void ConfigWrittenBeforeEndpointsExisted_ResolvesUnchanged()
    {
        var legacyTask = Task();  // no endpoints at all, as such a task.yaml has none
        var legacyMapping = new SourceTableSpec
        {
            ConnectionName = "src-conn", Database = "AppDb", Schema = "sales", Table = "Orders",
        };

        var resolved = EndpointResolution.ResolveSource(legacyTask, legacyMapping);

        Assert.Equal("src-conn", resolved.ConnectionName);
        Assert.Equal("AppDb", resolved.Database);
        Assert.Equal("sales", resolved.Schema);
        Assert.Equal("Orders", resolved.Table);
    }

    [Fact]
    public void Validate_ChecksEveryMappingSide()
    {
        var mapping = new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { Table = "Orders" }],
            Targets = [new TableSpec { Table = "Orders" }],
        };

        EndpointResolution.Validate(Task(Src, Tgt), mapping);                       // resolves: no throw
        Assert.Throws<ConfigValidationException>(() => EndpointResolution.Validate(Task(Src), mapping)); // target unset
    }
}
