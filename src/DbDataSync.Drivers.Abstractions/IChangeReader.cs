using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

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
    /// <param name="mappingName">The table mapping this read belongs to, named as an operator would —
    /// for a <see cref="MetadataNotCachedException"/> that says which mapping needs Refresh metadata,
    /// not merely that some mapping does.</param>
    /// <param name="sourceColumns">
    /// The source's shape as of the mapping's last save or explicit refresh (phase 90) —
    /// <see cref="TableMappingConfig.SourceColumns"/>, verbatim. A reader that needs a specific column's
    /// type or key-ness (a watermark column, a segment column, a key/non-key split) looks it up here and
    /// throws <see cref="MetadataNotCachedException"/> if it's missing, rather than querying
    /// <see cref="ITableCatalog"/> or the driver live — see phase 91. Readers with no such need (a
    /// change-log mechanism that carries its own row shape, a scripted query) ignore this parameter
    /// entirely.
    /// </param>
    /// <param name="previousWatermark">
    /// Where the last successful <c>Primary</c> pass got to, or null when there is none — a mapping
    /// that has never run, one whose source table changed, or one a resync cleared. A Backfill is
    /// always handed null.
    /// <para>
    /// **Null means read the whole source table, not read nothing.** A reader over a change feed —
    /// Change Tracking, CDC, a trigger's shadow table — only knows about changes since that feed was
    /// switched on, so a source table that already held rows would start permanently and silently
    /// half-replicated if the first pass read the feed. The failure is invisible: the pass succeeds
    /// and the counts look plausible. Readers with nothing to be behind (a reload, a query source)
    /// are exempt and say so.
    /// <br/>
    /// See <c>architecture/detailed-design.md</c> §4.1 for the rule and the per-reader table.
    /// <c>ChangeReaderFirstPassContractTests</c> fails until a new reader is declared as either
    /// following it or exempt from it.
    /// </para>
    /// </param>
    Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> sourceColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);
}
