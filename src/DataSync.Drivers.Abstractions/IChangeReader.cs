using System.Data.Common;
using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>
/// Produces a change result set + change markers for one source table, per
/// architecture/detailed-design.md §3.4. Implementations may be driver-specific (SQL Server Change
/// Tracking, CDC) or generic (batch WHERE-clause reads).
/// <para>
/// <paramref name="sourceConnection" /> must be a different connection instance from whatever
/// connection a subsequent <see cref="IStagingProvider"/>/<see cref="IChangeWriter"/> call uses for
/// the target — even when source and target happen to be the same physical server. A staging
/// provider that streams this reader's rows into e.g. SqlBulkCopy on the target connection will
/// otherwise deadlock: SqlBulkCopy holds its connection exclusively for the duration of the copy, so
/// the source query this reader's async enumerable lazily executes can't run on that same connection
/// concurrently — MARS does not help here. See architecture/implementation/done/phase-003-mssql-driver.md.
/// </para>
/// </summary>
public interface IChangeReader
{
    /// <summary>Identifier matched against <see cref="ReaderConfig.Kind"/>, e.g. "MsSqlChangeTracking".</summary>
    string Kind { get; }

    Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);
}
