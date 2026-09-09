namespace DbDataSync.Cli.Tests;

/// <summary>
/// <see cref="ServiceCommand.Run"/>'s own dispatch — argument validation only. <c>install</c>/
/// <c>uninstall</c>/<c>status</c> on Linux route straight to <see cref="SystemdService"/> against the
/// real environment (no injectable seam at this layer), which is exactly what
/// <see cref="SystemdServiceTests"/> exists to test safely instead — this file never calls those three
/// on this real host.
/// </summary>
public sealed class ServiceCommandTests
{
    [Fact]
    public void Run_NoArguments_PrintsUsageAndFails()
    {
        var (exitCode, output) = RunCaptured([]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage", output);
    }

    [Fact]
    public void Run_UnknownSubcommand_FailsWithoutTouchingTheRealEnvironment()
    {
        var (exitCode, output) = RunCaptured(["bogus"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown service command 'bogus'", output);
    }

    private static (int ExitCode, string Output) RunCaptured(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = ServiceCommand.Run(args);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
