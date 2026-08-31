using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Api.Auth;
using DataSync.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

[ApiController]
[Route("api/replications")]
public sealed class ReplicationsController(
    ConfigRepository configRepository,
    ParameterCheck parameterCheck,
    ProcessSupervisor supervisor,
    TaskRunStore taskRunStore,
    CurrentUser currentUser) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<IReadOnlyList<string>> List() => Ok(configRepository.ListReplications());

    [Authorize(Policies.Viewer)]
    [HttpGet("{name}")]
    public ActionResult<ReplicationTaskConfig> Get(string name)
    {
        try
        {
            return Ok(configRepository.LoadReplicationTask(name));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// What this replication's worker process is doing, right now.
    /// <para>
    /// Pulled rather than pushed, on <c>run-metrics.md</c>'s call: a status card is not something
    /// anybody stares at, and the case where somebody is watching is a live run, which the run hub
    /// already covers.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{name}/status")]
    public ActionResult<ReplicationStatus> Status(string name)
    {
        if (!configRepository.ListReplications().Contains(name, StringComparer.Ordinal))
            return NotFound();

        // Composed here rather than in the supervisor, which knows about processes and has no business
        // reading config or state. What the worker is doing and whether it is allowed to do anything
        // are different questions; they travel together because one card asks both.
        var (paused, pauseNote) = taskRunStore.GetPauseState(name);
        var enabled = true;
        try
        {
            enabled = configRepository.LoadReplicationTask(name).Enabled;
        }
        catch (FileNotFoundException)
        {
            // Listed a moment ago and gone now. A status call is not the place to turn that into an
            // error — the next poll will 404 on the listing check above.
        }

        return Ok(supervisor.DescribeStatus(name) with
        {
            Enabled = enabled,
            Paused = paused,
            PauseNote = pauseNote,
            ShouldRun = TaskScheduling.ShouldRun(enabled, paused),
        });
    }

    /// <summary>
    /// Holds this replication, or releases it — state only, never a commit.
    /// <para>
    /// Deliberately not shaped like <see cref="SetEnabled"/> beyond its URL: there is no config load
    /// and no save at all, because a pause is not config. That is the whole point of it existing
    /// separately from <c>Enabled</c> rather than as another value of it.
    /// </para>
    /// <para>
    /// The note arrives with every action, in both directions, because the popup asks every time —
    /// pausing with a reason, resuming with a note about the resolution, or either with the note
    /// cleared. Nothing here decides on the operator's behalf what should happen to it.
    /// </para>
    /// </summary>
    [HttpPut("{name}/paused")]
    public ActionResult<ReplicationStatus> SetPaused(string name, [FromBody] SetPausedRequest request)
    {
        if (!configRepository.ListReplications().Contains(name, StringComparer.Ordinal))
            return NotFound();

        taskRunStore.SetPaused(name, request.Paused, request.Note, currentUser.Author.Name);
        return Status(name);
    }

    /// <summary>
    /// Turns a replication on or off, and nothing else.
    /// <para>
    /// Its own endpoint rather than a mode of the upsert, because it is the one field that commits on
    /// its own. Routing it through the full save would mean toggling Enabled from the Runs tab quietly
    /// committed whatever half-finished pipeline edit was sitting in the Overview's draft — a change
    /// nobody asked for, made by a control that says nothing about it.
    /// </para>
    /// </summary>
    [HttpPut("{name}/enabled")]
    public ActionResult<ReplicationTaskConfig> SetEnabled(string name, [FromBody] SetEnabledRequest request)
    {
        try
        {
            var task = configRepository.LoadReplicationTask(name);
            task.Enabled = request.Enabled;
            return Ok(configRepository.SaveReplicationTask(task, currentUser.Author));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{name}")]
    public ActionResult<ReplicationTaskConfig> Upsert(string name, [FromBody] ReplicationTaskConfig task)
    {
        task.Name = name;
        try
        {
            parameterCheck.ThrowIfInvalid(task);
            return Ok(configRepository.SaveReplicationTask(task, currentUser.Author));
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{name}")]
    public IActionResult Delete(string name)
    {
        configRepository.DeleteReplicationTask(name, currentUser.Author);
        return NoContent();
    }

    [Authorize(Policies.Viewer)]
    [HttpGet("{name}/history")]
    public ActionResult<IReadOnlyList<CommitInfo>> History(string name, [FromQuery] int limit = 50) =>
        Ok(configRepository.GetReplicationHistory(name, limit));
}

/// <summary>The one field that saves on its own — see <c>SetEnabled</c>.</summary>
public sealed record SetEnabledRequest(bool Enabled);

/// <summary>
/// A hold, and whatever the operator wants recorded about it — see <c>SetPaused</c>.
/// </summary>
/// <param name="Note">Sent on every action, including a resume. Null clears it.</param>
public sealed record SetPausedRequest(bool Paused, string? Note);
