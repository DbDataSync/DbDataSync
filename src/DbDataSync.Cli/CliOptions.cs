namespace DbDataSync.Cli;

/// <summary>
/// Where a tool that was just installed keeps its things.
/// <para>
/// Not the current directory, which is what the API's own defaults use: `dbdatasync serve` run from
/// wherever a shell happens to be would scatter a config repo and a state database across a user's
/// filesystem, and the second run would find neither. A per-user application-data directory is the
/// answer every other installed tool gives.
/// </para>
/// </summary>
public static class CliOptions
{
    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create),
            "DbDataSync");

    /// <summary>Reads <c>--name value</c> from an argument list, or null.</summary>
    public static string? Read(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    public static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
