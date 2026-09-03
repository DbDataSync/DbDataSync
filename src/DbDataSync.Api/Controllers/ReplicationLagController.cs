using DbDataSync.Api.Auth;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Every mapping's staleness in one call, plus the range across them — see phase 86.
/// <para>
/// A controller of its own on <c>api/replications/{name}/lag</c>, mirroring <see
/// cref="MetricsController"/> rather than hanging off <see cref="TableMappingsController"/>: the
/// figure is per-mapping but the question is about the replication, and the per-mapping endpoint
/// phase 85 shipped stays exactly where it is for a caller that wants one.
/// </para>
/// <para>
/// **One bulk call rather than N.** The two screens that ask this — the Monitoring tab and the
/// replications list — would otherwise fan out one request per mapping, and a replication with forty
/// mappings would make forty requests every thirty seconds to render a range over them. It also puts
/// the arithmetic in one place: two clients each reducing over their own fetches is two chances to
/// decide differently what an unreportable mapping contributes to a minimum.
/// </para>
/// </summary>
[ApiController]
[Route("api/replications/{replicationName}/lag")]
public sealed class ReplicationLagController(
    ConfigRepository configRepository, ReaderLagService lag) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<ReplicationLag> Get(
        string replicationName, CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        // A loop over state-database reads, since phase 87: no mapping in it opens a connection to a
        // source any more. The comment this replaces explained why the fan-out was sequential rather
        // than parallel — a replication with forty mappings would otherwise have been a load spike on
        // the database it was reporting about. That mitigation is no longer holding anything back;
        // the querying it was rationing is gone. Still sequential, now for no reason more interesting
        // than that a loop is the simplest thing that reads forty rows.
        var mappings = new Dictionary<string, MappingLag>(StringComparer.Ordinal);
        foreach (var mappingName in configRepository.ListTableMappings(replicationName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            mappings[mappingName] = MappingLag.From(lag.Describe(task, mappingName));
        }

        return Ok(ReplicationLag.From(mappings));
    }
}

/// <summary>
/// Every mapping's lag, keyed by mapping name, and the range across the ones that have a figure.
/// </summary>
/// <param name="Mappings">Every mapping the replication has, present whether or not it has a lag to
/// report — a mapping missing from this map would be indistinguishable from one that reports
/// nothing, and the screen has to say which.</param>
/// <param name="LowestLagMs">
/// The smallest effective lag among mappings that have one, or null when none does.
/// <para>
/// **A mapping with no figure is excluded, never counted as zero.** An unsupported reader and a
/// supported one that has not run yet both have nothing to say about how far behind they are;
/// folding either in as zero would report a replication as perfectly caught up on the strength of
/// the mappings that cannot answer, which is the reading an operator would most want to trust and
/// least be able to.
/// </para>
/// </param>
/// <param name="HighestLagMs">The largest effective lag, on the same rule. Null exactly when
/// <paramref name="LowestLagMs"/> is.</param>
/// <param name="RangeIncludesEstimates">
/// Whether any mapping that contributed to the range did so through <c>estimatedLagMs</c>.
/// <para>
/// Ranking needs one comparable number per mapping and <c>exactLagMs ?? estimatedLagMs</c> is that
/// number, which is a deliberate coalesce of two figures phase 85 went to some trouble to keep
/// apart. This flag is what keeps the coalesce honest: a range that any estimate went into carries
/// that estimate's error — the polling interval — and a screen that says so is showing a range,
/// while one that does not is showing a fact it does not have.
/// </para>
/// </param>
public sealed record ReplicationLag(
    IReadOnlyDictionary<string, MappingLag> Mappings,
    long? LowestLagMs,
    long? HighestLagMs,
    bool RangeIncludesEstimates)
{
    public static ReplicationLag From(IReadOnlyDictionary<string, MappingLag> mappings)
    {
        long? lowest = null;
        long? highest = null;
        var estimated = false;

        foreach (var mapping in mappings.Values)
        {
            // Unsupported readers never reach the coalesce at all. They cannot have a figure, and a
            // future reader kind that reported one while declaring itself unsupported would be a bug
            // worth ignoring here rather than averaging in.
            if (!mapping.Supported)
                continue;

            if (Effective(mapping) is not { } effective)
                continue;

            if (lowest is null || effective < lowest) lowest = effective;
            if (highest is null || effective > highest) highest = effective;
            if (mapping.ExactLagMs is null) estimated = true;
        }

        return new ReplicationLag(mappings, lowest, highest, estimated);
    }

    /// <summary>
    /// The one comparable number a mapping can be ranked by. The two fields are never both populated
    /// (see <see cref="MappingLag"/>), so this loses no information — it just picks whichever the
    /// mapping was able to answer in. Null is a real answer here: no figure, so no place in a range.
    /// </summary>
    private static long? Effective(MappingLag mapping) => mapping.ExactLagMs ?? mapping.EstimatedLagMs;
}
