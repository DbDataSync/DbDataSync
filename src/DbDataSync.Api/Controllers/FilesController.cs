using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Phase 173V — <c>&lt;repo&gt;/files/</c>, a standard place for user-provided files (a JDBC driver jar,
/// so far the only real case). See <c>architecture/planning/todo/user-provided-files-store.md</c> for the
/// full design. <c>[Authorize(Policies.Admin)]</c> throughout, per the <see cref="LibrariesController"/>
/// precedent this mirrors closely: uploading a jar here means it is eventually loaded and run inside the
/// API/TaskRunner process, the identical trust boundary a non-catalog library install already carries.
/// </summary>
[ApiController]
[Route("api/files")]
public sealed class FilesController(FilesService service) : ControllerBase
{
    /// <summary>.jar only, v1 — an allowlist, not a denylist, matching the one real use case; broadening
    /// it later is a one-line change, not a reason to accept anything by default now.</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jar" };

    [Authorize(Policies.Admin)]
    [HttpGet]
    public ActionResult<IReadOnlyList<FileSummary>> List() => Ok(service.List());

    /// <summary>
    /// One or more files in one request (an Oracle-wallet-shaped upload is four files at once) — each
    /// reported independently rather than all-or-nothing over one stale name, so a partial conflict
    /// doesn't have to be retried as a whole.
    /// </summary>
    [Authorize(Policies.Admin)]
    [HttpPost]
    [RequestSizeLimit(UploadSizeLimitBytes)]
    public async Task<ActionResult<IReadOnlyList<UploadResult>>> Upload(
        [FromForm] IFormFileCollection files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return BadRequest(new { error = "At least one file is required." });

        var results = new List<UploadResult>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(name))
            {
                results.Add(new UploadResult(file.FileName, false, "No filename given."));
                continue;
            }
            if (!AllowedExtensions.Contains(Path.GetExtension(name)))
            {
                results.Add(new UploadResult(name, false,
                    $"'{Path.GetExtension(name)}' is not an accepted file type — only .jar today."));
                continue;
            }
            if (service.Exists(name))
            {
                results.Add(new UploadResult(name, false,
                    $"'{name}' already exists — delete it first, or rename the file before re-uploading."));
                continue;
            }

            await using var stream = file.OpenReadStream();
            await service.SaveAsync(name, stream, cancellationToken);
            results.Add(new UploadResult(name, true, null));
        }

        return Ok(results);
    }

    /// <summary>Refused (409) when a JDBC-backed driver on disk still names this file, unless
    /// <paramref name="force"/> — in-use is in-use regardless of anything else.</summary>
    [Authorize(Policies.Admin)]
    [HttpDelete("{name}")]
    public ActionResult Delete(string name, [FromQuery] bool force = false)
    {
        if (!service.Exists(name))
            return NotFound(new { error = $"'{name}' does not exist." });

        var usedBy = service.UsedBy(name);
        if (usedBy.Count > 0 && !force)
        {
            return Conflict(new
            {
                error = $"'{name}' is still named by {usedBy.Count} driver(s): {string.Join(", ", usedBy)}. " +
                         "Pass force=true to remove it anyway.",
                usedBy,
            });
        }

        service.Delete(name);
        return NoContent();
    }

    // 200 MB: comfortably covers every known real case (Oracle's wallet jars, tens of MB total) without
    // leaving the door wide open — a deliberate number, not "reasonable limits" left unstated.
    private const long UploadSizeLimitBytes = 200 * 1024 * 1024;
}

public sealed record UploadResult(string Name, bool Succeeded, string? Error);
