using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Core.Tests;

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
    /// A BulkLoad's per-work-item Kind stays the most specific — over a mapping's override exactly as
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

    /// <summary>
    /// Phase 133: the Bulk Load pipeline resolves through the same two-level shape as Change
    /// Processing, but independently of it — a mapping's Change Processing overrides say nothing about
    /// what its Bulk Load pipeline runs, and vice versa.
    /// </summary>
    [Fact]
    public void A_mapping_with_no_bulk_load_overrides_runs_the_replications_bulk_load_kinds()
    {
        var task = Task();
        task.BulkLoad = new BulkLoadConfig
        {
            Reader = new ReaderConfig { Kind = "TaskBulkReader", Options = { ["batch"] = "500" } },
            Cache = new CacheConfig { Kind = "TaskBulkCache" },
            Writer = new WriterConfig { Kind = "TaskBulkWriter" },
        };
        var mapping = Mapping();

        Assert.Equal("TaskBulkReader", PipelineResolution.BulkLoadReader(task, mapping).Kind);
        Assert.Equal("500", PipelineResolution.BulkLoadReader(task, mapping).Options["batch"]);
        Assert.Equal("TaskBulkCache", PipelineResolution.BulkLoadCache(task, mapping).Kind);
        Assert.Equal("TaskBulkWriter", PipelineResolution.BulkLoadWriter(task, mapping).Kind);
    }

    /// <summary>A replication that says nothing about Bulk Load still has a working pipeline — the
    /// reader defaults to BatchReload, and the cache/writer fall through to the resolved Change
    /// Processing ones (<see cref="PipelineResolution.BulkLoadCache"/>/<see cref="PipelineResolution.BulkLoadWriter"/>).</summary>
    [Fact]
    public void An_unconfigured_bulk_load_pipeline_defaults_to_BatchReload_and_falls_through_to_change_processing()
    {
        var task = Task();
        var mapping = Mapping();

        Assert.Equal("BatchReload", PipelineResolution.BulkLoadReader(task, mapping).Kind);
        Assert.Equal("TaskCache", PipelineResolution.BulkLoadCache(task, mapping).Kind);
        Assert.Equal("TaskWriter", PipelineResolution.BulkLoadWriter(task, mapping).Kind);
    }

    /// <summary>Overriding only the Bulk Load reader on a mapping leaves the Bulk Load cache/writer
    /// resolving from the replication (or its Change Processing fallback) unaffected — the same
    /// per-stage independence Change Processing already has.</summary>
    [Fact]
    public void Overriding_only_the_mappings_bulk_load_reader_leaves_bulk_load_cache_and_writer_inherited()
    {
        var task = Task();
        task.BulkLoad = new BulkLoadConfig
        {
            Reader = new ReaderConfig { Kind = "TaskBulkReader" },
            Cache = new CacheConfig { Kind = "TaskBulkCache" },
            Writer = null,
        };
        var mapping = Mapping();
        mapping.BulkLoadReaderOverride = new ReaderConfig { Kind = "MappingBulkReader" };

        Assert.Equal("MappingBulkReader", PipelineResolution.BulkLoadReader(task, mapping).Kind);
        Assert.Equal("TaskBulkCache", PipelineResolution.BulkLoadCache(task, mapping).Kind);
        // The replication's own BulkLoad.Writer is null, so this still falls all the way through to
        // Change Processing's writer.
        Assert.Equal("TaskWriter", PipelineResolution.BulkLoadWriter(task, mapping).Kind);
    }

    /// <summary>A mapping overriding all three Bulk Load stages independently gets each override, and
    /// none of it leaks onto the mapping's Change Processing resolution.</summary>
    [Fact]
    public void A_mapping_can_override_all_three_bulk_load_stages_independently_of_change_processing()
    {
        var task = Task();
        var mapping = Mapping();
        mapping.BulkLoadReaderOverride = new ReaderConfig { Kind = "MappingBulkReader" };
        mapping.BulkLoadCacheOverride = new CacheConfig { Kind = "MappingBulkCache" };
        mapping.BulkLoadWriterOverride = new WriterConfig { Kind = "MappingBulkWriter" };

        Assert.Equal("MappingBulkReader", PipelineResolution.BulkLoadReader(task, mapping).Kind);
        Assert.Equal("MappingBulkCache", PipelineResolution.BulkLoadCache(task, mapping).Kind);
        Assert.Equal("MappingBulkWriter", PipelineResolution.BulkLoadWriter(task, mapping).Kind);

        // Change Processing resolution is untouched by the Bulk Load overrides above.
        Assert.Equal("TaskReader", PipelineResolution.Reader(task, mapping).Kind);
        Assert.Equal("TaskCache", PipelineResolution.Cache(task, mapping).Kind);
        Assert.Equal("TaskWriter", PipelineResolution.Writer(task, mapping).Kind);
    }

    [Fact]
    public void The_level_a_bulk_load_stage_resolved_at_is_reportable()
    {
        var mapping = Mapping();
        Assert.Equal(BindingLevel.Replication, PipelineResolution.LevelOfBulkLoadReader(mapping));
        Assert.Equal(BindingLevel.Replication, PipelineResolution.LevelOfBulkLoadCache(mapping));
        Assert.Equal(BindingLevel.Replication, PipelineResolution.LevelOfBulkLoadWriter(mapping));

        mapping.BulkLoadWriterOverride = new WriterConfig { Kind = "MappingBulkWriter" };
        Assert.Equal(BindingLevel.Replication, PipelineResolution.LevelOfBulkLoadReader(mapping));
        Assert.Equal(BindingLevel.Mapping, PipelineResolution.LevelOfBulkLoadWriter(mapping));
    }
}
