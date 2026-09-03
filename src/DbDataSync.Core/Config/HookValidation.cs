using System.Text.RegularExpressions;

namespace DbDataSync.Core.Config;

/// <summary>
/// The token/parameter check that is the SQL equivalent of compiling — see phase 26's "identifiers are
/// substituted; values are bound". Two families, never confused:
/// <list type="bullet">
/// <item><b>Tokens</b> — identifiers, <c>{{name}}</c>, quoted by the target dialect: the five built-ins
/// (<see cref="BuiltInTokens"/>) plus, for a named reusable hook, its own declared parameters — a hook
/// author writes <c>{{controlTable}}</c> exactly like a built-in, because a declared parameter *is* an
/// identifier, never a second value channel.</item>
/// <item><b>Parameters</b> — values, <c>@name</c>, bound: the eleven built-ins
/// (<see cref="BuiltInParameters"/>). There is no way to declare a custom one.</item>
/// </list>
/// </summary>
public static partial class HookValidation
{
    public static IReadOnlyList<string> BuiltInTokens { get; } =
        ["target", "targetSchema", "targetTable", "source", "staging"];

    public static IReadOnlyList<string> BuiltInParameters { get; } =
    [
        "replication", "mapping", "runId", "runKind", "segment", "segmentIndex", "segmentCount",
        "isLastSegment", "rowsStaged", "rowsWritten", "watermark",
    ];

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"@(\w+)")]
    private static partial Regex ParameterPattern();

    public static IReadOnlyCollection<string> FindTokenReferences(string sql) =>
        TokenPattern().Matches(sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> FindParameterReferences(string sql) =>
        ParameterPattern().Matches(sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Not everything is available at every point — see phase 26's table. Returns which
    /// built-in tokens/parameters are *not* available at <paramref name="point"/>; everything else in
    /// the built-in sets is available everywhere.</summary>
    private static (IReadOnlyList<string> Tokens, IReadOnlyList<string> Parameters) Unavailable(string point) => point switch
    {
        // The reader is lazy and nothing has been counted yet — there is no row count to report.
        HookPoints.BeforeStage => (["staging"], ["rowsStaged", "rowsWritten"]),
        HookPoints.AfterStage => ([], ["rowsWritten"]),
        HookPoints.BeforeLoad => ([], ["rowsWritten"]),
        HookPoints.AfterLoad => ([], []),
        _ => ([], []),
    };

    /// <summary>
    /// Point-free validation: every reference names a real token (built-in or declared) or a real
    /// built-in parameter — nothing about which point this will run at, because a reusable hook script
    /// may be bound at more than one. Used for a hook script's own "Validate" action.
    /// </summary>
    public static IReadOnlyList<string> ValidateBody(string sql, IReadOnlyList<string> declaredParameterNames)
    {
        var errors = new List<string>();
        var knownTokens = new HashSet<string>(BuiltInTokens, StringComparer.Ordinal);
        knownTokens.UnionWith(declaredParameterNames);

        foreach (var token in FindTokenReferences(sql))
            if (!knownTokens.Contains(token))
                errors.Add($"'{{{{{token}}}}}' is not a built-in token or a declared parameter.");

        foreach (var parameter in FindParameterReferences(sql))
            if (!BuiltInParameters.Contains(parameter, StringComparer.Ordinal))
                errors.Add($"'@{parameter}' is not a built-in hook parameter.");

        return errors;
    }

    /// <summary>
    /// Full validation once a specific point is known — used when a hook binding (inline or a reference
    /// to a named hook) is saved at a mapping, replication or connection. Naming the point and what is
    /// available there is the point: the most likely accident should fail where the operator is
    /// looking, at save time, not as a null at 3am.
    /// </summary>
    public static IReadOnlyList<string> ValidateAtPoint(string point, string sql, IReadOnlyList<string> declaredParameterNames)
    {
        var errors = new List<string>(ValidateBody(sql, declaredParameterNames));
        var (unavailableTokens, unavailableParameters) = Unavailable(point);

        foreach (var token in FindTokenReferences(sql))
            if (unavailableTokens.Contains(token))
                errors.Add($"'{{{{{token}}}}}' is not available at '{point}' (available: {AvailableTokensAt(point)}).");

        foreach (var parameter in FindParameterReferences(sql))
            if (unavailableParameters.Contains(parameter))
                errors.Add($"'@{parameter}' is not available at '{point}' (available: {AvailableParametersAt(point)}).");

        return errors;
    }

    private static string AvailableTokensAt(string point)
    {
        var unavailable = Unavailable(point).Tokens;
        return string.Join(", ", BuiltInTokens.Where(t => !unavailable.Contains(t)));
    }

    private static string AvailableParametersAt(string point)
    {
        var unavailable = Unavailable(point).Parameters;
        return string.Join(", ", BuiltInParameters.Where(p => !unavailable.Contains(p)));
    }
}
