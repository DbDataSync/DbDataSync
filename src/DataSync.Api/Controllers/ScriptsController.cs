using DataSync.Api.Services;
using DataSync.Core.Config;
using DataSync.Core.Git;
using DataSync.Scripting;
using DataSync.Scripting.Abstractions;
using DataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
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
    ScriptUsageScanner usageScanner,
    ScriptTestService testService,
    CurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// Every script with where it is bound. The binding sites come with the list rather than from a
    /// per-script call because the list is where the question is asked — "which of these is actually
    /// doing anything" is about all of them at once, and N+1 requests to answer it would be one
    /// request per row.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<IReadOnlyList<ScriptListItem>> List()
    {
        var usages = usageScanner.ScanAll();
        return Ok(configRepository.ListScripts()
            .Select(n => configRepository.LoadScript(n).Manifest)
            .Select(m => new ScriptListItem(m, usages.GetValueOrDefault(m.Name, [])))
            .ToList());
    }

    /// <summary>
    /// The slots this build supports, each with the binding levels it may be bound at — so the SPA
    /// renders a slot only where it means something rather than offering a control that silently does
    /// nothing. <c>metadataProvider</c> is connection-only; see <see cref="ScriptSlots.BindableAt"/>.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("slots")]
    public ActionResult<IReadOnlyList<ScriptSlotInfo>> Slots() =>
        Ok(ScriptSlots.All
            .Select(slot => new ScriptSlotInfo(
                slot,
                BindingLevels.All.Where(level => ScriptSlots.IsBindableAt(slot, level)).ToList(),
                ScriptSlots.Describe(slot).Label,
                ScriptSlots.Describe(slot).Description))
            .ToList());

    [Authorize(Policies.Viewer)]
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
                known = ScriptSlots.All.Append(ScriptSlots.Hook),
            });

        var diagnostics = ValidateScript(script);
        if (diagnostics.Count > 0)
            return BadRequest(new
            {
                error = script.Manifest.Language == ScriptLanguage.Sql
                    ? $"Script '{name}' did not validate."
                    : $"Script '{name}' did not compile.",
                diagnostics = diagnostics.Select(d => new { d.Line, d.Column, d.Message }),
            });

        try
        {
            configRepository.SaveScript(script, currentUser.Author);
            return Ok(new ScriptSaveResult(script.Manifest, []));
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Checks a script without saving, so an operator finds out before committing — compiling
    /// for a C# script, the token/parameter check for a SQL hook (see <see cref="HookValidation"/>).
    /// Named Compile for the C# case that motivated it; kept as one endpoint for both because the SPA's
    /// edit page swaps only which check runs, not the action a Validate button calls.</summary>
    /// <summary>
    /// Runs the script against sample input and reports what it did — see phase 41.
    /// <para>
    /// Takes the definition in the body like <c>Compile</c> does, so what is tested is what is in the
    /// editor rather than what was last saved. A test that could only run saved code would mean saving
    /// to find out whether it was worth saving.
    /// </para>
    /// <para>
    /// Generated input unless the caller names a connection. There is deliberately no fallback the
    /// other way: a live test is a query against a real system, and it happens only because somebody
    /// asked for it by name.
    /// </para>
    /// </summary>
    [HttpPost("{name}/test")]
    public async Task<ActionResult<ScriptTestResult>> Test(
        string name, [FromBody] ScriptTestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await testService.RunAsync(request, cancellationToken));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ConfigValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{name}/compile")]
    public ActionResult<ScriptSaveResult> Compile(string name, [FromBody] ScriptDefinition script)
    {
        script.Manifest.Name = name;
        var diagnostics = ValidateScript(script);
        return Ok(new ScriptSaveResult(
            script.Manifest,
            diagnostics.Select(d => new ScriptDiagnosticDto(d.Line, d.Column, d.Message)).ToList(),
            diagnostics.Count == 0));
    }

    /// <summary>Empty means "compiled/validated cleanly".</summary>
    private List<ScriptDiagnostic> ValidateScript(ScriptDefinition script)
    {
        if (script.Manifest.Language == ScriptLanguage.Sql)
        {
            // Point-free: a reusable hook may be bound at more than one point, so only "is every
            // reference to a real token/parameter" is checked here. Point-specific availability is
            // checked when a binding names the point — see ConfigRepository.ValidateHooks.
            var declaredNames = script.Manifest.Parameters.Select(p => p.Name).ToList();
            return HookValidation.ValidateBody(script.Code, declaredNames)
                .Select(e => new ScriptDiagnostic(0, 0, e)).ToList();
        }

        var compilation = scriptHost.Validate(script);
        return compilation.Success ? [] : compilation.Diagnostics.ToList();
    }

    [HttpDelete("{name}")]
    public IActionResult Delete(string name)
    {
        configRepository.DeleteScript(name, currentUser.Author);
        return NoContent();
    }
}

/// <summary>A script as the list shows it: its manifest, and every place it is bound.</summary>
public sealed record ScriptListItem(ScriptConfig Manifest, IReadOnlyList<ScriptUsage> UsedBy);

public sealed record ScriptSlotInfo(string Slot, IReadOnlyList<string> Levels, string Label, string Description);

public sealed record ScriptDiagnosticDto(int Line, int Column, string Message);

public sealed record ScriptSaveResult(
    ScriptConfig Manifest, IReadOnlyList<ScriptDiagnosticDto> Diagnostics, bool Compiles = true);
