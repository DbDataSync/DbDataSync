using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Opt-in capability for readers that can turn an <see cref="AutoSegment"/> into concrete
/// <see cref="RangeSegment"/>s by inspecting the source table's actual value range. Kept separate from
/// <see cref="IChangeReader"/> so a reader that has no meaningful notion of segmentation isn't forced
/// to implement (or throw from) a method it can't honour — callers query the capability with
/// <c>reader is ISegmentExpandingReader</c> rather than matching on Kind strings.
/// </summary>
public interface ISegmentExpandingReader
{
    /// <summary>
    /// Returns <paramref name="segments"/> with every <see cref="AutoSegment"/> replaced by its
    /// expansion and every other segment passed through unchanged, so the result is always free of
    /// <see cref="AutoSegment"/>. Called once, wherever segments are enqueued — never per unit of work.
    /// <para>
    /// <paramref name="sourceColumns"/>/<paramref name="mappingName"/> — phase 167V. An auto segment's
    /// column *type* (needed only to know how to bucket the value range this method samples live — see
    /// implementers) comes from the mapping's cache, the same way every cache-only run-time consumer
    /// reads it since phase 91, via <see cref="CachedMetadataLookup.RequireColumn"/>. It is never asked
    /// of the live catalog: sampling the column's actual value range has no cached substitute and stays
    /// live, but looking up its type does, and reading it live had no defense once checked — see
    /// <c>architecture/planning/todo/jdbc-metadata-catalog.md</c>.
    /// </para>
    /// </summary>
    /// <param name="columnMappings">
    /// Phase 192S: consulted only to find a matching (unqualified) <see cref="ColumnMapping.Transform"/>
    /// for the auto-segment's own column, so the sampled range agrees with what the target actually
    /// stores. A reader with no such concept ignores it.
    /// </param>
    /// <param name="relationships">This mapping's own declared <see cref="RelationshipConfig"/>s — phase
    /// 195S, the same list <see cref="IChangeReader.ReadChangesAsync"/> receives. Needed to render a
    /// <c>JOIN</c> when an <see cref="AutoSegment"/> names a relationship's own column; a reader with no
    /// such concept ignores it.</param>
    /// <param name="relationshipColumns">As <paramref name="sourceColumns"/>, keyed by
    /// <see cref="RelationshipConfig.Name"/> (case-insensitively) — phase 195S. An auto segment on a
    /// relationship's own column still needs its declared type (for
    /// <c>SegmentExpansion.BuildBuckets</c>'s own bucketing-kind classification), the same way a
    /// primary-sourced one already does via <paramref name="sourceColumns"/> — the live <c>MIN</c>/<c>MAX</c>
    /// sample this method takes has no equivalent "what kind of column is this" answer of its own.</param>
    Task<IReadOnlyList<BatchReloadSegment>> ExpandAutoSegmentsAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        IReadOnlyList<BatchReloadSegment> segments,
        IReadOnlyList<CachedColumn> sourceColumns,
        string mappingName,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyList<RelationshipConfig> relationships,
        IReadOnlyDictionary<string, IReadOnlyList<CachedColumn>> relationshipColumns,
        CancellationToken cancellationToken);
}
