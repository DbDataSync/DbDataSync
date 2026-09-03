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
    /// </summary>
    Task<IReadOnlyList<BatchReloadSegment>> ExpandAutoSegmentsAsync(
        DbConnection sourceConnection,
        SourceTableRef source,
        IReadOnlyList<BatchReloadSegment> segments,
        CancellationToken cancellationToken);
}
