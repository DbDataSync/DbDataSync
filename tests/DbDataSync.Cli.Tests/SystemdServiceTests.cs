namespace DbDataSync.Cli.Tests;

/// <summary>
/// Phase 111's Linux service registration. <see cref="SystemdService.RenderUnit"/> is pure and tested
/// directly with no fake needed; <c>Install</c>/<c>Uninstall</c>/<c>Status</c> are driven through
/// <see cref="FakeSystemdEnvironment"/> so nothing here ever touches this host's real systemd, its
/// real <c>/etc/systemd/system</c>, or its real user database — even though, unlike the Windows
/// service path, all of that genuinely runs on this sandbox.
/// </summary>
public sealed class SystemdServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-systemd-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RenderUnit_DefaultRoot_IncludesStateDirectoryAndHardening()
    {
        var unit = SystemdService.RenderUnit("/usr/bin/dbdatasync", "/var/lib/dbdatasync", "http://localhost:5080", "dbdatasync");

        Assert.Contains("Type=notify", unit);
        Assert.Contains("""ExecStart="/usr/bin/dbdatasync" serve --repo "/var/lib/dbdatasync" --url http://localhost:5080""", unit);
        Assert.Contains("User=dbdatasync", unit);
        Assert.Contains("Group=dbdatasync", unit);
        Assert.Contains("WorkingDirectory=/var/lib/dbdatasync", unit);
        Assert.Contains("StateDirectory=dbdatasync", unit);
        Assert.Contains("ProtectSystem=strict", unit);
        Assert.Contains("ReadWritePaths=/var/lib/dbdatasync", unit);
        Assert.Contains("WantedBy=multi-user.target", unit);
    }

    [Fact]
    public void RenderUnit_NonDefaultRoot_SkipsHardeningButStillGrantsReadWriteAccess()
    {
        var unit = SystemdService.RenderUnit("/usr/bin/dbdatasync", "/srv/dbdatasync", "http://localhost:5080", "dbdatasync");

        Assert.DoesNotContain("NoNewPrivileges=yes", unit);
        Assert.DoesNotContain("\nProtectSystem=strict", unit);
        Assert.DoesNotContain("StateDirectory=", unit);
        Assert.Contains("ReadWritePaths=/srv/dbdatasync", unit);
    }

    [Fact]
    public void RenderUnit_QuotesTheExecutableAndRepoPathsForSystemdsOwnParsing()
    {
        var unit = SystemdService.RenderUnit("/usr/bin/dbdatasync tool", "/var/lib/db data sync", "http://localhost:5080", "dbdatasync");

        Assert.Contains("\"/usr/bin/dbdatasync tool\"", unit);
        Assert.Contains("\"/var/lib/db data sync\"", unit);
    }

    [Fact]
    public void RenderUnit_WithADotnetRoot_EmitsItAsAScopedEnvironmentLine()
    {
        var unit = SystemdService.RenderUnit(
            "/usr/bin/dbdatasync", "/var/lib/dbdatasync", "http://localhost:5080", "dbdatasync", "/usr/lib/dotnet");

        Assert.Contains("Environment=DOTNET_ROOT=/usr/lib/dotnet", unit);
    }

    [Fact]
    public void RenderUnit_WithNoDotnetRoot_OmitsTheLineEntirely()
    {
        var unit = SystemdService.RenderUnit("/usr/bin/dbdatasync", "/var/lib/dbdatasync", "http://localhost:5080", "dbdatasync");

        Assert.DoesNotContain("DOTNET_ROOT", unit);
    }

    [Fact]
    public void ResolveDotnetRoot_OnThisRealSandbox_FindsARealDotnetExecutable()
    {
        // Read-only and side-effect-free — proves the RuntimeEnvironment-walk-up half of phase 123's
        // "fall back to omitting the line rather than emitting a wrong one" promise actually lands on
        // a directory that really does contain dotnet, on the one environment this can be checked for
        // real (whatever CI/dev box happens to run this).
        var root = SystemdService.ResolveDotnetRoot();

        Assert.NotNull(root);
        Assert.True(
            File.Exists(Path.Combine(root!, "dotnet")) || File.Exists(Path.Combine(root!, "dotnet.exe")));
    }

    [Fact]
    public void Install_ExecutableUnderHomeWithTheHardenedDefaultRoot_RefusesRatherThanRegisteringABrokenUnit()
    {
        var env = new FakeSystemdEnvironment();
        var homeExecutable = Path.Combine(
            Environment.GetEnvironmentVariable("HOME") ?? "/home/someone", "dbdatasync");

        var (exitCode, output) = RunCaptured(() => SystemdService.Install(
            ["--user", "testsvc"], env, executableOverride: homeExecutable));

        Assert.Equal(1, exitCode);
        Assert.Contains("ProtectHome=yes", output);
        Assert.Contains("tool install", output);
        Assert.Null(env.WrittenUnit);
        Assert.Empty(env.CreatedUsers);
    }

    [Fact]
    public void Install_ExecutableUnderHomeWithANonHardenedRoot_WarnsButStillRegisters()
    {
        var env = new FakeSystemdEnvironment();
        var homeExecutable = Path.Combine(
            Environment.GetEnvironmentVariable("HOME") ?? "/home/someone", "dbdatasync");

        var (exitCode, output) = RunCaptured(() => SystemdService.Install(
            ["--repo", _root, "--user", "testsvc"], env, executableOverride: homeExecutable));

        Assert.Equal(0, exitCode);
        Assert.Contains("Warning:", output);
        Assert.Contains("user profile", output);
        Assert.NotNull(env.WrittenUnit);
    }

    [Fact]
    public void Install_UserDoesNotExist_CreatesItThenWritesEnablesAndReloads()
    {
        var env = new FakeSystemdEnvironment();

        var exitCode = SystemdService.Install(["--repo", _root, "--url", "http://localhost:5080", "--user", "testsvc"], env);

        Assert.Equal(0, exitCode);
        Assert.Contains("testsvc", env.CreatedUsers);
        Assert.NotNull(env.WrittenUnit);
        Assert.Equal(SystemdService.UnitPath, env.WrittenUnit!.Value.Path);
        Assert.Contains(_root, env.WrittenUnit.Value.Content);
        Assert.Contains((_root, "testsvc", "testsvc"), env.ChownCalls);
        Assert.Contains(env.SystemctlCalls, call => call is ["daemon-reload"]);
        Assert.Contains(env.SystemctlCalls, call => call is ["enable", SystemdService.UnitName]);
        // Enabled, not started — the operator runs `systemctl start` themselves and sees its output.
        Assert.DoesNotContain(env.SystemctlCalls, call => call.Contains("start"));
    }

    [Fact]
    public void Install_UserAlreadyExists_DoesNotTryToCreateItAgain()
    {
        var env = new FakeSystemdEnvironment();
        env.ExistingUsers.Add("testsvc");

        var exitCode = SystemdService.Install(["--repo", _root, "--user", "testsvc"], env);

        Assert.Equal(0, exitCode);
        Assert.Empty(env.CreatedUsers);
    }

    [Fact]
    public void Install_CannotCreateUser_FailsWithThePermissionSentenceAndWritesNothing()
    {
        var env = new FakeSystemdEnvironment { CreateSystemUserResult = 1 };

        var (exitCode, output) = RunCaptured(() => SystemdService.Install(["--repo", _root, "--user", "testsvc"], env));

        Assert.Equal(1, exitCode);
        Assert.Contains("needs root", output);
        Assert.Contains("sudo dbdatasync service install", output);
        Assert.Null(env.WrittenUnit);
        Assert.Empty(env.SystemctlCalls);
    }

    [Fact]
    public void Uninstall_DisablesRemovesTheUnitAndReloads()
    {
        var env = new FakeSystemdEnvironment();

        var exitCode = SystemdService.Uninstall(["--repo", _root], env);

        Assert.Equal(0, exitCode);
        Assert.Contains(env.SystemctlCalls, call => call is ["disable", "--now", SystemdService.UnitName]);
        Assert.Contains(SystemdService.UnitPath, env.DeletedUnitPaths);
        Assert.Contains(env.SystemctlCalls, call => call is ["daemon-reload"]);
    }

    [Fact]
    public void Install_ThenUninstall_WritesThenClearsTheServiceRegistrationMarker()
    {
        var env = new FakeSystemdEnvironment();

        SystemdService.Install(["--repo", _root, "--user", "testsvc"], env);
        var afterInstall = ServiceRegistration.Read(_root);
        Assert.NotNull(afterInstall);
        Assert.Equal("testsvc", afterInstall!.Account);
        Assert.Equal("linux", afterInstall.Platform);

        SystemdService.Uninstall(["--repo", _root], env);
        Assert.Null(ServiceRegistration.Read(_root));
    }

    [Fact]
    public void Status_PassesThroughToSystemctlStatusNoPager()
    {
        var env = new FakeSystemdEnvironment { SystemctlResult = 3 };

        var exitCode = SystemdService.Status(env);

        Assert.Equal(3, exitCode);
        Assert.Contains(env.SystemctlCalls, call => call is ["status", SystemdService.UnitName, "--no-pager"]);
    }

    /// <summary>
    /// The one test here against the *real* environment, not the fake — read-only and side-effect-free
    /// (<c>id -u</c> answers whether a user exists; it never creates, deletes, or changes anything), so
    /// it's safe to run against this host directly. Proves <see cref="RealSystemdEnvironment"/>'s own
    /// process-invocation plumbing (argument passing, exit-code capture) actually works on this real
    /// Linux sandbox, which the fake-driven tests above cannot — they only prove <see
    /// cref="SystemdService"/> calls the interface correctly, not that the interface's real
    /// implementation does what it says.
    /// </summary>
    [Fact]
    public void RealSystemdEnvironment_UserExists_TellsRootFromANameThatCannotExist()
    {
        var env = new RealSystemdEnvironment();

        Assert.True(env.UserExists("root"));
        Assert.False(env.UserExists("dbdatasync-test-user-that-should-never-exist-anywhere"));
    }

    /// <summary>Same reasoning as the test above, for the <c>systemctl</c> half of the seam —
    /// <c>--version</c> is read-only and proves the process actually launches and its exit code comes
    /// back correctly, without registering, starting, or changing anything.</summary>
    [Fact]
    public void RealSystemdEnvironment_RunSystemctl_ActuallyInvokesSystemctl()
    {
        var env = new RealSystemdEnvironment();

        Assert.Equal(0, env.RunSystemctl("--version"));
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
}
