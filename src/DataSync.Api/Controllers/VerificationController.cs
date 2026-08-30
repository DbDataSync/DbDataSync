using DataSync.Api.Configuration;
using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.State;
using DataSync.Verification;
using DataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
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

    [Authorize(Policies.Viewer)]
    [HttpGet("verification-results")]
    public ActionResult<IReadOnlyList<VerificationResultRecord>> List(
        string replicationName, [FromQuery] string? mappingName = null, [FromQuery] int limit = 50) =>
        Ok(results.List(replicationName, mappingName, limit));

    /// <summary>
    /// One page of a result, read back from the parquet the runner wrote.
    /// <para>
    /// A page, not the result. A check over a large table produces a row per group — millions of them
    /// — and this used to return every one: the API allocated the lot, the response was tens of
    /// megabytes of JSON, and the browser rendered a DOM node per cell. It locked the tab up for
    /// minutes and crashed some of them, to show a screenful.
    /// </para>
    /// <para>
    /// The index is checked against the replication in the route before the file is opened: a result
    /// id is an integer somebody could guess, and a path out of the database is not a path this should
    /// serve without knowing it belongs where the caller says it does.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("verification-results/{id:long}")]
    public async Task<ActionResult<VerificationResultPage>> Get(
        string replicationName,
        long id,
        CancellationToken cancellationToken,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        [FromQuery] bool differingOnly = false)
    {
        if (ResolveFile(replicationName, id) is not { } path)
            return NotFound();

        if (!System.IO.File.Exists(path))
        {
            return NotFound(new
            {
                error = "The result file is no longer on disk. Run the check again to produce a new one.",
            });
        }

        return Ok(await VerificationResultQuery.ReadPageAsync(path, offset, limit, differingOnly, cancellationToken));
    }

    /// <summary>
    /// Throws away one result — its index row and its file.
    /// <para>
    /// The index row goes first. A row pointing at a file that is gone is a broken link an operator
    /// has to work out; a file with no row pointing at it is disk somebody can reclaim, and the next
    /// run of the same check overwrites it anyway.
    /// </para>
    /// </summary>
    [HttpDelete("verification-results/{id:long}")]
    public IActionResult Delete(string replicationName, long id)
    {
        if (ResolveFile(replicationName, id) is not { } path)
            return NotFound();

        results.Delete(id);

        try
        {
            System.IO.File.Delete(path);
        }
        catch (IOException)
        {
            // The result is forgotten either way. A file still held open by something is a stale file,
            // not a failed delete, and reporting it as one would leave the operator with a row they
            // cannot remove.
        }

        return NoContent();
    }

    /// <summary>
    /// The result's file, or null when there is no such result for this replication.
    /// <para>
    /// Under the verification root and nowhere else. The path came out of this process's own database,
    /// but a check on the way out costs nothing and is the kind of thing that stops being true after a
    /// migration nobody thought about.
    /// </para>
    /// </summary>
    private string? ResolveFile(string replicationName, long id)
    {
        var record = results.Get(id);
        if (record is null || record.TaskName != replicationName)
            return null;

        var root = Path.GetFullPath(VerificationPaths.RootFor(options.StateDbPath));
        var path = Path.GetFullPath(record.ResultPath);
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path : null;
    }
}
