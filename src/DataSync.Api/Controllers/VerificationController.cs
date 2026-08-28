using DataSync.Api.Configuration;
using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.State;
using DataSync.Verification;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

/// <summary>
/// Running a mapping's checks, and reading what they said — see phase 43.
/// <para>
/// A verification run goes through the same queue, lock and history as any other: it is a run, and
/// giving it a parallel mechanism would mean a second answer to "what is this replication doing".
/// What is different is what it produces — a file rather than rows at the target — and that file is
/// written by the TaskRunner straight to disk, never through this process's state channel.
/// </para>
/// </summary>
[ApiController]
[Route("api/replications/{replicationName}")]
public sealed class VerificationController(
    ConfigRepository configRepository,
    WorkQueueStore workQueue,
    ProcessSupervisor supervisor,
    VerificationResultStore results,
    ApiOptions options) : ControllerBase
{
    /// <summary>
    /// Queues this mapping's checks as one run. Operator-triggered, like phase 41's script test and
    /// phase 19's connection test — a check is something somebody asks for, and nothing has yet asked
    /// for it on a schedule.
    /// </summary>
    [HttpPost("mappings/{mappingName}/verify")]
    public IActionResult Verify(string replicationName, string mappingName)
    {
        TableMappingConfig mapping;
        try
        {
            mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = "Replication or table mapping not found." });
        }

        if (mapping.Verification.Count == 0)
        {
            return BadRequest(new
            {
                error = $"Table mapping '{mappingName}' has no verification checks configured.",
            });
        }

        var runId = workQueue.Enqueue(replicationName, RunKind.Verification, mappingName);
        supervisor.EnsureWorkerRunning(replicationName);
        return Accepted(new { runIds = new[] { runId } });
    }

    [HttpGet("verification-results")]
    public ActionResult<IReadOnlyList<VerificationResultRecord>> List(
        string replicationName, [FromQuery] string? mappingName = null, [FromQuery] int limit = 50) =>
        Ok(results.List(replicationName, mappingName, limit));

    /// <summary>
    /// The result itself, read back from the parquet the runner wrote.
    /// <para>
    /// The index is checked against the replication in the route before the file is opened: a result
    /// id is an integer somebody could guess, and a path out of the database is not a path this should
    /// serve without knowing it belongs where the caller says it does.
    /// </para>
    /// </summary>
    [HttpGet("verification-results/{id:long}")]
    public async Task<ActionResult<VerificationResult>> Get(
        string replicationName, long id, CancellationToken cancellationToken)
    {
        var record = results.Get(id);
        if (record is null || record.TaskName != replicationName)
            return NotFound();

        // Under the verification root and nowhere else. The path came out of this process's own
        // database, but a check on the way out costs nothing and is the kind of thing that stops
        // being true after a migration nobody thought about.
        var root = Path.GetFullPath(VerificationPaths.RootFor(options.StateDbPath));
        var path = Path.GetFullPath(record.ResultPath);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return NotFound();

        if (!System.IO.File.Exists(path))
        {
            return NotFound(new
            {
                error = "The result file is no longer on disk. Run the check again to produce a new one.",
            });
        }

        return Ok(await VerificationResultFile.ReadAsync(path, cancellationToken));
    }
}
