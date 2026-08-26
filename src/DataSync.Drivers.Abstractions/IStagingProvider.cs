using System.Data.Common;
using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// Persists a change set before it's applied to the target (the "Change Cache" in
/// architecture/planning/architecture.md). Implementations may be target-specific (a staging table
/// via bulk insert) or generic (e.g. parquet, deferred past v1).
/// </summary>
public interface IStagingProvider
{
    /// <summary>Identifier matched against <see cref="CacheConfig.Kind"/>, e.g. "MsSqlStagingTable".</summary>
    string Kind { get; }

    Task<StagedChangeSet> StageAsync(
        DbConnection targetConnection,
        TableRef target,
        IAsyncEnumerable<ChangeRow> rows,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Discards a staged change set once it has been applied (or once applying it has failed).
    /// <para>
    /// Needed because one unit of work can stage more than once — a standalone reload replication
    /// iterates its configured segments within a single pass — so staging can no longer rely on
    /// connection teardown to clean up after it. It's also the prerequisite for reusing one
    /// long-lived connection per consumer slot across many work items, flagged as a future
    /// optimization in phase-008-work-queue-schema.md.
    /// </para>
    /// </summary>
    Task CleanupAsync(
        DbConnection targetConnection,
        StagedChangeSet staged,
        CancellationToken cancellationToken);
}
