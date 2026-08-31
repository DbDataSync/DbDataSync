using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

/// <summary>The Setup card's backend — see phase 25 §6. Both sides of a mapping in one GET so the
/// card can never show them disagreeing about the same round trip; apply re-plans first and executes
/// exactly what it just planned, never a client-held preview.</summary>
[ApiController]
[Route("api/replications/{replicationName}/table-mappings/{mappingName}/provisioning")]
public sealed class ProvisioningController(ProvisioningService provisioningService) : ControllerBase
{
    [Authorize(Policies.Viewer)]
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
    [Authorize(Policies.Viewer)]
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
    /// What the SCD Type 2 writer's natural key would be for this mapping if nobody stated one — the
    /// mapping editor's Pipeline tab shows it rather than deriving it invisibly at run time (phase 68).
    /// <para>
    /// On this controller rather than a new one because it is the same shape as
    /// <c>inferred-column-types</c> beside it: a per-mapping question answered by reading the source's
    /// columns, which is what this service is.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("inferred-natural-key")]
    public async Task<ActionResult<InferredNaturalKey>> GetInferredNaturalKey(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await provisioningService.GetInferredNaturalKeyAsync(replicationName, mappingName, cancellationToken));
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
            // A source table that is not there yet is normal while a mapping is being set up.
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
