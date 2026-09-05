using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// The replication-wide Provisioning tab's backend — phase 105. Distinct from
/// <see cref="ProvisioningController"/> beside it, which is the per-mapping Setup card: this aggregates
/// every table mapping's plan into one page, grouped by server and deduplicated, rather than answering
/// for one mapping at a time.
/// </summary>
[ApiController]
[Route("api/replications/{replicationName}/provisioning")]
public sealed class ReplicationProvisioningController(ProvisioningService provisioningService) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public async Task<ActionResult<ReplicationProvisioningPlan>> GetPlan(
        string replicationName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await provisioningService.GetReplicationPlanAsync(replicationName, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Applies exactly the selected steps, re-planning first — see
    /// <see cref="ProvisioningService.ApplyReplicationPlanAsync"/> for why. Never a 4xx for a step id the
    /// fresh plan no longer contains: that comes back as an ordinary
    /// <see cref="ProvisioningStepOutcome.NoLongerNeeded"/> result, not a request failure.
    /// </summary>
    // No explicit policy, matching ProvisioningController.Apply beside it — see that controller's
    // routing test doc comment for why: the fallback policy (any authenticated user) is what every
    // Apply endpoint in this app runs under today.
    [HttpPost("apply")]
    public async Task<ActionResult<ReplicationProvisioningApplyResult>> Apply(
        string replicationName, [FromBody] ApplyReplicationProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await provisioningService.ApplyReplicationPlanAsync(
                replicationName, request.StepIds, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed record ApplyReplicationProvisioningRequest(IReadOnlyList<string> StepIds);
