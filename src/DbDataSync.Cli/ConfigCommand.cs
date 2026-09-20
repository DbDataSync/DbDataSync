using System.Text.Json;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync config check|get|set|cert|secret|library|driver</c> — every configuration-related command
/// grouped under one root, phase 115's answer to the CLI having grown one verb per feature
/// (<c>doctor</c>, <c>cert</c>, <c>secret</c>, <c>library</c>, <c>driver</c> were all separately
/// top-level before this). <c>cert</c>/<c>secret</c>/<c>library</c>/<c>driver</c> keep their own
/// existing implementations untouched — this only changes how they're reached, forwarding
/// "everything after my own verb" to each exactly as <c>Program.cs</c> used to.
/// <para>
/// <c>check</c> is new here: it's <c>doctor</c>'s old non-interactive entry point, unchanged in
/// behaviour, reached one level down. <c>setup</c> is deliberately not nested under <c>config</c> —
/// it stays a purely interactive, top-level experience; its review screen calls
/// <see cref="ReadinessChecks"/> in-process the same way <c>check</c> does here.
/// </para>
/// </summary>
public static class ConfigCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var rest = args[1..];
        return args[0].ToLowerInvariant() switch
        {
            "check" => await CheckAsync(rest),
            "get" => ConfigValueCommand.Get(rest),
            "set" => ConfigValueCommand.Set(rest),
            "cert" => CertCommand.Run(rest),
            "secret" => SecretCommand.Run(rest),
            "library" => await LibraryCommand.RunAsync(rest),
            "driver" => await DriverCommand.RunAsync(rest),
            var other => Unknown(other),
        };
    }

    /// <summary>Read-only. Exits 0 when nothing is <see cref="CheckStatus.Fail"/>, 1 otherwise — for a
    /// CI smoke test or a service wrapper that cannot answer an interactive prompt.</summary>
    private static async Task<int> CheckAsync(string[] args)
    {
        var asJson = CliOptions.Has(args, "--json");
        var context = ReadinessChecks.BuildContext(args);
        var results = await ReadinessChecks.RunChecksAsync(context);

        if (asJson)
        {
            // No free text mixed into machine-readable output — a CI reader parses this as JSON.
            Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            if (LegacyRootMigration.DetectAt(context.Root) is { } legacyRoot)
            {
                Console.WriteLine(LegacyRootMigration.Message(legacyRoot, context.Root));
                Console.WriteLine();
            }

            foreach (var result in results)
            foreach (var line in ReadinessChecks.FormatResult(result))
                Console.WriteLine(line);
        }

        return results.Any(r => r.Status == CheckStatus.Fail) ? 1 : 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown config subcommand '{sub}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine(
            """
            Usage:
              dbdatasync config check [--repo <path>] [--json]
              dbdatasync config get <key> [--repo <path>]
              dbdatasync config set <key> <value> [--repo <path>]
              dbdatasync config cert status|list|new-self-signed|enroll|renew|retrieve|templates|bind
              dbdatasync config secret set <ref> <value>|list [<ref> ...]|remove <ref>
              dbdatasync config library install|sync|list|uninstall
              dbdatasync config driver install|list|uninstall
            Run `dbdatasync config <name>` with no further arguments to see that group's own flags.
            """);
}
