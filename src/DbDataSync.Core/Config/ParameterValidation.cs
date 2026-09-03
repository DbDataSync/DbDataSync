namespace DbDataSync.Core.Config;

/// <summary>
/// Checks operator-supplied values against what was declared.
/// <para>
/// Server-side, because the SPA's own checks are a courtesy: config also arrives through the API
/// directly and through a git commit somebody made by hand, and "the form would not have let you"
/// is not a property of the data.
/// </para>
/// </summary>
public static class ParameterValidation
{
    /// <summary>
    /// Every problem, not the first. Somebody filling in six settings should find out about all six
    /// mistakes at once rather than one save at a time.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<ParameterDescriptor> declared, IReadOnlyDictionary<string, string> values, string what)
    {
        var problems = new List<string>();

        foreach (var parameter in declared)
        {
            // A parameter the declarer says does not apply is not one the operator can fill in.
            // Requiring an unanswerable question would make a valid connection unsaveable.
            if (!parameter.Visible)
                continue;

            if (parameter.Occurrences.IsVararg)
            {
                ValidateVararg(parameter, values, what, problems);
                continue;
            }

            var present = values.TryGetValue(parameter.Name, out var value) && !string.IsNullOrWhiteSpace(value);
            if (parameter.Required && !present)
            {
                problems.Add($"{what}: '{parameter.DisplayLabel}' is required.");
                continue;
            }
            if (present)
                ValidateValue(parameter, value!, what, problems);
        }

        // An undeclared value is reported rather than rejected: a driver may read a setting it has not
        // got round to declaring, and silently dropping one an operator typed would be worse than
        // saying it is not recognised.
        foreach (var name in values.Keys)
        {
            if (!declared.Any(p => Matches(p, name)))
                problems.Add($"{what}: '{name}' is not a setting this offers.");
        }

        return problems;
    }

    /// <summary>
    /// A vararg's values are keyed by the parameter's own name with an index or a key suffixed —
    /// <c>properties.Encrypt</c>, <c>segments.0</c> — because the persisted shape is a flat string
    /// dictionary and has to stay one for every config written before this existed to keep loading.
    /// </summary>
    public static string KeyFor(ParameterDescriptor parameter, string suffix) => $"{parameter.Name}.{suffix}";

    private static bool Matches(ParameterDescriptor parameter, string key) =>
        parameter.Occurrences.IsVararg
            ? key.StartsWith(parameter.Name + ".", StringComparison.Ordinal)
            : key == parameter.Name;

    private static void ValidateVararg(
        ParameterDescriptor parameter, IReadOnlyDictionary<string, string> values, string what, List<string> problems)
    {
        var count = values.Keys.Count(k => Matches(parameter, k));
        var cardinality = parameter.Occurrences;

        if (count < cardinality.Min)
            problems.Add($"{what}: '{parameter.DisplayLabel}' needs at least {cardinality.Min} value(s); {count} given.");

        var max = Math.Min(cardinality.Max, ParameterCardinality.Ceiling);
        if (count > max)
            problems.Add($"{what}: '{parameter.DisplayLabel}' takes at most {max} value(s); {count} given.");

        foreach (var (key, value) in values.Where(v => Matches(parameter, v.Key)))
        {
            if (!string.IsNullOrWhiteSpace(value))
                ValidateValue(parameter, value, $"{what} ({key})", problems);
        }
    }

    /// <summary>
    /// Only what the type genuinely determines. A <see cref="ParameterType.ColumnPicker"/> is not
    /// checked against a column list here: the mapping it resolves against is not in scope at save
    /// time for every caller, and a rule that only sometimes runs is worse than one that does not.
    /// </summary>
    private static void ValidateValue(
        ParameterDescriptor parameter, string value, string what, List<string> problems)
    {
        var ok = parameter.Type switch
        {
            ParameterType.Number => double.TryParse(value, out _),
            ParameterType.Bool => bool.TryParse(value, out _),
            ParameterType.Date => DateOnly.TryParse(value, out _),
            ParameterType.DateTime => DateTimeOffset.TryParse(value, out _),
            ParameterType.Dropdown => parameter.DropdownOptions?.Contains(value, StringComparer.Ordinal) ?? true,
            _ => true,
        };

        if (!ok)
        {
            problems.Add(parameter.Type == ParameterType.Dropdown
                ? $"{what}: '{value}' is not one of {parameter.DisplayLabel}'s options " +
                  $"({string.Join(", ", parameter.DropdownOptions ?? [])})."
                : $"{what}: '{parameter.DisplayLabel}' expects {parameter.Type.ToString().ToLowerInvariant()}, got '{value}'.");
        }
    }
}
