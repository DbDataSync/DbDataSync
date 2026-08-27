using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Scripting;
using DataSync.Scripting.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

/// <summary>
/// The global script registry. A script is registered once here and then bound wherever it applies —
/// on a connection, a replication or a table mapping — with the mapping winning
/// (<see cref="ScriptResolution"/>).
/// </summary>
[ApiController]
[Route("api/scripts")]
public sealed class ScriptsController(
    ConfigRepository configRepository,
    ScriptHost scriptHost,
    GitAuthor author) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ScriptConfig>> List() =>
        Ok(configRepository.ListScripts().Select(n => configRepository.LoadScript(n).Manifest).ToList());

    [HttpGet("slots")]
    public ActionResult<IReadOnlyList<string>> Slots() => Ok(ScriptSlots.All);

    [HttpGet("{name}")]
    public ActionResult<ScriptDefinition> Get(string name)
    {
        try
        {
            return Ok(configRepository.LoadScript(name));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Saves a script, **compiling it first**. A script that will not compile is rejected here with its
    /// diagnostics rather than accepted and discovered at the first run — the rule phase 16 set for
    /// table mappings, applied to the thing where it matters most.
    /// <para>
    /// A successful compile also populates the on-disk assembly cache, so the TaskRunner — a process
    /// per run — normally loads a DLL and never starts a compiler.
    /// </para>
    /// </summary>
    [HttpPut("{name}")]
    public ActionResult<ScriptSaveResult> Upsert(string name, [FromBody] ScriptDefinition script)
    {
        script.Manifest.Name = name;

        if (!ScriptSlots.IsKnown(script.Manifest.Kind))
            return BadRequest(new
            {
                error = $"'{script.Manifest.Kind}' is not a script kind this build knows about.",
                known = ScriptSlots.All,
            });

        var compilation = scriptHost.Validate(script);
        if (!compilation.Success)
            return BadRequest(new
            {
                error = $"Script '{name}' did not compile.",
                diagnostics = compilation.Diagnostics.Select(d => new { d.Line, d.Column, d.Message }),
            });

        try
        {
            configRepository.SaveScript(script, author);
            return Ok(new ScriptSaveResult(script.Manifest, []));
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Compiles without saving, so an operator can find out before committing.</summary>
    [HttpPost("{name}/compile")]
    public ActionResult<ScriptSaveResult> Compile(string name, [FromBody] ScriptDefinition script)
    {
        script.Manifest.Name = name;
        var compilation = scriptHost.Validate(script);
        return Ok(new ScriptSaveResult(
            script.Manifest,
            compilation.Diagnostics.Select(d => new ScriptDiagnosticDto(d.Line, d.Column, d.Message)).ToList(),
            compilation.Success));
    }

    [HttpDelete("{name}")]
    public IActionResult Delete(string name)
    {
        configRepository.DeleteScript(name, author);
        return NoContent();
    }
}

public sealed record ScriptDiagnosticDto(int Line, int Column, string Message);

public sealed record ScriptSaveResult(
    ScriptConfig Manifest, IReadOnlyList<ScriptDiagnosticDto> Diagnostics, bool Compiles = true);
