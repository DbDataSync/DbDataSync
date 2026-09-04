using DbDataSync.Api.Hubs;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Api.Auth;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace DbDataSync.Api.Controllers;

[ApiController]
[Route("api/replications/{replicationName}/table-mappings")]
public sealed class TableMappingsController(
    ConfigRepository configRepository, CurrentUser currentUser, IHubContext<RunHub> hub,
    ParameterCheck parameterCheck, ReaderLagService lag,
    MappingMetadataService mappingMetadata, MappingColumnReader columnReader,
    ChangeWatermarkStore watermarks, RunLockStore runLocks, DriverRegistry driverRegistry) : ControllerBase
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

    /// <summary>
    /// This mapping's stored intent and hold, resolved the way a caller actually wants them: the
    /// mapping's own stored row when it has one, and the configured default when it does not — a
    /// mapping that has never run is not "unknown", it is going to do exactly what
    /// <see cref="ReadIntentResolution"/> says on its first pass. See phase 100.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{mappingName}/read-state")]
    public ActionResult<MappingReadStateDto> GetReadState(string replicationName, string mappingName)
    {
        ReplicationTaskConfig task;
        TableMappingConfig mapping;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
            mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        string sourceTable;
        try
        {
            sourceTable = ResolveWatermarkKey(task, mapping);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var stored = watermarks.GetReadState(replicationName, mappingName, sourceTable);
        return Ok(new MappingReadStateDto(
            stored?.Intent ?? ReadIntentResolution.Default(task, mapping),
            stored?.Hold ?? ReadHold.None,
            stored?.Watermark,
            stored?.WatermarkTimeUtc));
    }

    /// <summary>
    /// Sets this mapping's intent and clears or sets its hold, in the one call — an operator recovering
    /// from a hold is choosing both at once, and two separate requests would leave a window where the
    /// hold has already cleared but the old intent is still what the next pass would honour.
    /// <para>
    /// **Refused while a run holds this mapping's lock.** An intent cannot move under a pass that is
    /// already acting on it — the same pass would read the value it was dispatched with regardless, and
    /// changing the stored row underneath it would only make the two disagree about what happened.
    /// </para>
    /// <para>
    /// No validation against what the mapping's reader can actually honour — that needs the capability
    /// interface phase 101 adds. This will happily store <c>ChangesFromEarliest</c> against a reader
    /// that has no such thing; today nothing consumes it, and once phase 101 does, it is the one that
    /// refuses to silently downgrade it.
    /// </para>
    /// </summary>
    [HttpPost("{mappingName}/read-state")]
    public ActionResult<MappingReadStateDto> SetReadState(
        string replicationName, string mappingName, [FromBody] SetMappingReadStateRequest request)
    {
        ReplicationTaskConfig task;
        TableMappingConfig mapping;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
            mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        if (runLocks.IsLocked(replicationName, RunKind.Primary, mappingName)
            || runLocks.IsLocked(replicationName, RunKind.Backfill, mappingName))
        {
            return Conflict(new
            {
                error = $"Table mapping '{mappingName}' has a run in progress; its read intent cannot " +
                    "change until that pass finishes.",
            });
        }

        string sourceTable;
        try
        {
            sourceTable = ResolveWatermarkKey(task, mapping);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        watermarks.SetReadIntentAndHold(replicationName, mappingName, sourceTable, request.Intent, request.Hold);

        var stored = watermarks.GetReadState(replicationName, mappingName, sourceTable)!;
        return Ok(new MappingReadStateDto(stored.Intent, stored.Hold, stored.Watermark, stored.WatermarkTimeUtc));
    }

    /// <summary>
    /// The <c>ChangeWatermarks</c> key for a mapping's own source — the same resolution
    /// <c>ResyncService</c> and <c>ChangeSourceResolver</c> each make on their own, restated here rather
    /// than shared because neither of those fits this caller: <c>ResyncService</c> only runs once a run
    /// has already failed, and <c>ChangeSourceResolver</c> answers null for a reader with no
    /// database-wide counter to gate on, which is not "no key" — the Watermark reader has a perfectly
    /// good key and simply is not gated.
    /// </summary>
    private SourceTableRef ResolveSourceTable(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        if (mapping.Sources.Count != 1)
            throw new InvalidOperationException(
                $"Table mapping '{mapping.Name}' has {mapping.Sources.Count} sources; read intent supports 1:1 mappings.");

        return EndpointResolution.ResolveSource(task, mapping.Sources[0]);
    }

    private string ResolveWatermarkKey(ReplicationTaskConfig task, TableMappingConfig mapping)
    {
        var source = ResolveSourceTable(task, mapping);
        var connection = configRepository.LoadConnection(source.ConnectionName);
        var dialect = (driverRegistry.Get(connection.DriverType) as IDialectProvider)?.Dialect
            ?? throw new InvalidOperationException(
                $"The '{connection.DriverType}' driver does not name a SQL dialect.");

        return WatermarkKey.Build(source, dialect);
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
    /// <para>
    /// **Each mapping is introspected as it is created** — phase 95. A mapping created here used to
    /// arrive with an empty metadata cache *and* no column mappings, which are two independent
    /// reasons it could not run: phase 91 made an empty cache throw, and staging has always thrown on
    /// an empty <c>ColumnMappings</c>. Both are filled from one read of each side, before the single
    /// save, so forty tables is still forty commits and not eighty. What a side that cannot be read
    /// does instead is <see cref="CaptureAsync"/>'s subject.
    /// </para>
    /// </summary>
    [HttpPost("bulk")]
    public async Task<ActionResult<BulkCreateResult>> BulkCreate(
        string replicationName, [FromBody] BulkCreateRequest request, CancellationToken cancellationToken)
    {
        if (request.Tables.Count == 0)
            return BadRequest(new { error = "No tables were selected." });

        ReplicationTaskConfig task;
        try
        {
            // Loaded once, up front, because capture has to resolve each side's endpoint against it —
            // a bulk-created mapping states only its tables, so the replication is what says where
            // they live. Once, not per table: it does not change under a batch it is the subject of.
            task = configRepository.LoadReplicationTask(replicationName);
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = $"Replication '{replicationName}' was not found." });
        }

        var existing = configRepository.ListTableMappings(replicationName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = new List<string>();
        var skipped = new List<string>();
        var notes = new List<BulkCreateNote>();

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
                var mapping = NewMapping(name, table);

                await ReportAsync(request.BatchId, BulkCreateProgress.Reading(
                    created.Count + skipped.Count, request.Tables.Count, name), cancellationToken);
                var captured = await CaptureAsync(task, mapping, cancellationToken);

                configRepository.SaveTableMapping(replicationName, mapping, currentUser.Author);

                // Only once it exists. A note about a mapping the save then rejected would name a
                // mapping the operator cannot go and look at.
                notes.AddRange(captured);
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

            await ReportAsync(request.BatchId, BulkCreateProgress.Created(
                created.Count + skipped.Count, request.Tables.Count, name), cancellationToken);
        }

        return Ok(new BulkCreateResult(created, skipped, notes));
    }

    /// <summary>
    /// Reads both of a mapping's sides and fills in everything a mapping needs to be runnable that
    /// nobody typed: the metadata cache phase 91's readers and writers run from, and the column
    /// mappings staging refuses to work without. Returns what it could not do, in the operator's
    /// terms; it never throws for a side it could not read.
    /// <para>
    /// **A side that cannot be read is never fatal here.** Two of the three ways it happens are not
    /// faults at all: a target that provisioning has yet to create genuinely is not in the catalog,
    /// and that is the ordinary case for a mapping being created from a source table. The third — a
    /// source that could not be read — leaves the mapping exactly as this endpoint left every mapping
    /// before this phase, which is to say uncaptured and needing one Refresh, so failing the batch
    /// over it would withhold a mapping that used to be created without complaint.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<BulkCreateNote>> CaptureAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, CancellationToken cancellationToken)
    {
        var source = await columnReader.ReadAsync(
            EndpointResolution.ResolveSource(task, mapping.Sources[0]), cancellationToken);
        var target = await columnReader.ReadAsync(
            EndpointResolution.ResolveTarget(task, mapping.Targets[0]), cancellationToken);

        if (source.Shape is not null) mapping.SourceColumns = source.Shape;
        if (target.Shape is not null) mapping.TargetColumns = target.Shape;
        if (source.Shape is not null || target.Shape is not null)
            mapping.ColumnsCapturedUtc = DateTime.UtcNow;

        // Same rule as the editor's "Auto-map by name" — see ColumnAutoMap. A target that is not
        // there yet maps every source column, which is also what provisioning will then create.
        mapping.ColumnMappings = ColumnAutoMap.Between(source.Shape, target.Shape);

        return
        [
            .. Note(mapping.Name, "source", source,
                "the source table is not in the catalog, so nothing was captured and no columns were mapped"),
            .. Note(mapping.Name, "target", target,
                "the target table does not exist yet — its shape is captured when provisioning creates it"),
        ];
    }

    /// <summary>Nothing at all for a side that read cleanly, which is what makes the list worth
    /// showing: it is the exceptions, not a row per table.</summary>
    private static IEnumerable<BulkCreateNote> Note(string mapping, string side, SideRead read, string missing)
    {
        if (read.Unavailable is not null) yield return new BulkCreateNote(mapping, side, read.Unavailable);
        else if (read.IsMissingTable) yield return new BulkCreateNote(mapping, side, missing);
    }

    private async Task ReportAsync(string? batchId, BulkCreateProgress progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(batchId))
            return;

        await hub.Clients.Group(batchId).SendAsync("bulkMappingProgress", progress, cancellationToken);
    }

    /// <summary>
    /// Everything not stated is left unset, which is what makes this worth doing in bulk: the endpoints,
    /// the provisioning settings, the scripts and the hooks all inherit, so forty mappings created this
    /// way are forty mappings that follow the replication rather than forty copies of its settings.
    /// The target mirrors the source's schema and table — the same autofill the form does by hand.
    /// <para>
    /// The columns are not part of "unset": <see cref="CaptureAsync"/> fills them in before this is
    /// saved. They are not a setting to inherit — they are what the two tables are.
    /// </para>
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

/// <param name="Stage">What is happening to <paramref name="Name"/> right now — <c>"reading"</c> while
/// its two catalogs are being read, <c>"created"</c> once it is saved. There because reading them is
/// the slow part and a screen that only counted saves would look stalled through it.
/// <para>
/// A closed set of strings rather than an enum, deliberately. These go out over SignalR, whose
/// protocol has its own serializer configuration separate from MVC's <c>JsonStringEnumConverter</c> —
/// an enum here would be a number on the wire the day someone changed one of them, and a client
/// comparing it to a name would silently stop matching.
/// </para></param>
public sealed record BulkCreateProgress(int Done, int Total, string Name, string Stage)
{
    public static BulkCreateProgress Reading(int done, int total, string name) => new(done, total, name, "reading");

    public static BulkCreateProgress Created(int done, int total, string name) => new(done, total, name, "created");
}

/// <param name="Side">"source" or "target" — which of the two could not be fully captured.</param>
public sealed record BulkCreateNote(string Mapping, string Side, string Reason);

/// <param name="Skipped">Tables that already had a mapping. Reported rather than silently dropped, so
/// "create 40" answering with 12 is explained on screen instead of looking like a failure.</param>
/// <param name="Notes">Mappings that were created but could not be fully captured, and why — a target
/// provisioning has yet to create, a source that could not be read. Named reasons rather than a count,
/// for the same reason <see cref="MetadataRefreshSide"/> names columns rather than counting them: a
/// count leaves the operator to go and find which one.</param>
public sealed record BulkCreateResult(
    IReadOnlyList<string> Created, IReadOnlyList<string> Skipped, IReadOnlyList<BulkCreateNote> Notes);

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

/// <summary>
/// One mapping's read intent and hold, over the wire — see phase 100.
/// <para>
/// <paramref name="Intent"/> and <paramref name="Hold"/> are always resolved, never the raw absence of
/// a stored row: a mapping that has never run reports <see cref="ReadIntentResolution.Default"/> and
/// <see cref="ReadHold.None"/> rather than a null a client would have to resolve itself. Everything
/// this design promises — that a full load always traces to somebody's choice — depends on the
/// resolved value being visible before a first pass, not only after one.
/// </para>
/// </summary>
public sealed record MappingReadStateDto(ReadIntent Intent, ReadHold Hold, string? Watermark, DateTimeOffset? WatermarkTimeUtc);

/// <summary>Both fields required, deliberately: the one call this backs exists so an operator recovering
/// from a hold states the intent and the hold together, rather than in two requests with a window
/// between them where the hold is gone and the old intent is still what the next pass would honour.</summary>
public sealed record SetMappingReadStateRequest(ReadIntent Intent, ReadHold Hold);
