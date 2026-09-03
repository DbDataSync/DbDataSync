namespace DbDataSync.Scripting.Abstractions;

/// <summary>
/// The parameters a binding supplies, with the reading helpers a script would otherwise write itself.
/// Strings, like every other options bag in this codebase (`ReaderConfig.Options`, `CacheConfig.Options`);
/// consistent, and untyped.
/// </summary>
public sealed class ScriptParameters(IReadOnlyDictionary<string, string> values)
{
    public static ScriptParameters Empty { get; } = new(new Dictionary<string, string>());

    public IReadOnlyDictionary<string, string> Values => values;

    public string? Get(string name) => values.TryGetValue(name, out var value) ? value : null;

    /// <summary>Throws naming the parameter and the script's own manifest, because a missing required
    /// parameter is a configuration error and the operator needs to know which binding to fix.</summary>
    public string Require(string name) =>
        Get(name) ?? throw new ScriptExecutionException($"Required script parameter '{name}' was not supplied.");

    public int GetInt(string name, int fallback) =>
        int.TryParse(Get(name), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    public bool GetBool(string name, bool fallback) =>
        Get(name) is { } raw
            ? raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1"
            : fallback;
}

/// <summary>Thrown by a script, or on its behalf, when it cannot do what it was asked. Carries no
/// special handling beyond failing the run with the script's name attached — the point is that a
/// script's own failures read differently from the host's.</summary>
public sealed class ScriptExecutionException(string message, Exception? inner = null)
    : Exception(message, inner);
