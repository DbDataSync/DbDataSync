namespace DataSync.DevHarness;

/// <summary>
/// A verb plus <c>--name value</c> / <c>--flag</c> options. Hand-rolled rather than taking a
/// command-line-parser dependency, matching how <c>TaskRunnerOptions</c> already parses its own
/// arguments in this repo.
/// </summary>
public sealed class HarnessArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private HarnessArgs(string verb) => Verb = verb;

    public string Verb { get; }

    public static bool TryParse(string[] args, out HarnessArgs? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            error = "A verb is required.";
            return false;
        }

        var result = new HarnessArgs(args[0]);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument '{args[i]}' (options must be written as --name value).";
                return false;
            }

            var name = args[i][2..];
            // A flag is an option whose next token is another option, or nothing at all.
            var isFlag = i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal);
            result._options[name] = isFlag ? null : args[++i];
        }

        parsed = result;
        return true;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? String(string name, string? fallback = null) =>
        _options.TryGetValue(name, out var value) && value is not null ? value : fallback;

    public int Int(string name, int fallback) =>
        _options.TryGetValue(name, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    /// <summary>Accepts a bare number of seconds or a suffixed duration ("90s", "5m", "1h"), because
    /// typing <c>--duration 5m</c> is what anyone actually reaches for.</summary>
    public TimeSpan Duration(string name, TimeSpan fallback)
    {
        var raw = String(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var value = raw.Trim();
        var suffix = value[^1];
        var number = char.IsDigit(suffix) ? value : value[..^1];

        if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var amount))
            throw new HarnessException($"'--{name} {raw}' is not a duration (try 30s, 5m, or 1h).");

        return char.ToLowerInvariant(suffix) switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ when char.IsDigit(suffix) => TimeSpan.FromSeconds(amount),
            _ => throw new HarnessException($"'--{name} {raw}' has an unknown unit '{suffix}' (use s, m, or h)."),
        };
    }
}

/// <summary>An error worth showing the operator as a message rather than a stack trace — a missing
/// prerequisite, a bad argument, an environment that isn't in the state the verb needs.</summary>
public sealed class HarnessException(string message) : Exception(message);
