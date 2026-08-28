using DataSync.Api.Services;
using DataSync.Core.Config;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

/// <summary>
/// What a pass would run, without running it — see phase 37. Read-only by design: the place to change
/// a statement is the thing that generated it, and a preview that could be edited would invite the
/// question of what happens to the generator.
/// </summary>
[ApiController]
[Route("api/replications/{replicationName}/table-mappings/{mappingName}/preview")]
public sealed class PreviewController(PreviewService previewService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PreviewReport>> Get(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await previewService.BuildAsync(replicationName, mappingName, cancellationToken));
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
