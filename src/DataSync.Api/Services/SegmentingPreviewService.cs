using DataSync.Core.Config;
using DataSync.Scripting;

namespace DataSync.Api.Services;

/// <param name="Label">What to show for this candidate — the strategy's own name for it.</param>
/// <param name="Segment">What would actually be reloaded if it is chosen.</param>
/// <param name="Selected">Whether its box starts ticked.</param>
public sealed record SegmentCandidateDto(string Label, BatchReloadSegment Segment, bool Selected);

public sealed record SegmentingPreviewResult(
    IReadOnlyList<SegmentCandidateDto>? Candidates, string? Error, bool NotFound = false);

/// <summary>
/// Runs one segmenting strategy and hands back everything it proposes, for the Backfill form's
/// checklist — see phase 58.
/// <para>
/// **Every candidate, not just the selected ones.** The scheduled path filters to what the strategy
/// decided; a person looking at the form gets to see the whole proposal and disagree with it, which is
/// the difference between a checklist and an announcement.
/// </para>
/// <para>
/// Connections are opened **only for a strategy that says it needs them**. A DuckDB strategy is
/// previewed with none open at all, which is what makes previewing one on every keystroke of a picker
/// a safe thing to do.
/// </para>
/// </summary>
public sealed class SegmentingPreviewService(
    ConfigRepository configRepository,
    DriverConnectionFactory connections,
    CustomSegmentExpansion customSegments)
{
    /// <summary>Previews a strategy the replication has already saved, by name.</summary>
    public async Task<SegmentingPreviewResult> PreviewAsync(
        string replicationName, string mappingName, string strategyName, CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        TableMappingConfig mapping;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
            mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        }
        catch (FileNotFoundException)
        {
            return new SegmentingPreviewResult(null, null, NotFound: true);
        }

        var strategy = task.SegmentingStrategies
            .FirstOrDefault(s => string.Equals(s.Name, strategyName, StringComparison.OrdinalIgnoreCase));
        if (strategy is null)
            return new SegmentingPreviewResult(null, null, NotFound: true);

        return await RunAsync(task, mapping, mappingName, strategy, cancellationToken);
    }

    /// <summary>
    /// Previews a strategy that has not been saved — what the editor's Test button runs (phase 61).
    /// <para>
    /// The point of a Test button is finding out that a query is malformed while writing it, rather
    /// than the next time an unattended reload silently does nothing. Requiring a save first would
    /// mean committing a strategy in order to discover it does not work, which is the situation this
    /// is meant to remove.
    /// </para>
    /// <para>
    /// The strategy arrives in the request; everything else is read from config exactly as above, and
    /// the execution below is the same code either way. A strategy previewed here and the same
    /// strategy previewed once saved cannot disagree.
    /// </para>
    /// </summary>
    public async Task<SegmentingPreviewResult> PreviewAsync(
        string replicationName, string mappingName, SegmentingStrategyConfig strategy,
        CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        TableMappingConfig mapping;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
            mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        }
        catch (FileNotFoundException)
        {
            return new SegmentingPreviewResult(null, null, NotFound: true);
        }

        return await RunAsync(task, mapping, mappingName, strategy, cancellationToken);
    }

    private async Task<SegmentingPreviewResult> RunAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, string mappingName,
        SegmentingStrategyConfig strategy, CancellationToken cancellationToken)
    {
        if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
            return new SegmentingPreviewResult(
                null, $"Table mapping '{mappingName}' is not 1:1, which segmenting does not support in v1.");

        try
        {
            var candidates = strategy.RunsAgainstAConnection
                ? await WithConnectionsAsync(task, mapping, strategy, cancellationToken)
                : await customSegments.ProposeAsync(strategy, task, mapping, new SegmentingConnections(), cancellationToken);

            return new SegmentingPreviewResult(
                [.. candidates.Select(c => new SegmentCandidateDto(c.Label, c.Segment, c.Selected))], null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            // A strategy whose SQL does not compile, or returns the wrong columns, or cannot reach its
            // side, is the configuration being wrong — worth saying in the form rather than as a 500.
            return new SegmentingPreviewResult(null, ex.Message);
        }
    }

    private async Task<IReadOnlyList<Scripting.Abstractions.SegmentCandidate>> WithConnectionsAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, SegmentingStrategyConfig strategy,
        CancellationToken cancellationToken)
    {
        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);

        var (sourceConnection, _) = await connections.OpenAsync(source.ConnectionName, cancellationToken);
        await using (sourceConnection)
        {
            var (targetConnection, _) = await connections.OpenAsync(target.ConnectionName, cancellationToken);
            await using (targetConnection)
            {
                return await customSegments.ProposeAsync(
                    strategy, task, mapping,
                    new SegmentingConnections(sourceConnection, targetConnection), cancellationToken);
            }
        }
    }
}
