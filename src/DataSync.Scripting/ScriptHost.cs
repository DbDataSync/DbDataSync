using System.Collections.Concurrent;
using DataSync.Core.Config;
using DataSync.Scripting.Abstractions;

namespace DataSync.Scripting;

/// <summary>
/// Loads, compiles and hands out script instances. One per process.
/// <para>
/// The in-memory cache here matters in the API, which is long-lived and compiles on every save and
/// preview. In the TaskRunner it is nearly irrelevant — that process handles one run — and what does
/// the work there is <see cref="ScriptCacheDirectory"/>.
/// </para>
/// </summary>
public sealed class ScriptHost(ConfigRepository configRepository, ScriptCompiler compiler)
{
    private readonly ConcurrentDictionary<string, ScriptCompilation> _compiled = new(StringComparer.Ordinal);

    /// <summary>Compiles without caching the result, for validating a script the operator is editing.</summary>
    public ScriptCompilation Validate(ScriptDefinition script) => compiler.Compile(script);

    /// <summary>
    /// The script's implementation of <typeparamref name="T"/>, compiled if it has not been already.
    /// Throws rather than returning null: a binding naming a script that does not compile is a
    /// configuration error, and swallowing it would silently drop a transform the operator asked for.
    /// </summary>
    public T Resolve<T>(string scriptName) where T : class
    {
        var compilation = _compiled.GetOrAdd(scriptName, name =>
        {
            ScriptDefinition script;
            try
            {
                script = configRepository.LoadScript(name);
            }
            catch (FileNotFoundException)
            {
                return ScriptCompilation.Failed([new ScriptDiagnostic(0, 0, $"Script '{name}' was not found.")]);
            }

            return script.Manifest.Enabled
                ? compiler.Compile(script)
                : ScriptCompilation.Failed([new ScriptDiagnostic(0, 0, $"Script '{name}' is disabled.")]);
        });

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
