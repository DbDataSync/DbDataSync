namespace DbDataSync.Cli.Tests;

/// <summary>
/// Phase 123's <c>dbdatasync tool install</c>/<c>tool uninstall</c>. This sandbox is Linux, so
/// <c>ToolCommand.Run</c>'s own <c>OperatingSystem.IsWindows()</c> branch genuinely runs the Unix path
/// here — tested directly through <see cref="FakeToolPathEnvironment"/>, the same reasoning
/// <c>SystemdServiceTests</c> gives for testing its own Linux path for real rather than only through a
/// fake. The Windows Machine-<c>PATH</c> logic has no OS branch of its own to fake, so it's tested as
/// the pure <see cref="ToolCommand.AddToPath"/>/<see cref="ToolCommand.RemoveFromPath"/> functions
/// instead — exactly what a Windows run of <c>ToolCommand</c> itself calls, unexercised here only
/// because nothing on this host can pretend to *be* Windows.
/// </summary>
public sealed class ToolCommandTests : IDisposable
{
    private readonly string _toolDir = Directory.CreateTempSubdirectory("dbdatasync-tool-test-").FullName;

    public ToolCommandTests() => File.WriteAllText(Path.Combine(_toolDir, "dbdatasync"), "#!/bin/sh\n");

    public void Dispose() => Directory.Delete(_toolDir, recursive: true);

    private string Executable => Path.Combine(_toolDir, "dbdatasync");

    [Fact]
    public void Install_NotElevated_PrintsTheSudoCommand_AndWritesNothing()
    {
        var env = new FakeToolPathEnvironment { IsElevated = false };

        var (exitCode, output) = RunCaptured(["install", "--dir", _toolDir], env);

        Assert.Equal(1, exitCode);
        Assert.Contains("sudo", output);
        Assert.Contains("tool install", output);
        Assert.Empty(env.Symlinks);
        Assert.Empty(env.ChmodCalls);
    }

    [Fact]
    public void Install_AsRoot_LinksUsrLocalBin_AndChmodsTheToolDir()
    {
        var env = new FakeToolPathEnvironment();

        var (exitCode, output) = RunCaptured(["install", "--dir", _toolDir], env);

        Assert.Equal(0, exitCode);
        Assert.Equal(Executable, env.Symlinks.GetValueOrDefault("/usr/local/bin/dbdatasync"));
        Assert.Contains(_toolDir, env.ChmodCalls);
        Assert.Contains("Next: `dbdatasync config check`", output);
    }

    [Fact]
    public void Install_Twice_IsANoOp_TheSecondTime()
    {
        var env = new FakeToolPathEnvironment();
        RunCaptured(["install", "--dir", _toolDir], env);

        var (exitCode, output) = RunCaptured(["install", "--dir", _toolDir], env);

        Assert.Equal(0, exitCode);
        Assert.Contains("already links to", output);
    }

    [Fact]
    public void Uninstall_RemovesTheSymlink_AndPrintsTheDotnetToolUninstallLine()
    {
        var env = new FakeToolPathEnvironment();
        RunCaptured(["install", "--dir", _toolDir], env);

        var (exitCode, output) = RunCaptured(["uninstall", "--dir", _toolDir], env);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("/usr/local/bin/dbdatasync", env.Symlinks.Keys);
        Assert.Contains($"dotnet tool uninstall --tool-path {_toolDir} DbDataSync", output);
    }

    [Fact]
    public void Uninstall_NotElevated_PrintsTheSudoCommand_AndRemovesNothing()
    {
        var env = new FakeToolPathEnvironment();
        RunCaptured(["install", "--dir", _toolDir], env);
        env.IsElevated = false;

        var (exitCode, output) = RunCaptured(["uninstall", "--dir", _toolDir], env);

        Assert.Equal(1, exitCode);
        Assert.Contains("sudo", output);
        Assert.Single(env.Symlinks);
    }

    [Fact]
    public void Uninstall_NothingInstalled_SaysSoAndStillExitsZero()
    {
        var env = new FakeToolPathEnvironment();

        var (exitCode, output) = RunCaptured(["uninstall", "--dir", _toolDir], env);

        Assert.Equal(0, exitCode);
        Assert.Contains("nothing to remove", output);
    }

    [Fact]
    public void Install_ADirUnderTheUserProfile_WarnsButStillProceeds()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        Assert.False(string.IsNullOrEmpty(home)); // this sandbox always has one
        var underProfile = Path.Combine(home!, $".dbdatasync-tool-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(underProfile);
        try
        {
            File.WriteAllText(Path.Combine(underProfile, "dbdatasync"), "#!/bin/sh\n");
            var env = new FakeToolPathEnvironment();

            var (exitCode, output) = RunCaptured(["install", "--dir", underProfile], env);

            Assert.Equal(0, exitCode);
            Assert.Contains("looks like a per-user install", output);
            Assert.Contains(CliOptions.DefaultToolDir, output);
            Assert.NotEmpty(env.Symlinks);
        }
        finally
        {
            Directory.Delete(underProfile, recursive: true);
        }
    }

    [Fact]
    public void NoArguments_PrintsUsageAndFails()
    {
        var (exitCode, output) = RunCaptured([], new FakeToolPathEnvironment());

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage", output);
    }

    [Fact]
    public void UnknownSubcommand_Fails()
    {
        var (exitCode, output) = RunCaptured(["bogus"], new FakeToolPathEnvironment());

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown tool subcommand 'bogus'", output);
    }

    [Fact]
    public void AddToPath_ANewEntry_AppendsIt()
    {
        var (path, alreadyPresent) = ToolCommand.AddToPath(@"C:\Windows;C:\Windows\System32", @"C:\Program Files\DbDataSync");

        Assert.False(alreadyPresent);
        Assert.Equal(@"C:\Windows;C:\Windows\System32;C:\Program Files\DbDataSync", path);
    }

    [Fact]
    public void AddToPath_AlreadyPresent_ChangesNothing()
    {
        const string toolDir = @"C:\Program Files\DbDataSync";
        var original = $@"C:\Windows;{toolDir};C:\Windows\System32";

        var (path, alreadyPresent) = ToolCommand.AddToPath(original, toolDir);

        Assert.True(alreadyPresent);
        Assert.Equal(original, path);
    }

    [Fact]
    public void AddToPath_AlreadyPresentWithATrailingBackslash_StillMatches()
    {
        const string toolDir = @"C:\Program Files\DbDataSync";
        var original = $@"C:\Windows;{toolDir}\;C:\Windows\System32";

        var (_, alreadyPresent) = ToolCommand.AddToPath(original, toolDir);

        Assert.True(alreadyPresent);
    }

    [Fact]
    public void RemoveFromPath_AnExistingEntry_DropsExactlyThat_LeavingTheRestByteForByte()
    {
        const string toolDir = @"C:\Program Files\DbDataSync";
        var original = $@"C:\Windows;{toolDir};C:\Windows\System32";

        var (path, removed) = ToolCommand.RemoveFromPath(original, toolDir);

        Assert.True(removed);
        Assert.Equal(@"C:\Windows;C:\Windows\System32", path);
    }

    [Fact]
    public void RemoveFromPath_NotPresent_ChangesNothing()
    {
        const string original = @"C:\Windows;C:\Windows\System32";

        var (path, removed) = ToolCommand.RemoveFromPath(original, @"C:\Program Files\DbDataSync");

        Assert.False(removed);
        Assert.Equal(original, path);
    }

    private static (int ExitCode, string Output) RunCaptured(string[] args, IToolPathEnvironment env)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = ToolCommand.Run(args, env);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
