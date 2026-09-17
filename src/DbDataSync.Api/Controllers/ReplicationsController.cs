using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Api.Auth;
using DbDataSync.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

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

    /// <summary>
    /// The patch one commit made to this replication — the Version Control tab's "View changes".
    /// <para>
    /// Read-only. Editing a diff is not a thing this offers, and the response carries no way to.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{name}/history/{sha}/diff")]
    public ActionResult<ConfigDiff> Diff(string name, string sha)
    {
        try
        {
            return Ok(configRepository.GetReplicationCommitDiff(name, sha));
        }
        catch (GitCommitNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// What restoring to this commit would change — **not** the patch that commit made.
    /// <para>
    /// A separate endpoint rather than a flag on the one above because the two answer different
    /// questions and a confirmation has to show this one. A commit's own patch says what it changed;
    /// once anything has happened since, "what will this restore change" is a different set — restoring
    /// to a commit that only renamed a column may well delete three mappings created after it, none of
    /// which appear in that commit's patch. Both come from the same diff machinery, anchored
    /// differently.
    /// </para>
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{name}/history/{sha}/restore-preview")]
    public ActionResult<ConfigDiff> RestorePreview(string name, string sha)
    {
        try
        {
            return Ok(configRepository.GetReplicationRestoreDiff(name, sha));
        }
        catch (GitCommitNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Puts this replication's config back the way it was at this commit, recorded as a new commit.
    /// <para>
    /// Admin by omission, like every other write here. A restore that would produce config this tool
    /// would reject on save is refused with what would have broken, and refusing leaves the working
    /// tree untouched — see <see cref="ConfigRepository.RestoreReplication"/>, which validates the whole
    /// restored set before writing any of it.
    /// </para>
    /// <para>
    /// **A running replication is not stopped for this.** The work queue may hold items for a mapping
    /// the restore deletes, and a worker may be mid-pass. The run model already handles a mapping
    /// disappearing — the work item fails, not the process — so the honest thing is for the
    /// confirmation to say what will change and let the operator decide, rather than for this endpoint
    /// to take a replication offline as a side effect.
    /// </para>
    /// </summary>
    [HttpPost("{name}/history/{sha}/restore")]
    public ActionResult<ConfigRestoreResult> Restore(string name, string sha)
    {
        try
        {
            return Ok(configRepository.RestoreReplication(name, sha, currentUser.Author));
        }
        catch (GitCommitNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Every pause/resume this replication has recorded, over both grains — see phase 131 and
    /// <see cref="TaskRunStore.GetPauseHistory"/>. Not gated on <c>ListReplications</c> the way most
    /// endpoints here are, matching <see cref="History"/> immediately above: an unknown name simply has
    /// no rows, the same convention that method already uses rather than a new one.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("{name}/pause-history")]
    public ActionResult<IReadOnlyList<PauseEventRecord>> PauseHistory(string name, [FromQuery] int limit = 50) =>
        Ok(taskRunStore.GetPauseHistory(name, limit));
}

/// <summary>The one field that saves on its own — see <c>SetEnabled</c>.</summary>
public sealed record SetEnabledRequest(bool Enabled);

/// <summary>
/// A hold, and whatever the operator wants recorded about it — see <c>SetPaused</c>.
/// </summary>
/// <param name="Note">Sent on every action, including a resume. Null clears it.</param>
public sealed record SetPausedRequest(bool Paused, string? Note);
