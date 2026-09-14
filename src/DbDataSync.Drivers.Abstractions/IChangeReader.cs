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

    /// <summary>
    /// Whether this reader can state each row's true source order and time — see
    /// <see cref="ChangeOrdering"/>. False by default, the same "overstating is what loses data"
    /// reasoning as <see cref="DetectsDeletes"/>: a reader that has not thought about it says no.
    /// <para>
    /// Only <c>MsSqlCdcReader</c> declares this true (phase 132). It is what lets
    /// <c>Scd2Writer</c> process a key that staged more than one row in one pass — a real CDC
    /// outcome, not an edge case — without the two rows colliding on the same pass-wide surrogate key.
    /// </para>
    /// </summary>
    bool CapturesChangeOrder => false;

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
    /// that has never run, one whose source table changed, one a resync cleared, or one whose
    /// <paramref name="intent"/> asks for a floor or a latest position this pass has not yet resolved.
    /// A Backfill is always handed null.
    /// </param>
    /// <param name="intent">
    /// What this pass is asked to do — see <see cref="ReadIntent"/> and
    /// architecture/implementation/done/phase-101-readers-honour-the-read-intent.md. This, not the
    /// nullness of <paramref name="previousWatermark"/>, is what a reader branches on:
    /// <see cref="ReadIntent.InitialLoad"/> reads the source table itself (today's behaviour, now
    /// stated rather than inferred from a missing watermark); <see cref="ReadIntent.Changes"/> is the
    /// ordinary incremental read; <see cref="ReadIntent.ChangesFromEarliest"/> reads from the feed's
    /// surviving floor; <see cref="ReadIntent.ChangesFromLatest"/> adopts the current position without
    /// reading anything that came before it.
    /// <para>
    /// A reader declares which of <c>Changes</c>/<c>ChangesFromEarliest</c>/<c>ChangesFromLatest</c> it
    /// can honour via <see cref="IReadIntentDeclaring"/>; the caller never hands it one of those three it
    /// did not declare. <c>InitialLoad</c> is the one exception, per
    /// architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md: it is not declared by
    /// any reader and is never refused — every mapping can request it regardless of reader, which is
    /// what makes it universally available rather than a per-reader capability.
    /// </para>
    /// See <c>architecture/detailed-design.md</c> §4.1 for the rule and the per-reader table.
    /// <c>ChangeReaderFirstPassContractTests</c> fails until a new reader's supported intents are
    /// declared, or it is named as exempt from declaring them and why.
    /// </param>
    Task<ReadResult> ReadChangesAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        string? previousWatermark,
        ReadIntent intent,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> sourceColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken);
}
