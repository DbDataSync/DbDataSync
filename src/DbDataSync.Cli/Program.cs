using System.Reflection;
using DbDataSync.Cli;

if (args.Length == 0 || CliOptions.Has(args, "--help") || CliOptions.Has(args, "-h"))
{
    Help.Print();
    return args.Length == 0 ? 1 : 0;
}

var command = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

return command switch
{
    "serve" => await ServeCommand.RunAsync(rest),
    "service" => ServiceCommand.Run(rest),
    "tool" => ToolCommand.Run(rest),
    "health" => await HealthCommand.RunAsync(rest),
    "invite" => InviteCommand.Run(rest),
    "config" => await ConfigCommand.RunAsync(rest),
    "setup" => await SetupCommand.RunAsync(rest),
    "internal" => await InternalCommand.RunAsync(rest),
    "version" => Version(),
    _ => Unknown(command),
};

// AssemblyName.Version, not this — it's a strict 4-part numeric System.Version, and the SDK
// derives it from <Version>'s numeric core alone, silently dropping any prerelease label
// (-alpha.<seconds> on every dev build since the local-tool version convention moved into MSBuild;
// -beta on a release.yml prerelease tag). AssemblyInformationalVersionAttribute is what SDK-style
// projects stamp with the *whole* <Version> string, prerelease label included, precisely so a tool
// asked "what version are you" can answer honestly rather than truncating the one part of the
// string that says "this build isn't the real thing."
static int Version()
{
    var informational = typeof(Help).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    Console.WriteLine(informational ?? typeof(Help).Assembly.GetName().Version?.ToString() ?? "unknown");
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    Help.Print();
    return 1;
}
