using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Scripting.Abstractions;

/// <summary>
/// Proposes how a table divides for a reload — see phase 58.
/// <para>
/// A peer of <see cref="IVerificationQueryBuilder"/> and <see cref="ISourceQueryBuilder"/>, and for the
/// same reason all three exist: some answers cannot be written down in advance. <c>Auto</c> splits a
/// column's value range into evenly-sized buckets, which is the right answer right up until the
/// boundaries need to mean something — a calendar month is not a fixed number of days, and no amount of
/// even division produces one.
/// </para>
/// <para>
/// **Strictly more powerful than either SQL path**, because it is handed both connections and both
/// sides' metadata: a strategy can read the source, read a control table on the target, or read
/// neither and compute from the calendar alone.
/// </para>
/// </summary>
public interface ISegmentingStrategy
{
    /// <summary>
    /// The candidates this strategy proposes, in the order an operator should see them.
    /// <para>
    /// Returning candidates rather than segments is the point: which of them actually run is decided
    /// afterwards, by an operator ticking boxes or by a scheduled pass taking the selected ones.
    /// </para>
    /// </summary>
    IReadOnlyList<SegmentCandidate> ProposeSegments(SegmentingContext context);
}

/// <param name="Segment">
/// What would actually be reloaded. A <see cref="RangeSegment"/> in every realistic case, carrying the
/// strategy's own label so the run history says <c>"2024-03"</c> rather than a pair of bounds.
/// </param>
/// <param name="Selected">
/// Whether this candidate should run without anyone saying so.
/// <para>
/// **This is the field that makes a scheduled, self-updating reload possible**, not a UI convenience.
/// Interactively it decides which boxes start ticked. Unattended it decides the whole pass: a strategy
/// that flags "the last three months", recomputed against today on every run, is a relative-date ETL
/// with no new scheduling concept behind it.
/// </para>
/// <para>
/// It lives here, on the proposal, and deliberately not on <see cref="BatchReloadSegment"/>. Once a
/// candidate has been turned into work, the flag has done its job — a segment being reloaded does not
/// need to remember that something once suggested it.
/// </para>
/// </param>
public sealed record SegmentCandidate(BatchReloadSegment Segment, bool Selected)
{
    /// <summary>What to show for this candidate. The segment's own description, which for a labelled
    /// range is the label.</summary>
    public string Label => Segment.Describe();
}

/// <param name="Source">The source table this mapping reads.</param>
/// <param name="Target">The target table it writes.</param>
/// <param name="SourceColumns">The source's columns as its catalog reports them — the metadata a
/// strategy that has to look before it can answer exists to be able to look at.</param>
/// <param name="SourceConnection">
/// Open, and the strategy's to query but not to dispose. Null only when the caller could not open it,
/// which is the case a DuckDB-authored strategy never notices because it never asks.
/// </param>
/// <param name="TargetConnection">
/// The same, for the target — a control table saying which periods need reloading lives here, not at
/// the source.
/// </param>
/// <param name="SourceDialect">
/// How to quote and parameterise for the source, for a strategy that generates SQL. Null when the
/// source driver names no dialect, and null when the caller had no reason to resolve one — a strategy
/// that needs it should say so rather than assume it is there.
/// </param>
public sealed record SegmentingContext(
    SourceTableRef Source,
    TableRef Target,
    IReadOnlyList<ColumnMapping> ColumnMappings,
    IReadOnlyList<ColumnMetadata> SourceColumns,
    System.Data.Common.DbConnection? SourceConnection,
    System.Data.Common.DbConnection? TargetConnection,
    IScriptDialect? SourceDialect,
    ScriptParameters Parameters);
