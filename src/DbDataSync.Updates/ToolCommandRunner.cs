using System.Diagnostics;

namespace DbDataSync.Updates;

/// <param name="Output">Standard output and error together, for the log.</param>
public sealed record ToolCommandResult(int ExitCode, string Output);

/// <summary>Runs <c>dotnet &lt;arguments&gt;</c>. An interface so an update can be tested without touching a real
/// installation.</summary>
public interface IToolCommandRunner
{
    Task<ToolCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the real <c>dotnet</c>. Finds it through <c>DOTNET_ROOT</c> first — a service account's environment, or a
/// <c>sudo</c> one, may have no <c>PATH</c> worth trusting — and gives it a home directory when the environment has
/// none, which <c>dotnet tool</c> needs for its first-run state and package cache.
/// <para>
/// Runs in <paramref name="workingDirectory"/>, a directory of the caller's own, with a <c>nuget.config</c> there that
/// pins nuget.org. <c>dotnet tool</c> reads <c>nuget.config</c> from its working directory and every parent, so run
/// from the data directory — which the service can write — a compromised service could have added a package source
/// of its own to an install an operator runs as root.
/// </para>
/// </summary>
public sealed class ProcessToolCommandRunner(string workingDirectory) : IToolCommandRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<ToolCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ResolveDotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        Directory.CreateDirectory(workingDirectory);
        WritePinnedNuGetConfig(Path.Combine(workingDirectory, "nuget.config"));
        start.WorkingDirectory = workingDirectory;

        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_CLI_HOME")))
        {
            var home = Path.Combine(workingDirectory, "dotnet-home");
            Directory.CreateDirectory(home);
            start.Environment["DOTNET_CLI_HOME"] = home;
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new ToolCommandResult(process.ExitCode, (await output) + (await error));
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>Only nuget.org. Written by temp file and rename, so a planted file — or a symlink — at this path is
    /// replaced rather than written through. A snapshot's own folder is added on the command line, which adds to
    /// these sources rather than replacing them.</summary>
    internal static void WritePinnedNuGetConfig(string path)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
            </configuration>
            """);
        File.Move(temp, path, overwrite: true);
    }

    private static string ResolveDotnet()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
                return candidate;
        }

        return "dotnet";
    }
}
