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

    [HttpPost("{action}/apply")]
    public async Task<ActionResult<ApplyResult>> Apply(
        string replicationName, string mappingName, string action, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await provisioningService.ApplyAsync(replicationName, mappingName, action, cancellationToken));
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
