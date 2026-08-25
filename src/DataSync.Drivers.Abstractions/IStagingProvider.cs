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
}
