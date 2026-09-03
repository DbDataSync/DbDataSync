using System.Data.Common;
using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// Persists a change set before it's applied to the target (the "Change Cache" in
/// architecture/planning/done/architecture.md). Implementations may be target-specific (a staging table
/// via bulk insert) or generic (e.g. parquet, deferred past v1).
/// </summary>
public interface IStagingProvider
{
    /// <summary>Identifier matched against <see cref="CacheConfig.Kind"/>, e.g. "MsSqlStagingTable".</summary>
    string Kind { get; }

    /// <inheritdoc cref="IChangeReader.Parameters"/>
    IReadOnlyList<ParameterDescriptor> Parameters => [];

    /// <param name="mappingName">The table mapping this staging call belongs to — see
    /// <see cref="IChangeReader.ReadChangesAsync"/>'s identical parameter.</param>
    /// <param name="targetColumns">
    /// The target's shape as of the mapping's last save or explicit refresh (phase 90) —
    /// <see cref="TableMappingConfig.TargetColumns"/>, verbatim. A provider that needs the target's
    /// column types to build a staging table looks them up here and throws
    /// <see cref="MetadataNotCachedException"/> when the cache is empty or a mapped column isn't in it,
    /// rather than querying the target's catalog live — see phase 91.
    /// </param>
    Task<StagedChangeSet> StageAsync(
        DbConnection targetConnection,
        TableRef target,
        IAsyncEnumerable<ChangeRow> rows,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> targetColumns,
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
