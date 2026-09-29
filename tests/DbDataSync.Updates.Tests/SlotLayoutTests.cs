namespace DbDataSync.Updates.Tests;

/// <summary>
/// Stands in for <c>dotnet tool install --tool-path &lt;slot&gt; … --version &lt;v&gt;</c>: lays the payload down where the
/// real one would (<see cref="SlotPathsTests.PutPayload"/>), unless told to fail.
/// </summary>
internal sealed class InstallingRunner(int exitCode = 0) : IToolCommandRunner
{
    public List<string> Commands { get; } = [];

    public Task<ToolCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Commands.Add(string.Join(' ', arguments));
        if (exitCode == 0 && arguments is ["tool", "install", "--tool-path", var slot, ..])
            SlotPathsTests.PutPayload(slot, arguments[arguments.ToList().IndexOf("--version") + 1]);
        return Task.FromResult(new ToolCommandResult(exitCode, exitCode == 0 ? "ok" : "it broke"));
    }
}

public class SlotLayoutTests
{
    [Fact]
    public void Flip_WritesThePointer_AndLeavesNoTempFileBehind()
    {
        using var root = new TempDirectory();
        var layout = new SlotLayout(root.Path);
        Assert.Null(layout.Current);
        Assert.False(layout.Exists);

        layout.Flip("b");
        Assert.Equal("b", layout.Current);
        layout.Flip("a");
        Assert.Equal("a", layout.Current);

        Assert.True(layout.Exists);
        Assert.Equal(["current.txt"], Directory.GetFiles(root.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Flip_RefusesAnythingButASlot()
    {
        using var root = new TempDirectory();
        Assert.Throws<ArgumentException>(() => new SlotLayout(root.Path).Flip("../b"));
    }

    [Fact]
    public void Slot_ReportsWhatItHolds()
    {
        using var root = new TempDirectory();
        var layout = new SlotLayout(root.Path);
        SlotPathsTests.PutPayload(SlotPaths.SlotDirectory(root.Path, "a"), "2026.9.18.1918");

        Assert.Equal("2026.9.18.1918", layout.Slot("a").Version);
        Assert.Null(layout.Slot("b").Version);
        Assert.False(layout.Slot("b").Ambiguous);
    }

    [Fact]
    public void Check_WarnsWhenThePointerIsMissing_OrNamesAnEmptySlot()
    {
        using var root = new TempDirectory();
        var layout = new SlotLayout(root.Path);
        SlotPathsTests.PutPayload(SlotPaths.SlotDirectory(root.Path, "a"), "2026.9.18.1918");

        var missing = Assert.Single(layout.Check());
        Assert.Equal(SlotCheckLevel.Warning, missing.Level);
        Assert.Contains("current.txt is missing", missing.Message);

        layout.Flip("b");
        var empty = Assert.Single(layout.Check());
        Assert.Equal(SlotCheckLevel.Warning, empty.Level);
        Assert.Contains("names slot b, which is empty", empty.Message);
        Assert.Contains("write \"a\"", empty.Message);
    }

    [Fact]
    public void Check_RunningTheOlderOfTwo_IsANote_WithTheWayForward()
    {
        using var root = new TempDirectory();
        var layout = new SlotLayout(root.Path);
        SlotPathsTests.PutPayload(SlotPaths.SlotDirectory(root.Path, "a"), "2026.9.16.1005");
        SlotPathsTests.PutPayload(SlotPaths.SlotDirectory(root.Path, "b"), "2026.9.18.1918");
        layout.Flip("a");

        var note = Assert.Single(layout.Check());
        Assert.Equal(SlotCheckLevel.Note, note.Level);
        Assert.Contains("dbdatasync update --to 2026.9.18.1918 --apply", note.Message);

        layout.Flip("b");
        Assert.Empty(layout.Check());
    }

    // --- installing into a slot -----------------------------------------------------------------------------

    private static UpdateStateStore Store(TempDirectory root) => new(new UpdateWorkspace(Path.Combine(root.Path, "data")));

    [Fact]
    public async Task Install_EmptiesTheSlotFirst_ThenInstalls_AndChecksTheResult()
    {
        using var root = new TempDirectory();
        var layout = new SlotLayout(Path.Combine(root.Path, "tool"));
        var slot = SlotPaths.SlotDirectory(layout.Root, "b");
        SlotPathsTests.PutPayload(slot, "2026.9.11.532");
        var runner = new InstallingRunner();

        var result = await new SlotInstaller(runner, Store(root)).InstallAsync(layout, "b", "2026.9.18.1918", null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([$"tool install --tool-path {slot} DbDataSync --version 2026.9.18.1918"], runner.Commands);
        Assert.Equal(["2026.9.18.1918"], SlotPaths.InstalledVersions(slot));
    }

    [Fact]
    public async Task Install_TheVersionAlreadyThere_RunsNothing()
    {
        using var root = new TempDirectory();
        var layout = new SlotLayout(root.Path);
        SlotPathsTests.PutPayload(SlotPaths.SlotDirectory(root.Path, "b"), "2026.9.18.1918");
        var runner = new InstallingRunner();

        var result = await new SlotInstaller(runner, Store(root)).InstallAsync(layout, "b", "2026.9.18.1918", null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("already holds", result.Message);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Install_AFailedInstall_SaysNothingWasSwitched()
    {
        using var root = new TempDirectory();
        var layout = new SlotLayout(root.Path);

        var result = await new SlotInstaller(new InstallingRunner(exitCode: 1), Store(root))
            .InstallAsync(layout, "b", "2026.9.18.1918", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("failed (exit 1)", result.Message);
        Assert.Contains("Nothing was switched", result.Message);
    }

    [Fact]
    public async Task Install_ThatSucceedsButLeavesNothingRunnable_IsAFailure()
    {
        using var root = new TempDirectory();
        var layout = new SlotLayout(root.Path);

        var result = await new SlotInstaller(new FakeToolRunner(), Store(root))
            .InstallAsync(layout, "b", "2026.9.18.1918", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("does not hold a runnable 2026.9.18.1918", result.Message);
    }

    // --- the launcher ---------------------------------------------------------------------------------------------

    private static string PutLauncher(string payloadDirectory, string content = "launcher v1")
    {
        var directory = Path.Combine(payloadDirectory, SlotPaths.LauncherDirectoryName, SlotPaths.PortableRuntimeIdentifier()!);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dbdatasync"), content);
        File.WriteAllText(Path.Combine(directory, "dbdatasync.dll"), content + " dll");
        File.WriteAllText(Path.Combine(directory, "dbdatasync.pdb"), "symbols");
        return directory;
    }

    [Fact]
    public void LauncherInstaller_CopiesOnlyWhatDiffers_AndNeverThePdb()
    {
        using var payload = new TempDirectory();
        using var root = new TempDirectory();
        var source = PutLauncher(payload.Path);
        Assert.Equal(source, LauncherInstaller.SourceDirectory(payload.Path));

        Assert.Equal(2, LauncherInstaller.Install(source, root.Path));
        Assert.Equal(0, LauncherInstaller.Install(source, root.Path));
        Assert.False(File.Exists(Path.Combine(root.Path, "dbdatasync.pdb")));

        PutLauncher(payload.Path, "launcher v2");
        Assert.Equal(2, LauncherInstaller.Install(source, root.Path));
        Assert.Equal("launcher v2", File.ReadAllText(Path.Combine(root.Path, "dbdatasync")));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public void LauncherInstaller_APackageWithNoLauncher_HasNoSource()
    {
        using var payload = new TempDirectory();
        Assert.Null(LauncherInstaller.SourceDirectory(payload.Path));
    }

    // --- converting a pre-196L install -------------------------------------------------------------------------

    /// <summary>A pre-196L <c>--tool-path</c> install: its shim, and the running version's package in its store.</summary>
    private static string LegacyInstall(TempDirectory root, string version)
    {
        var tool = Path.Combine(root.Path, "tool");
        var kept = Path.Combine(tool, ".store", "dbdatasync", version, "dbdatasync", version);
        Directory.CreateDirectory(kept);
        File.WriteAllText(Path.Combine(kept, $"dbdatasync.{version}.nupkg"), "package");
        File.WriteAllText(Path.Combine(tool, "dbdatasync"), "legacy shim");
        return tool;
    }

    [Fact]
    public async Task Migration_PutsTheRunningVersionInSlotA_FromItsOwnPackage_ThenThePointer_ThenTheLauncher()
    {
        using var root = new TempDirectory();
        using var payload = new TempDirectory();
        var tool = LegacyInstall(root, "2026.9.16.1005");
        PutLauncher(payload.Path);
        var runner = new InstallingRunner();

        var result = await new SlotMigration(runner, Store(root)).ConvertAsync(tool, "2026.9.16.1005+abc", payload.Path, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var command = Assert.Single(runner.Commands);
        Assert.StartsWith($"tool install --tool-path {SlotPaths.SlotDirectory(tool, "a")} DbDataSync --add-source ", command);
        Assert.EndsWith("--version 2026.9.16.1005", command);
        var layout = new SlotLayout(tool);
        Assert.Equal("a", layout.Current);
        Assert.Equal("2026.9.16.1005", layout.Slot("a").Version);
        Assert.Equal("launcher v1", File.ReadAllText(Path.Combine(tool, "dbdatasync")));

        // The legacy store is left for a later run from the launcher; this process is running from it.
        Assert.True(Directory.Exists(Path.Combine(tool, ".store")));
        Assert.True(SlotMigration.RemoveLegacyStore(layout));
        Assert.False(Directory.Exists(Path.Combine(tool, ".store")));
    }

    [Fact]
    public async Task Migration_RunAgainAfterAnInterruption_SkipsWhatIsDone()
    {
        using var root = new TempDirectory();
        using var payload = new TempDirectory();
        var tool = LegacyInstall(root, "2026.9.16.1005");
        PutLauncher(payload.Path);
        await new SlotMigration(new InstallingRunner(), Store(root)).ConvertAsync(tool, "2026.9.16.1005", payload.Path, CancellationToken.None);
        var again = new InstallingRunner();

        var result = await new SlotMigration(again, Store(root)).ConvertAsync(tool, "2026.9.16.1005", payload.Path, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(again.Commands);
    }

    [Fact]
    public async Task Migration_WithNoLauncherForThisPlatform_ChangesNothing()
    {
        using var root = new TempDirectory();
        using var payload = new TempDirectory();
        var tool = LegacyInstall(root, "2026.9.16.1005");
        var runner = new InstallingRunner();

        var result = await new SlotMigration(runner, Store(root)).ConvertAsync(tool, "2026.9.16.1005", payload.Path, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no launcher for this platform", result.Message);
        Assert.Empty(runner.Commands);
        Assert.False(new SlotLayout(tool).Exists);
        Assert.Equal("legacy shim", File.ReadAllText(Path.Combine(tool, "dbdatasync")));
    }

    [Fact]
    public async Task Migration_ASnapshotWhosePackageIsGone_CannotComeFromNuGet_AndChangesNothing()
    {
        using var root = new TempDirectory();
        using var payload = new TempDirectory();
        var tool = LegacyInstall(root, "2026.9.16.1005");
        PutLauncher(payload.Path);

        var result = await new SlotMigration(new InstallingRunner(), Store(root))
            .ConvertAsync(tool, "2026.9.19.1432-snapshot.g65615e7", payload.Path, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("nuget.org does not have it", result.Message);
        Assert.False(new SlotLayout(tool).Exists);
    }

    [Fact]
    public void RemoveLegacyStore_NeverTouchesAnInstallThatHasNoSlots()
    {
        using var root = new TempDirectory();
        var tool = LegacyInstall(root, "2026.9.16.1005");

        Assert.False(SlotMigration.RemoveLegacyStore(new SlotLayout(tool)));
        Assert.True(Directory.Exists(Path.Combine(tool, ".store")));
    }

    // --- the commands the console shows -------------------------------------------------------------------------

    [Fact]
    public void CliCommands_OnLinux_UseSudoToChangeAnything_AndNameTheRepoWhenGiven()
    {
        var commands = UpdateCliCommands.For(windows: false, "/srv/my data");

        Assert.Equal("dbdatasync update --list", commands.List);
        Assert.Equal("dbdatasync update --status --repo \"/srv/my data\"", commands.Status);
        Assert.Equal("sudo dbdatasync update --to 2026.9.18.1918 --apply --repo \"/srv/my data\"", commands.ApplyFor("2026.9.18.1918"));
        Assert.Equal("sudo dbdatasync update --rollback --repo \"/srv/my data\"", commands.Rollback);
    }

    [Fact]
    public void CliCommands_OnWindows_SayToElevate_InsteadOfSudo()
    {
        var commands = UpdateCliCommands.For(windows: true, null);

        Assert.Equal("dbdatasync update --to {version} --apply", commands.Apply);
        Assert.Contains("Run as administrator", commands.Where);
        Assert.DoesNotContain("sudo", commands.Rollback);
    }
}
