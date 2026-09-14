using System.Runtime.Versioning;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <see cref="ServiceCommand.Run"/>'s own dispatch — argument validation only. <c>install</c>/
/// <c>uninstall</c>/<c>status</c> on Linux route straight to <see cref="SystemdService"/> against the
/// real environment (no injectable seam at this layer), which is exactly what
/// <see cref="SystemdServiceTests"/> exists to test safely instead — this file never calls those three
/// on this real host.
/// </summary>
public sealed class ServiceCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-service-command-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

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

    /// <summary>
    /// Phase 135's own repro: <c>LocalSystem</c> (the default, <c>--account</c> unspecified) used to
    /// return early from <c>GrantDataDirectoryAccess</c> and never touch the directory's ownership at
    /// all. Real <c>icacls.exe</c> execution against a real temp directory — this repo's own "real, not
    /// mocked" precedent for install-time OS interaction — proving ownership actually transfers to
    /// <c>SYSTEM</c> (icacls's own name for <c>LocalSystem</c>) rather than just asserting the method
    /// returns without throwing.
    /// </summary>
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void GrantDataDirectoryAccess_LocalSystem_TakesOwnershipInsteadOfReturningEarly()
    {
        ServiceCommand.GrantDataDirectoryAccess(_root, account: null);

        var owner = new System.Security.AccessControl.DirectorySecurity(_root, System.Security.AccessControl.AccessControlSections.Owner)
            .GetOwner(typeof(System.Security.Principal.NTAccount))!.Value;

        Assert.Contains("SYSTEM", owner, StringComparison.OrdinalIgnoreCase);
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
