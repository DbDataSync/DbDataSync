using DataSync.Api.Services;
using DataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/connections/{connectionName}/metadata")]
public sealed class MetadataController(MetadataService metadataService) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet("databases")]
    public async Task<IActionResult> ListDatabases(string connectionName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await metadataService.ListDatabasesAsync(connectionName, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [Authorize(Policies.Viewer)]
    [HttpGet("databases/{database}/tables")]
    public async Task<IActionResult> ListTables(string connectionName, string database, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await metadataService.ListTablesAsync(connectionName, database, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [Authorize(Policies.Viewer)]
    [HttpGet("databases/{database}/schemas/{schema}/tables/{table}/columns")]
    public async Task<IActionResult> ListColumns(
        string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await metadataService.ListColumnsAsync(connectionName, database, schema, table, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // A table that is not there is a 404, not a fault. The UI asks about tables that may not
            // exist yet (phase 40), and a 500 there reads as "the server is broken" rather than
            // "there is no such table".
            return NotFound(new { error = ex.Message });
        }
    }
}
