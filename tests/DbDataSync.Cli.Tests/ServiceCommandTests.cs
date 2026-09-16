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


    /// <summary>
    /// The refusal that replaced a crash. Run non-elevated on a real Windows shell,
    /// <c>service install</c> used to rewrite part of the data directory's ACLs and then die with an
    /// unhandled <c>SecurityException</c> out of <c>EventLog.SourceExists</c> — stack trace, exit code
    /// 127, no mention of elevation, half-modified directory. Phase 136's follow-up doc listed this
    /// case as unverified; it reproduced exactly as feared.
    /// <para>
    /// Driven through the test-only <c>elevatedOverride</c> because CI's <c>windows-latest</c> runners
    /// are elevated, so the branch is otherwise unreachable there — the same reason
    /// <see cref="SystemdService.Install"/> takes an <c>executableOverride</c>. <c>_root</c> is passed
    /// and then asserted untouched, which is the half of this that matters: refusing late would still
    /// have left the directory changed.
    /// </para>
    /// </summary>
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void Install_NotElevated_RefusesBeforeTouchingAnything()
    {
        var ownerBefore = new System.Security.AccessControl.DirectorySecurity(
                _root, System.Security.AccessControl.AccessControlSections.Owner)
            .GetOwner(typeof(System.Security.Principal.NTAccount))!.Value;

        var (exitCode, output) = RunCaptured(
            () => ServiceCommand.Install(["install", "--repo", _root], elevatedOverride: false));

        Assert.Equal(1, exitCode);
        Assert.Contains("Administrator", output);
        Assert.Contains("Nothing has been changed.", output);

        var ownerAfter = new System.Security.AccessControl.DirectorySecurity(
                _root, System.Security.AccessControl.AccessControlSections.Owner)
            .GetOwner(typeof(System.Security.Principal.NTAccount))!.Value;

        Assert.Equal(ownerBefore, ownerAfter);
    }


    /// <summary>
    /// The quieter half of the same bug <see cref="Install_NotElevated_RefusesBeforeTouchingAnything"/>
    /// covers. <c>Uninstall</c> deleted the local registration record *before* running
    /// <c>sc delete</c>, so a non-elevated run reported sc.exe's own failure while having already told
    /// the rest of the tool no service was registered — and that record is what
    /// <see cref="ReadinessChecks"/> and <c>ServeCommand</c>'s startup-failure message read to name a
    /// registered service's account. Losing it is how an Error 1053 goes back to naming nothing, which
    /// is the whole thing phases 135 and 136 exist to prevent.
    /// </summary>
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void Uninstall_NotElevated_RefusesAndKeepsTheRegistrationRecord()
    {
        ServiceRegistration.Write(_root, "LocalSystem", "windows");

        var (exitCode, output) = RunCaptured(
            () => ServiceCommand.Uninstall(["uninstall", "--repo", _root], elevatedOverride: false));

        Assert.Equal(1, exitCode);
        Assert.Contains("Administrator", output);
        Assert.NotNull(ServiceRegistration.Read(_root));
    }

    private static (int ExitCode, string Output) RunCaptured(Func<int> action)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            return (action(), output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
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
