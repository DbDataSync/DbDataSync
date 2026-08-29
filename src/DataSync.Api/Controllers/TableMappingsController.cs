using DataSync.Api.Hubs;
using DataSync.Core.Config;
using DataSync.Core.Git;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/replications/{replicationName}/table-mappings")]
public sealed class TableMappingsController(
    ConfigRepository configRepository, GitAuthor author, IHubContext<RunHub> hub) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<string>> List(string replicationName) =>
        Ok(configRepository.ListTableMappings(replicationName));

    [HttpGet("{mappingName}")]
    public ActionResult<TableMappingConfig> Get(string replicationName, string mappingName)
    {
        try
        {
            return Ok(configRepository.LoadTableMapping(replicationName, mappingName));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{mappingName}")]
    public ActionResult<TableMappingConfig> Upsert(string replicationName, string mappingName, [FromBody] TableMappingConfig mapping)
    {
        mapping.Name = mappingName;
        try
        {
            return Ok(configRepository.SaveTableMapping(replicationName, mapping, author));
        }
        catch (FileNotFoundException)
        {
            // Saving a mapping resolves its endpoints against the replication, so the replication has
            // to exist. It always had to for the mapping to mean anything; now it is enforced.
            return NotFound(new { error = $"Replication '{replicationName}' was not found." });
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Creates one mapping per named table in a single request.
    /// <para>
    /// One call rather than N, because forty tables is forty round trips, forty git commits and forty
    /// chances to end up half-created with no record of where it stopped. Here a failure names the
    /// table it failed on and reports everything created before it, which is the difference between
    /// "retry the rest" and "work out what happened".
    /// </para>
    /// <para>
    /// Progress goes out over <see cref="RunHub"/> under the caller's batch id — the push channel the
    /// live run log already uses. A second mechanism invented for this one screen would be a second
    /// thing to keep working. The response still carries the full result, so a client that misses
    /// every event is late, not wrong.
    /// </para>
    /// </summary>
    [HttpPost("bulk")]
    public async Task<ActionResult<BulkCreateResult>> BulkCreate(
        string replicationName, [FromBody] BulkCreateRequest request, CancellationToken cancellationToken)
    {
        if (request.Tables.Count == 0)
            return BadRequest(new { error = "No tables were selected." });

        var existing = configRepository.ListTableMappings(replicationName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = new List<string>();
        var skipped = new List<string>();

        foreach (var table in request.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = $"{table.Schema}.{table.Table}";
            if (existing.Contains(name))
            {
                // Not an error: an operator who ticks every table on a replication that already maps
                // half of them means "map the rest", and failing the batch over it would make the
                // obvious gesture the wrong one.
                skipped.Add(name);
                continue;
            }

            try
            {
                configRepository.SaveTableMapping(replicationName, NewMapping(name, table), author);
            }
            catch (FileNotFoundException)
            {
                return NotFound(new { error = $"Replication '{replicationName}' was not found." });
            }
            catch (ConfigValidationException ex)
            {
                return BadRequest(new
                {
                    error = $"Creating '{name}' failed: {ex.Message}",
                    created,
                    skipped,
                });
            }

            existing.Add(name);
            created.Add(name);

            if (!string.IsNullOrEmpty(request.BatchId))
            {
                await hub.Clients.Group(request.BatchId).SendAsync(
                    "bulkMappingProgress",
                    new BulkCreateProgress(created.Count + skipped.Count, request.Tables.Count, name),
                    cancellationToken);
            }
        }

        return Ok(new BulkCreateResult(created, skipped));
    }

    /// <summary>
    /// Everything not stated is left unset, which is what makes this worth doing in bulk: the endpoints,
    /// the provisioning settings, the scripts and the hooks all inherit, so forty mappings created this
    /// way are forty mappings that follow the replication rather than forty copies of its settings.
    /// The target mirrors the source's schema and table — the same autofill the form does by hand.
    /// </summary>
    private static TableMappingConfig NewMapping(string name, BulkCreateTable table) => new()
    {
        Name = name,
        Sources = [new SourceTableSpec { Schema = table.Schema, Table = table.Table }],
        Targets = [new TableSpec { Schema = table.Schema, Table = table.Table }],
    };

    [HttpDelete("{mappingName}")]
    public IActionResult Delete(string replicationName, string mappingName)
    {
        configRepository.DeleteTableMapping(replicationName, mappingName, author);
        return NoContent();
    }
}

/// <param name="BatchId">The SignalR group to report progress on. Optional — a client that does not
/// want progress simply does not join one, and the response is the same either way.</param>
public sealed record BulkCreateRequest(IReadOnlyList<BulkCreateTable> Tables, string? BatchId);

public sealed record BulkCreateTable(string Schema, string Table);

public sealed record BulkCreateProgress(int Done, int Total, string Name);

/// <param name="Skipped">Tables that already had a mapping. Reported rather than silently dropped, so
/// "create 40" answering with 12 is explained on screen instead of looking like a failure.</param>
public sealed record BulkCreateResult(IReadOnlyList<string> Created, IReadOnlyList<string> Skipped);
