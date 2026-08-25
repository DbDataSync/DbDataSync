using System.Data.Common;
using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// Applies a staged change set to the target. Implementations may be target-specific (MERGE, bulk
/// operations) or generic (ordered insert/update/delete statements).
/// </summary>
public interface IChangeWriter
{
    /// <summary>Identifier matched against <see cref="WriterConfig.Kind"/>, e.g. "MsSqlMerge".</summary>
    string Kind { get; }

    Task<WriteResult> ApplyAsync(
        DbConnection targetConnection,
        TableRef target,
        StagedChangeSet staged,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);
}
