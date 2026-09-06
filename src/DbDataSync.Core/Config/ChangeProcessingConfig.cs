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
    /// How many of this replication's table mappings its worker process reads, stages and applies at
    /// once — the number of concurrent work-queue consumers <c>RunExecutor.ExecuteWorkerAsync</c>
    /// runs, passed to the runner as <c>--degree-of-parallelism</c> by
    /// <c>DbDataSync.Api.Services.ProcessSupervisor</c>.
    /// <para>
    /// One value for the whole replication, not per stage: there is one worker process draining one
    /// queue, and the reader/cache/writer of a single mapping run in sequence within one consumer. A
    /// single mapping never occupies more than one consumer slot at a time regardless of this number
    /// — <c>WorkQueueStore.TryClaimNext</c>'s <c>NOT EXISTS</c> self-join already guarantees that — so
    /// raising it adds concurrency <em>across</em> mappings and never within one.
    /// </para>
    /// <para>
    /// <see cref="DefaultValueAttribute"/> is load-bearing the same way it is on
    /// <see cref="ReplicationTaskConfig.Enabled"/>: the YAML serializer omits values equal to the
    /// attribute's, so <c>4</c> is not written and anything else is. Unlike <c>Enabled</c>, an
    /// explicit <c>4</c> and an absent value mean exactly the same thing here, so a plain
    /// non-nullable int is right — there is nothing to lose by not writing it down.
    /// </para>
    /// </summary>
    [DefaultValue(DefaultDegreeOfParallelism)]
    public int DegreeOfParallelism { get; set; } = DefaultDegreeOfParallelism;

    /// <summary>The value a replication takes when its config says nothing — matches
    /// <c>DbDataSync.TaskRunner.TaskRunnerOptions.DegreeOfParallelism</c>'s own default so a worker
    /// launched by hand and one launched by the API behave the same when neither is told otherwise.</summary>
    public const int DefaultDegreeOfParallelism = 4;
}
