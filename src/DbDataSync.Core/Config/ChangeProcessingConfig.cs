using System.ComponentModel;

namespace DbDataSync.Core.Config;

/// <summary>
/// A reader/cache/writer "Kind" is a driver-advertised identifier (e.g. "MsSqlChangeTracking",
/// "MsSqlStagingTable", "MsSqlMerge") resolved against the driver registry introduced in Phase 3.
/// Phase 1 does not validate Kind against real drivers yet — only that config shape is well-formed.
/// </summary>
public sealed class ReaderConfig
{
    public required string Kind { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class CacheConfig
{
    public required string Kind { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class WriterConfig
{
    public required string Kind { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class ChangeProcessingConfig
{
    public required ReaderConfig Reader { get; set; }
    public required CacheConfig Cache { get; set; }
    public required WriterConfig Writer { get; set; }

    /// <summary>
    /// How many of this replication's table mappings its worker's <b>change-processing lane</b>
    /// (<c>RunKind.Primary</c> — ongoing incremental sync) reads, stages and applies at once — the
    /// number of consumers <c>RunExecutor</c> runs on that lane, passed to the runner as
    /// <c>--degree-of-parallelism</c> by <c>DbDataSync.Api.Services.ProcessSupervisor</c>.
    /// <para>
    /// The worker runs two independent lanes (phase-108): this one, and a <b>bulk load lane</b>
    /// (<c>RunKind.BulkLoad</c> + <c>RunKind.Verification</c>) sized by
    /// <see cref="BulkLoadDegreeOfParallelism"/>. A long reload on the bulk load lane can no longer
    /// take a consumer slot an incremental pass needs — but the total concurrency ceiling for the
    /// replication is now the two numbers added, not one shared value.
    /// </para>
    /// <para>
    /// Not per stage: the reader/cache/writer of a single mapping run in sequence within one consumer,
    /// and a single mapping never occupies more than one consumer slot at a time — so raising this
    /// adds concurrency <em>across</em> mappings and never within one.
    /// </para>
    /// <para>
    /// <see cref="DefaultValueAttribute"/> is load-bearing the same way it is on
    /// <see cref="ReplicationTaskConfig.Enabled"/>: the YAML serializer omits values equal to the
    /// attribute's, so <c>4</c> is not written and anything else is. Unlike <c>Enabled</c>, an
    /// explicit <c>4</c> and an absent value mean exactly the same thing here.
    /// </para>
    /// </summary>
    [DefaultValue(DefaultDegreeOfParallelism)]
    public int DegreeOfParallelism { get; set; } = DefaultDegreeOfParallelism;

    /// <summary>
    /// How many table mappings the worker's <b>bulk load lane</b> processes at once —
    /// <c>RunKind.BulkLoad</c> (a segment of an on-demand reload) and <c>RunKind.Verification</c> (a
    /// source/target comparison). Both read whole tables and can run for a long time; giving them
    /// their own budget, separate from <see cref="DegreeOfParallelism"/>, is what stops a big reload
    /// from starving incremental sync. Passed to the runner as <c>--bulk-load-parallelism</c>.
    /// </summary>
    [DefaultValue(DefaultDegreeOfParallelism)]
    public int BulkLoadDegreeOfParallelism { get; set; } = DefaultDegreeOfParallelism;

    /// <summary>The value a lane takes when config says nothing — matches
    /// <c>DbDataSync.TaskRunner.TaskRunnerOptions</c>'s own default so a worker launched by hand and
    /// one launched by the API behave the same when neither is told otherwise.</summary>
    public const int DefaultDegreeOfParallelism = 4;
}
