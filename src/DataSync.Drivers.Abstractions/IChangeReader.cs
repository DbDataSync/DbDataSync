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

    /// <summary>
    /// The settings this reader reads out of its options bag, declared beside the code that reads
    /// them. Empty by default, so a reader with no settings says nothing rather than being made to.
    /// <para>
    /// Choosing a Kind used to mean knowing its option keys by heart and typing them into a free-form
    /// table. Declaring them is what lets the SPA offer them instead — the same relationship
    /// <see cref="DriverCapabilities"/> already has with the Kind pickers.
    /// </para>
    /// </summary>
    IReadOnlyList<ParameterDescriptor> Parameters => [];

    /// <summary>
    /// Whether a row deleted at the source surfaces as a <see cref="ChangeOperation.Delete"/> change.
    /// False for readers that can only observe rows that still exist — a watermark scan, or a batch
    /// reload whose deletes are reconciled by the writer rather than reported by the reader.
    /// <para>
    /// Declared rather than inferred, so a UI can warn about it without string-matching Kind values.
    /// It defaults to false because that is the safe answer for a reader that has not thought about
    /// it: overstating the guarantee is what loses data.
    /// </para>
    /// </summary>
    bool DetectsDeletes => false;

    /// <param name="columnMappings">
    /// What this read is being asked to produce. A reader projects these columns rather than selecting
    /// everything, and applies each one's <see cref="ColumnMapping.Transform"/> — a SQL expression in
    /// the source's own dialect — into its SELECT list.
    /// <para>
    /// Empty means no projection was specified, and a reader should return whole rows. Staging and
    /// writing have always taken the mappings; the reader taking them too is what lets it stop asking
    /// the source for columns nobody mapped.
    /// </para>
    /// </param>
    Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);
}
