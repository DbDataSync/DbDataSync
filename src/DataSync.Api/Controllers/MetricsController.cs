using DataSync.State;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

/// <summary>
/// Aggregates over a replication's run history — see phase 36. Everything here is a query over
/// <c>TaskRuns</c>, which has held the data since phase 5; what was missing was the question.
/// <para>
/// Pull, not push. SignalR already exists and pushing these down it would be easy, but a 24-hour
/// aggregate is not something anyone watches change — and the case where someone *is* watching is a
/// live run, which the run hub already covers.
/// </para>
/// </summary>
[ApiController]
[Route("api/replications/{replicationName}/metrics")]
public sealed class MetricsController(RunMetricsStore metrics) : ControllerBase
{
    /// <param name="window">
    /// <c>1h</c>, <c>24h</c> or <c>7d</c> — "is it failing now" and "did it fail this week" are
    /// different questions, and a fixed window answers only one.
    /// </param>
    /// <param name="kind">
    /// Defaults to <c>Primary</c>. A backfill moving ten million rows next to incremental passes
    /// moving hundreds dominates every total it is added to, so the two are asked about separately
    /// rather than summed into a number that describes neither.
    /// </param>
    [HttpGet]
    public ActionResult<RunMetrics> Get(
        string replicationName,
        [FromQuery] string window = "24h",
        [FromQuery] RunKind? kind = RunKind.Primary,
        [FromQuery] int buckets = 24)
    {
        if (!TryParseWindow(window, out var span))
            return BadRequest(new { error = $"Unrecognised window '{window}'. Use 1h, 24h or 7d." });

        var now = DateTimeOffset.UtcNow;
        return Ok(metrics.Get(replicationName, now - span, now, kind, buckets));
    }

    /// <summary>
    /// A small closed set rather than a general duration parser: these are the three the card offers,
    /// and accepting arbitrary spans would invite a 90-day window against an unbounded table with no
    /// retention policy behind it.
    /// </summary>
    private static bool TryParseWindow(string window, out TimeSpan span)
    {
        span = window switch
        {
            "1h" => TimeSpan.FromHours(1),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            _ => TimeSpan.Zero,
        };
        return span != TimeSpan.Zero;
    }
}
