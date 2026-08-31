using DataSync.Core.Config;
using Xunit;

namespace DataSync.Core.Tests;

/// <summary>
/// The two-level stage resolution phase 68 added: the replication's pipeline, unless the mapping
/// states its own — each stage independently, Kind and Options together.
/// </summary>
public sealed class PipelineResolutionTests
{
    private static ReplicationTaskConfig Task() => new()
    {
        Name = "task",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "TaskReader", Options = { ["batch"] = "100" } },
            Cache = new CacheConfig { Kind = "TaskCache", Options = { ["temp"] = "yes" } },
            Writer = new WriterConfig { Kind = "TaskWriter", Options = { ["naturalKey"] = "TaskKey" } },
        },
    };

    private static TableMappingConfig Mapping() => new()
    {
        Name = "dbo.Orders",
        Sources = [new SourceTableSpec { Table = "Orders" }],
        Targets = [new TableSpec { Table = "Orders" }],
    };

    [Fact]
    public void A_mapping_with_no_overrides_runs_the_replications_kinds_and_options()
    {
        var task = Task();
        var mapping = Mapping();

        Assert.Equal("TaskReader", PipelineResolution.Reader(task, mapping).Kind);
        Assert.Equal("TaskCache", PipelineResolution.Cache(task, mapping).Kind);
        Assert.Equal("TaskWriter", PipelineResolution.Writer(task, mapping).Kind);

        Assert.Equal("100", PipelineResolution.Reader(task, mapping).Options["batch"]);
        Assert.Equal("yes", PipelineResolution.Cache(task, mapping).Options["temp"]);
        Assert.Equal("TaskKey", PipelineResolution.Writer(task, mapping).Options["naturalKey"]);
    }

    [Fact]
    public void Overriding_one_stage_leaves_the_other_two_inherited()
    {
        var task = Task();
        var mapping = Mapping();
        mapping.WriterOverride = new WriterConfig { Kind = "MappingWriter", Options = { ["naturalKey"] = "OrderId" } };

        Assert.Equal("MappingWriter", PipelineResolution.Writer(task, mapping).Kind);
        Assert.Equal("OrderId", PipelineResolution.Writer(task, mapping).Options["naturalKey"]);

        Assert.Equal("TaskReader", PipelineResolution.Reader(task, mapping).Kind);
        Assert.Equal("TaskCache", PipelineResolution.Cache(task, mapping).Kind);
    }

    /// <summary>
    /// The atomic-replace rule: an override's Options are its own, not the replication's with the
    /// override's merged over the top. An option that belonged to a Kind nobody selected any more must
    /// not survive.
    /// </summary>
    [Fact]
    public void An_override_replaces_options_wholesale_rather_than_merging()
    {
        var task = Task();
        var mapping = Mapping();
        mapping.ReaderOverride = new ReaderConfig { Kind = "MappingReader" };

        Assert.Empty(PipelineResolution.Reader(task, mapping).Options);
    }

    [Fact]
    public void No_mapping_at_all_resolves_to_the_replications_own_stages()
    {
        var task = Task();

        Assert.Equal("TaskReader", PipelineResolution.Reader(task, null).Kind);
        Assert.Equal("TaskCache", PipelineResolution.Cache(task, null).Kind);
        Assert.Equal("TaskWriter", PipelineResolution.Writer(task, null).Kind);
    }

    /// <summary>
    /// A Backfill's per-work-item Kind stays the most specific — over a mapping's override exactly as
    /// it was already over the replication's configured Kind.
    /// </summary>
    [Fact]
    public void A_work_items_kind_wins_over_both_levels()
    {
        var task = Task();
        var mapping = Mapping();
        mapping.ReaderOverride = new ReaderConfig { Kind = "MappingReader" };
        mapping.CacheOverride = new CacheConfig { Kind = "MappingCache" };
        mapping.WriterOverride = new WriterConfig { Kind = "MappingWriter" };

        Assert.Equal("ItemReader", PipelineResolution.ReaderKind("ItemReader", task, mapping));
        Assert.Equal("ItemCache", PipelineResolution.CacheKind("ItemCache", task, mapping));
        Assert.Equal("ItemWriter", PipelineResolution.WriterKind("ItemWriter", task, mapping));
    }

    [Fact]
    public void Without_a_work_item_kind_the_mappings_override_is_what_runs()
    {
        var task = Task();
        var mapping = Mapping();
        mapping.ReaderOverride = new ReaderConfig { Kind = "MappingReader" };

        Assert.Equal("MappingReader", PipelineResolution.ReaderKind(null, task, mapping));
        Assert.Equal("TaskCache", PipelineResolution.CacheKind(null, task, mapping));
    }

    [Fact]
    public void The_level_a_stage_resolved_at_is_reportable()
    {
        var mapping = Mapping();
        Assert.Equal(BindingLevel.Replication, PipelineResolution.LevelOfReader(mapping));
        Assert.Equal(BindingLevel.Replication, PipelineResolution.LevelOfWriter(mapping));

        mapping.WriterOverride = new WriterConfig { Kind = "MappingWriter" };
        Assert.Equal(BindingLevel.Replication, PipelineResolution.LevelOfReader(mapping));
        Assert.Equal(BindingLevel.Mapping, PipelineResolution.LevelOfWriter(mapping));
    }
}
