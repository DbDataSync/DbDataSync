using DataSync.Api.Services;
using DataSync.Core.Config;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

/// <summary>The Setup card's backend — see phase 25 §6. Both sides of a mapping in one GET so the
/// card can never show them disagreeing about the same round trip; apply re-plans first and executes
/// exactly what it just planned, never a client-held preview.</summary>
[ApiController]
[Route("api/replications/{replicationName}/table-mappings/{mappingName}/provisioning")]
public sealed class ProvisioningController(ProvisioningService provisioningService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ProvisioningPlanReport>> GetPlans(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await provisioningService.GetPlansAsync(replicationName, mappingName, cancellationToken));
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

    /// <summary>What each source column would become on the target — the column mapping editor's
    /// read path for the inferred type it shows on every row (phase 45 §3).</summary>
    [HttpGet("inferred-column-types")]
    public async Task<ActionResult<IReadOnlyList<InferredColumnType>>> GetInferredColumnTypes(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await provisioningService.GetInferredTargetTypesAsync(replicationName, mappingName, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // The source table not being there yet is normal while a mapping is being set up, and a
            // 500 would read as a broken server rather than "nothing to infer from".
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Named <c>provisioningAction</c>, not <c>action</c>. <c>{action}</c> is a reserved token in an
    /// MVC route template — it names the controller method rather than binding a segment — so a route
    /// declaring it never matches, and this endpoint was unreachable until phase 40 tried to use it.
    /// The URL is unchanged; only what the segment is called is.
    /// </summary>
    [HttpPost("{provisioningAction}/apply")]
    public async Task<ActionResult<ApplyResult>> Apply(
        string replicationName, string mappingName, string provisioningAction, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await provisioningService.ApplyAsync(replicationName, mappingName, provisioningAction, cancellationToken));
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
