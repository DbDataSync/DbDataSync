namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync config check|cert|secret|library|driver</c> — phase 115's dispatcher, forwarding
/// "everything after my own verb" to the existing command classes unchanged. These are thin routing
/// tests: full behavior for each nested group is already covered by its own suite
/// (<see cref="SecretCommandTests"/> here; <c>DbDataSync.Libraries.Tests</c> /
/// <c>DbDataSync.Drivers.*.Tests</c> elsewhere) — <c>cert</c> has no CLI test at all, Windows-only and
/// untestable in this environment, so there is nothing to route-test beyond the platform gate.
/// </summary>
public sealed class ConfigCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-config-tests-").FullName;
    private readonly string _secretRef = $"dbdatasync:test:{Guid.NewGuid():N}";

    public void Dispose()
    {
        SecretCommand.Run(["remove", _secretRef]);
        GitTempDirectory.DeleteRecursively(_root);
    }

    [Fact]
    public async Task NoSubcommand_PrintsUsageAndFails()
    {
        var (exitCode, output) = await Run([]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage", output);
    }

    [Fact]
    public async Task UnknownSubcommand_PrintsUsageAndFails()
    {
        var (exitCode, output) = await Run(["bogus"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown config subcommand 'bogus'", output);
    }

    [Fact]
    public async Task Check_ForwardsToTheReadinessEngine()
    {
        ServeCommand.Prepare(_root);

        var (exitCode, output) = await Run(["check", "--repo", _root]);

        // BindingCheck fails (nothing is listening), but Repo/State store/Auth all report Ok — proof
        // this reached the real check list, not just an empty/placeholder response.
        Assert.Equal(1, exitCode);
        Assert.Contains("Repo:", output);
        Assert.Contains("State store:", output);
    }

    [Fact]
    public async Task Secret_ForwardsToSecretCommand()
    {
        var (setCode, _) = await Run(["secret", "set", _secretRef, "sup3r-secret"]);
        Assert.Equal(0, setCode);

        var (listCode, output) = await Run(["secret", "list", _secretRef]);

        Assert.Equal(0, listCode);
        Assert.Contains(_secretRef, output);
        Assert.Contains("set", output);
        Assert.DoesNotContain("sup3r-secret", output);
    }

    [Fact]
    public async Task Library_ForwardsToLibraryCommand()
    {
        var (exitCode, output) = await Run(["library", "list", "--repo", _root]);

        Assert.Equal(0, exitCode);
        Assert.Contains("No libraries installed.", output);
    }

    [Fact]
    public async Task Driver_ForwardsToDriverCommand()
    {
        var (exitCode, output) = await Run(["driver", "list", "--repo", _root]);

        Assert.Equal(0, exitCode);
        Assert.Contains("No drivers installed.", output);
    }

    [Fact]
    public async Task Cert_ForwardsToCertCommand()
    {
        // "list" (unlike "status"/"use-pem"/"use-pfx" since phase 113) stays Windows-only, gated the
        // same way whether reached directly or through `config` — this environment is Linux, so the
        // platform message is what proves the forward happened.
        var (exitCode, output) = await Run(["cert", "list"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Windows-only", output);
    }

    private static async Task<(int ExitCode, string Output)> Run(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = await ConfigCommand.RunAsync(args);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
