using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.Scripting;

/// <summary>
/// Substitutes each <see cref="CustomSegment"/> in a segment list with the candidates its strategy
/// selects — see phase 58.
/// <para>
/// The same shape <c>AutoSegment</c> expansion already has, and expanded at the same points for the
/// same reason: a marker is resolved against the world as it is *now*, not as it was when somebody
/// wrote the config down. A strategy that generates "the last three months" has to mean something
/// different in April than it did in March, and freezing its output would quietly make it mean the
/// same thing forever.
/// </para>
/// </summary>
public sealed class CustomSegmentExpansion(SegmentingStrategyRunner runner)
{
    /// <summary>
    /// <paramref name="segments"/> with every custom marker replaced in place by its strategy's
    /// selected candidates, and everything else passed through untouched.
    /// <para>
    /// Only the *selected* candidates. Unattended, a strategy proposing twelve months and flagging
    /// three means reload three — taking all twelve would turn a relative-date ETL into a full
    /// reload every pass.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<BatchReloadSegment>> ExpandAsync(
        ReplicationTaskConfig task,
        TableMappingConfig mapping,
        IReadOnlyList<BatchReloadSegment> segments,
        SegmentingConnections connections,
        CancellationToken cancellationToken)
    {
        if (!segments.OfType<CustomSegment>().Any())
            return segments;

        var expanded = new List<BatchReloadSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment is not CustomSegment custom)
            {
                expanded.Add(segment);
                continue;
            }

            var strategy = task.SegmentingStrategies
                .FirstOrDefault(s => string.Equals(s.Name, custom.StrategyName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Table mapping '{mapping.Name}' segments with strategy '{custom.StrategyName}', which " +
                    $"replication '{task.Name}' does not define.");

            var candidates = await RunAsync(strategy, task, mapping, connections, cancellationToken);
            expanded.AddRange(candidates.Where(c => c.Selected).Select(c => c.Segment));
        }

        return expanded;
    }

    /// <summary>Every candidate, selected or not — what a Bulk Load checklist renders, before an
    /// operator has said which of them to run.</summary>
    public async Task<IReadOnlyList<SegmentCandidate>> ProposeAsync(
        SegmentingStrategyConfig strategy,
        ReplicationTaskConfig task,
        TableMappingConfig mapping,
        SegmentingConnections connections,
        CancellationToken cancellationToken) =>
        await RunAsync(strategy, task, mapping, connections, cancellationToken);

    private async Task<IReadOnlyList<SegmentCandidate>> RunAsync(
        SegmentingStrategyConfig strategy,
        ReplicationTaskConfig task,
        TableMappingConfig mapping,
        SegmentingConnections connections,
        CancellationToken cancellationToken)
    {
        var context = new SegmentingContext(
            EndpointResolution.ResolveSource(task, mapping.Sources[0]),
            EndpointResolution.ResolveTarget(task, mapping.Targets[0]),
            mapping.ColumnMappings,
            // Resolved only for the one kind that can look at it. The SQL paths express what they need
            // in their own query, so paying for a catalog round trip on their behalf would be a cost
            // with no reader.
            strategy.Kind == SegmentingStrategyKind.Script ? connections.SourceColumns : [],
            connections.Source,
            connections.Target,
            connections.SourceDialectOrNull,
            new ScriptParameters(strategy.Parameters));

        return await runner.RunAsync(strategy, context, connections.Source, connections.Target, cancellationToken);
    }
}

/// <summary>
/// What a strategy may be given to work with. Bundled rather than passed as four arguments because
/// every one of them is optional in some caller — a Bulk Load preview of a DuckDB strategy has none of
/// them, and correctly does not need them.
/// </summary>
/// <param name="SourceColumnsOrNull">Only consulted by a C# strategy; see the note at its one use.</param>
/// <param name="SourceDialectOrNull">
/// Null is a real answer here, not a gap: a DuckDB strategy generates no SQL for anybody's engine, so
/// resolving a dialect on its behalf would be work with no reader — and would fail outright for a
/// driver that names none.
/// </param>
public sealed record SegmentingConnections(
    DbConnection? Source = null,
    DbConnection? Target = null,
    IReadOnlyList<ColumnMetadata>? SourceColumnsOrNull = null,
    IScriptDialect? SourceDialectOrNull = null)
{
    public IReadOnlyList<ColumnMetadata> SourceColumns => SourceColumnsOrNull ?? [];

}
