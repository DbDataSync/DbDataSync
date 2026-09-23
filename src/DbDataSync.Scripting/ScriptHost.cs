using System.Collections.Concurrent;
using DbDataSync.Core.Config;
using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.Scripting;

/// <summary>
/// Loads, compiles and hands out script instances. One per process.
/// <para>
/// The in-memory cache here matters in the API, which is long-lived and compiles on every save and
/// preview. In the TaskRunner it is nearly irrelevant — that process handles one run — and what does
/// the work there is <see cref="ScriptCacheDirectory"/>.
/// </para>
/// <para>
/// **Keyed by content hash, not by script name.** A name-keyed cache means "resolved once, right or
/// wrong, for the life of the process" — edit a bound script, save it, preview or run again, and the
/// stale compiled instance from before the edit comes back regardless, with nothing short of a full
/// restart able to fix it. Every <see cref="Resolve{T}"/> call reads the script's manifest fresh (cheap
/// — a small YAML file, not the code beside it) to get its current <see cref="ScriptConfig.ContentHash"/>,
/// computed once at save time by <c>ConfigRepository.SaveScript</c>, and looks the compiled result up
/// by that. A hash the cache has not seen — a real edit, or the first resolve of a process — pays for
/// loading the code and compiling it; a hash it has costs one dictionary lookup. What never happens on
/// a call that hits the cache is rehashing anything: the hash was computed once, at save time, and is
/// only ever read back.
/// </para>
/// </summary>
public sealed class ScriptHost(ConfigRepository configRepository, ScriptCompiler compiler)
{
    private readonly ConcurrentDictionary<string, ScriptCompilation> _compiled = new(StringComparer.Ordinal);

    /// <summary>Compiles without caching the result, for validating a script the operator is editing.</summary>
    public ScriptCompilation Validate(ScriptDefinition script) => compiler.Compile(script);

    /// <summary>
    /// The script's implementation of <typeparamref name="T"/>, compiled if the manifest's current
    /// <see cref="ScriptConfig.ContentHash"/> has not been seen before. Throws rather than returning
    /// null: a binding naming a script that does not compile is a configuration error, and swallowing it
    /// would silently drop a transform the operator asked for.
    /// </summary>
    public T Resolve<T>(string scriptName) where T : class
    {
        ScriptCompilation compilation;
        try
        {
            var manifest = configRepository.LoadScriptManifest(scriptName);
            if (!manifest.Enabled)
            {
                // Not cached: as cheap to re-detect as the manifest read that found it, and caching it
                // would reintroduce the exact staleness this class exists to avoid — flip Enabled back
                // on and the very next resolve must see that, not a "disabled" answer frozen from before.
                compilation = ScriptCompilation.Failed(
                    [new ScriptDiagnostic(0, 0, $"Script '{scriptName}' is disabled.")]);
            }
            else
            {
                // A manifest saved before ContentHash existed reads back null — falling back to the
                // script's own name keeps that one script cached under today's (imperfect, name-only)
                // behavior rather than recompiling it every single call. Its very next save fills the
                // field in via ConfigRepository.SaveScript, and this fallback stops applying to it.
                var key = manifest.ContentHash ?? scriptName;
                compilation = _compiled.GetOrAdd(key, _ => compiler.Compile(configRepository.LoadScript(scriptName)));
            }
        }
        catch (FileNotFoundException)
        {
            compilation = ScriptCompilation.Failed([new ScriptDiagnostic(0, 0, $"Script '{scriptName}' was not found.")]);
        }

        return compilation.CreateInstance<T>(scriptName);
    }

    /// <summary>
    /// The binding for a slot, already resolved through the hierarchy, as a ready-to-call instance —
    /// or null when nothing is bound. Callers get "there is no script here" and "here is the script"
    /// and never have to think about which level it came from.
    /// </summary>
    public (T Script, ScriptParameters Parameters)? ResolveBinding<T>(
        string slot,
        ConnectionConfig? connection,
        ReplicationTaskConfig? task,
        TableMappingConfig? mapping) where T : class
    {
        var binding = ScriptResolution.Resolve(slot, connection, task, mapping);
        if (binding is null)
            return null;

        return (Resolve<T>(binding.ScriptName), new ScriptParameters(binding.Parameters));
    }
}
