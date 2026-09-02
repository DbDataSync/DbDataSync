using DataSync.Api.Hubs;
using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/replications/{replicationName}/table-mappings")]
public sealed class TableMappingsController(
    ConfigRepository configRepository, CurrentUser currentUser, IHubContext<RunHub> hub,
    ParameterCheck parameterCheck, ReaderLagService lag,
    MappingMetadataService mappingMetadata) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<IReadOnlyList<string>> List(string replicationName) =>
        Ok(configRepository.ListTableMappings(replicationName));

    [Authorize(Policies.Viewer)]
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

    /// <summary>
    /// How far behind its source this mapping is — see phase 85.
    /// <para>
    /// Beside the mapping rather than under a new endpoint of its own, and pulled rather than pushed,
    /// on the same call <c>{name}/status</c> is made on: this is a figure somebody reads when they go
    /// looking, and the case where somebody is watching a replication move is a live run, which the
    /// run hub already covers.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{mappingName}/lag")]
    public ActionResult<MappingLag> Lag(string replicationName, string mappingName)
    {
        ReplicationTaskConfig task;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        if (!configRepository.ListTableMappings(replicationName).Contains(mappingName, StringComparer.Ordinal))
            return NotFound();

        return Ok(MappingLag.From(lag.Describe(task, mappingName)));
    }

    [HttpPut("{mappingName}")]
    public ActionResult<TableMappingConfig> Upsert(string replicationName, string mappingName, [FromBody] TableMappingConfig mapping)
    {
        mapping.Name = mappingName;
        try
        {
            // Beside the endpoint and hook checks SaveTableMapping already makes, and for the same
            // reason: a per-stage override naming a Kind or a setting that cannot work is caught while
            // somebody is still looking at the edit, not on the first pass (phase 68).
            parameterCheck.ThrowIfInvalid(configRepository.LoadReplicationTask(replicationName), mapping);

            // What a save may do to the cached column metadata, which is very little — see
            // MappingMetadataCapture. A mapping being created has nothing stored to reconcile against.
            TableMappingConfig? stored = null;
            try
            {
                stored = configRepository.LoadTableMapping(replicationName, mappingName);
            }
            catch (FileNotFoundException) { }
            MappingMetadataCapture.Apply(mapping, stored, DateTime.UtcNow);

            return Ok(configRepository.SaveTableMapping(replicationName, mapping, currentUser.Author));
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
    /// Re-reads both sides' catalogs and overwrites this mapping's cached column metadata — see
    /// <see cref="CachedColumn"/> for why the cache is only ever written on request.
    /// <para>
    /// A POST rather than a GET, and not folded into <see cref="Upsert"/>: it opens connections to
    /// the source and target, writes config and makes a git commit. None of that should happen
    /// because something refetched.
    /// </para>
    /// </summary>
    [HttpPost("{mappingName}/refresh-metadata")]
    public async Task<ActionResult<MetadataRefreshResult>> RefreshMetadata(
        string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await mappingMetadata.RefreshAsync(
                replicationName, mappingName, currentUser.Author, cancellationToken));
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
                configRepository.SaveTableMapping(replicationName, NewMapping(name, table), currentUser.Author);
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
        configRepository.DeleteTableMapping(replicationName, mappingName, currentUser.Author);
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

/// <summary>
/// A mapping's staleness, over the wire — see <c>ReaderLagService</c> for what each figure is and how
/// it is arrived at.
/// <para>
/// **Three separately named fields rather than one number and a unit.** They are not three encodings
/// of the same measurement: <c>exactLagMs</c> is what the source engine says, <c>versionsBehind</c> is
/// a count in a unit only the source's own write rate gives meaning to, and <c>estimatedLagMs</c> is
/// reconstructed from how often this system happened to look. A single <c>lagMs</c> with a
/// <c>kind</c> beside it would be one <c>if</c> away, in every consumer ever written against it, from
/// charting an estimate on the same axis as a fact — and the estimate's error is the poll interval,
/// which is a property of this system's configuration rather than of the replication. Naming the
/// estimate is what makes ignoring the distinction a decision instead of an accident.
/// </para>
/// <para>
/// **<c>exactLagMs</c> and <c>estimatedLagMs</c> are never both populated**, and which one arrives is
/// the answer to "how good is this figure", not an implementation detail: a Change Tracking mapping
/// resolves through <c>dm_tran_commit_table</c> into the first field while its version is recent
/// enough for that DMV to hold, and drops into the second once it is not. A consumer reading only
/// <c>exactLagMs</c> is correct and gets nulls; one reading only <c>estimatedLagMs</c> is correct and
/// gets nulls; one that coalesces them has said, in writing, that it does not mind.
/// </para>
/// </summary>
/// <param name="Supported">False when the mechanism has no lag to report at all, as opposed to having
/// none yet. A consumer should render those differently: "not applicable", never a dash that reads
/// like zero.</param>
/// <param name="ExactLagMs">
/// Milliseconds, stated by the source engine at both ends — CDC always, Change Tracking whenever its
/// version is recent enough for <c>dm_tran_commit_table</c> to place. Milliseconds rather than a
/// serialised <c>TimeSpan</c>, matching <c>TaskRuns</c>' own
/// <c>ReaderLifetimeMs</c>/<c>WriterDurationMs</c> — a number a client can do arithmetic on without
/// parsing <c>"00:04:13.5"</c> first.
/// </param>
/// <param name="VersionsBehind">Change Tracking only, and exact. A count, never milliseconds.</param>
/// <param name="EstimatedLagMs">
/// Change Tracking's fallback, an estimate whose precision is the gate's polling interval, filled
/// only when the engine would not place the version. Null when the polling history cannot place it
/// either — which is a real state on a fresh install, and is not zero.
/// </param>
public sealed record MappingLag(
    string ReaderKind,
    bool Supported,
    long? ExactLagMs,
    long? VersionsBehind,
    long? EstimatedLagMs,
    /// <summary>
    /// Where the source had got to, at the moment the figures above are measured against — the exact
    /// <c>ChangeCheckHistory</c> row this mapping's own comparison used, never the clock. See
    /// <see cref="ReaderLag.AsOfUtc"/>, whose choice between the row's two timestamps this carries
    /// unchanged.
    /// <para>
    /// A timestamp rather than milliseconds, unlike the two figures beside it. Those are durations a
    /// client does arithmetic on; this is an instant a client renders, and an offset from an
    /// unstated origin would be the harder of the two to get right.
    /// </para>
    /// </summary>
    DateTimeOffset? AsOfUtc = null)
{
    public static MappingLag From(ReaderLag lag) => new(
        lag.ReaderKind,
        lag.Supported,
        (long?)lag.ExactLag?.TotalMilliseconds,
        lag.ExactVersionsBehind,
        (long?)lag.EstimatedLag?.TotalMilliseconds,
        lag.AsOfUtc);
}
