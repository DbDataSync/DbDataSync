namespace DbDataSync.Updates.Tests;

/// <summary>
/// The applier for a caller that **is** the operator — the CLI, which stops the service, installs, starts it and
/// judges the result itself. Nothing here crosses a trust boundary; the privileged step that does is
/// <see cref="PrivilegedApplyTests"/>.
/// </summary>
public class UpdateApplierTests
{
    private const string Previous = ApplierWorld.Running;

    // --- installing now --------------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyNow_InstallsWithoutLeavingAnUpdateOnTrial_AndKeepsTheOldPackageAside()
    {
        using var world = new ApplierWorld();

        var result = await world.Applier.ApplyNowAsync(world.Full("2026.9.19.100"), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Equal([$"tool update --tool-path {world.Tools} DbDataSync --version 2026.9.19.100"], world.Runner.Commands);
        Assert.Null(world.Store.ReadApplied());
        Assert.Equal($"package of {Previous}", File.ReadAllText(Path.Combine(world.Workspace.RollbackDirectory, $"DbDataSync.{Previous}.nupkg")));
        var state = world.Store.ReadState().Current!;
        Assert.Equal(UpdatePhase.Restarting, state.Phase);
        Assert.Equal(Previous, state.FromVersion);
        Assert.Contains("tool update", File.ReadAllText(world.Workspace.LogPath));
    }

    [Fact]
    public async Task ALowerVersion_UninstallsThenInstalls()
    {
        using var world = new ApplierWorld();

        await world.Applier.ApplyNowAsync(world.Full("2026.9.11.532"), CancellationToken.None);

        Assert.Equal(
            [
                $"tool uninstall --tool-path {world.Tools} DbDataSync",
                $"tool install --tool-path {world.Tools} DbDataSync --version 2026.9.11.532",
            ],
            world.Runner.Commands);
    }

    [Fact]
    public async Task ASnapshot_IsInstalledFromTheFolderItWasGiven()
    {
        using var world = new ApplierWorld();

        await world.Applier.ApplyNowAsync(world.Full(ApplierWorld.Snapshot, channel: ReleaseChannel.Snapshot, source: "/staged/x"), CancellationToken.None);

        Assert.Equal(
            [$"tool update --tool-path {world.Tools} DbDataSync --add-source /staged/x --version {ApplierWorld.Snapshot}"],
            world.Runner.Commands);
    }

    [Fact]
    public async Task TheVersionAlreadyInstalled_ChangesNothing()
    {
        using var world = new ApplierWorld();

        var result = await world.Applier.ApplyNowAsync(world.Full(Previous), CancellationToken.None);

        Assert.Equal(ApplyOutcome.NothingToDo, result.Outcome);
        Assert.Empty(world.Runner.Commands);
        Assert.Equal(UpdatePhase.Succeeded, world.Store.ReadState().Current!.Phase);
    }

    [Fact]
    public async Task AFailedUpdate_IsRecorded_AndLeavesTheInstalledVersion()
    {
        using var world = new ApplierWorld(dotnetExit: _ => 1);

        var result = await world.Applier.ApplyNowAsync(world.Full("2026.9.19.100"), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("tool update --tool-path", result.Message);
        Assert.Contains("left as it was", result.Message);
        Assert.Equal(UpdatePhase.Failed, world.Store.ReadState().Current!.Phase);
    }

    [Fact]
    public async Task ADowngradeWhoseInstallFailsAfterTheUninstall_PutsThePreviousVersionBack()
    {
        using var world = new ApplierWorld(dotnetExit: args => args.Contains("2026.9.11.532") ? 1 : 0);

        var result = await world.Applier.ApplyNowAsync(world.Full("2026.9.11.532"), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains($"{Previous} was put back", result.Message);
        Assert.Equal(3, world.Runner.Commands.Count);
        Assert.Equal(
            $"tool install --tool-path {world.Tools} DbDataSync --add-source {world.Workspace.RollbackDirectory} --version {Previous}",
            world.Runner.Commands[2]);
    }

    [Fact]
    public async Task ADowngradeThatCannotBePutBack_SaysToReinstallByHand()
    {
        using var world = new ApplierWorld(dotnetExit: args => args.Contains("install") ? 1 : 0);

        var result = await world.Applier.ApplyNowAsync(world.Full("2026.9.11.532"), CancellationToken.None);

        Assert.Contains("also failed", result.Message);
        Assert.Contains("reinstall it by hand", result.Message);
    }

    [Fact]
    public async Task ARunnerThatThrows_IsAFailedUpdate_NotAnException()
    {
        using var world = new ApplierWorld();
        var applier = new UpdateApplier(world.Store, new ThrowingRunner());

        var result = await applier.ApplyNowAsync(world.Full("2026.9.19.100"), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("failed (exit -1)", result.Message);
    }

    [Fact]
    public async Task ASnapshotBeingReplaced_WithNoPackageToKeep_IsRefusedBeforeAnythingChanges()
    {
        // The installed package is not in the store, and a snapshot cannot be re-fetched from nuget.org, so
        // there would be nothing to roll back to.
        using var world = new ApplierWorld(installed: null);

        var result = await world.Applier.ApplyNowAsync(world.Full("2026.9.19.100", previous: "2026.9.17.900-snapshot.gabc1234"), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("could not be found to keep for a rollback", result.Message);
        Assert.Empty(world.Runner.Commands);
    }

    [Fact]
    public async Task AStableVersionBeingReplaced_WithNoPackageToKeep_IsAllowed_BecauseNuGetCanSupplyIt()
    {
        using var world = new ApplierWorld(installed: null);

        var result = await world.Applier.ApplyNowAsync(world.Full("2026.9.19.100"), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.False(Directory.Exists(world.Workspace.RollbackDirectory));
    }

    [Fact]
    public async Task ARequestThatCannotBeUnderstood_IsAFailure_NotACrash()
    {
        using var world = new ApplierWorld();

        var result = await world.Applier.ApplyNowAsync(world.Full("../../etc/passwd"), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("could not be understood", result.Message);
        Assert.Empty(world.Runner.Commands);
    }

    // --- rolling back ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RollBack_AfterApplyNow_PutsThePreviousVersionBack_FromTheKeptPackage()
    {
        using var world = new ApplierWorld();
        var request = world.Full("2026.9.19.100");
        await world.Applier.ApplyNowAsync(request, CancellationToken.None);
        world.Runner.Commands.Clear();

        var result = await world.Applier.RollBackAsync(request, "the service did not answer", CancellationToken.None);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(
            [
                $"tool uninstall --tool-path {world.Tools} DbDataSync",
                $"tool install --tool-path {world.Tools} DbDataSync --add-source {world.Workspace.RollbackDirectory} --version {Previous}",
            ],
            world.Runner.Commands);
        Assert.False(Directory.Exists(world.Workspace.RollbackDirectory));
        Assert.Equal(UpdatePhase.RolledBack, world.Store.ReadState().Current!.Phase);
    }

    [Fact]
    public async Task TheRollbackPackageIsStillThereWhenTheRollbackRuns()
    {
        // The install is from the rollback folder; if the package were missing at that moment the command
        // would fail. The runner checks for it the way dotnet would.
        using var world = new ApplierWorld(dotnetExit: args =>
        {
            var at = args.ToList().IndexOf("--add-source");
            return at >= 0 && args.Contains("install")
                && !File.Exists(Path.Combine(args[at + 1], $"DbDataSync.{Previous}.nupkg")) ? 1 : 0;
        });
        var request = world.Full("2026.9.19.100");
        await world.Applier.ApplyNowAsync(request, CancellationToken.None);

        var result = await world.Applier.RollBackAsync(request, "test", CancellationToken.None);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
    }

    [Fact]
    public async Task ARollbackThatFails_IsRecordedAsFailed()
    {
        using var world = new ApplierWorld();
        var request = world.Full("2026.9.19.100");
        await world.Applier.ApplyNowAsync(request, CancellationToken.None);
        var failing = new UpdateApplier(world.Store, new FakeToolRunner(_ => 1));

        var result = await failing.RollBackAsync(request, "test", CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        var current = world.Store.ReadState().Current!;
        Assert.Equal(UpdatePhase.Failed, current.Phase);
        Assert.Contains("rolling back failed", current.Message);
        Assert.Contains($"Reinstall {Previous} by hand", current.Message);
    }

    [Fact]
    public async Task AKeptPackageThatCannotBeInstalled_FallsBackToNuGet_ForAStableVersion_RatherThanLeaveNothingInstalled()
    {
        // The new version has already been uninstalled by the time the install of the old one fails.
        using var world = new ApplierWorld(dotnetExit: args => args.Contains("--add-source") && args.Contains("install") ? 1 : 0);
        var request = world.Full("2026.9.19.100");
        await world.Applier.ApplyNowAsync(request, CancellationToken.None);
        world.Runner.Commands.Clear();

        var result = await world.Applier.RollBackAsync(request, "test", CancellationToken.None);

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(
            [
                $"tool uninstall --tool-path {world.Tools} DbDataSync",
                $"tool install --tool-path {world.Tools} DbDataSync --add-source {world.Workspace.RollbackDirectory} --version {Previous}",
                $"tool install --tool-path {world.Tools} DbDataSync --version {Previous}",
            ],
            world.Runner.Commands);
    }

    [Fact]
    public async Task AKeptSnapshotThatCannotBeInstalled_HasNoFallback_AndSaysToReinstallByHand()
    {
        // A snapshot cannot be re-fetched from nuget.org, so the kept package is all there is.
        const string snapshot = "2026.9.17.900-snapshot.gabc1234";
        using var world = new ApplierWorld(installed: snapshot);
        var request = world.Full("2026.9.19.100", previous: snapshot);
        await world.Applier.ApplyNowAsync(request, CancellationToken.None);
        var failing = new UpdateApplier(world.Store, new FakeToolRunner(args => args.Contains("install") ? 1 : 0));

        var result = await failing.RollBackAsync(request, "test", CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("Reinstall", world.Store.ReadState().Current!.Message);
    }

    [Fact]
    public async Task ARollbackWithNoPackageToGoBackTo_IsFailed_NotRetriedForever()
    {
        using var world = new ApplierWorld();
        var request = world.Full("2026.9.19.100", previous: "2026.9.17.900-snapshot.gabc1234");
        world.Store.WriteApplied(request);

        var result = await world.Applier.RollBackAsync(request, "test", CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Null(world.Store.ReadApplied());
        Assert.Empty(world.Runner.Commands);
        Assert.Contains("no package to roll back to", world.Store.ReadState().Current!.Message);
    }

    [Fact]
    public async Task Complete_RecordsSuccess_AndDropsTheSpareCopies()
    {
        using var world = new ApplierWorld();
        var request = world.Full("2026.9.19.100");
        await world.Applier.ApplyNowAsync(request, CancellationToken.None);
        Directory.CreateDirectory(world.Workspace.StagedDirectory);

        world.Applier.Complete(request);

        Assert.False(Directory.Exists(world.Workspace.RollbackDirectory));
        Assert.False(Directory.Exists(world.Workspace.StagedDirectory));
        Assert.Equal(UpdatePhase.Succeeded, world.Store.ReadState().Current!.Phase);
    }

    private sealed class ThrowingRunner : IToolCommandRunner
    {
        public Task<ToolCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            throw new System.ComponentModel.Win32Exception("dotnet: command not found");
    }
}

public class ProcessToolCommandRunnerTests
{
    [Fact]
    public async Task RunsTheRealDotnet_AndReturnsItsOutputAndExitCode()
    {
        using var root = new TempDirectory();
        var runner = new ProcessToolCommandRunner(new UpdateWorkspace(root.Path));

        var result = await runner.RunAsync(["--version"], CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", result.Output.Trim());
    }

    [Fact]
    public async Task ANonzeroExit_IsReportedNotThrown()
    {
        using var root = new TempDirectory();
        var runner = new ProcessToolCommandRunner(new UpdateWorkspace(root.Path));

        var result = await runner.RunAsync(["tool", "uninstall", "--tool-path", root.Path, "PackageThatIsNotInstalled"], CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Output);
    }

    [Fact]
    public async Task RunsInThePrivilegedDirectory_WithNuGetPinnedToNuGetOrg_WhateverIsPlantedThere()
    {
        // `dotnet tool` reads nuget.config from its working directory, and a service could plant one where its
        // unit's working directory is. Run in the privileged directory, over a planted config, only nuget.org remains.
        using var data = new TempDirectory();
        using var privileged = new TempDirectory();
        File.WriteAllText(Path.Combine(privileged.Path, "nuget.config"), """
            <configuration><packageSources><add key="hostile" value="https://evil.example/v3/index.json" /></packageSources></configuration>
            """);
        var runner = new ProcessToolCommandRunner(new UpdateWorkspace(data.Path, privileged.Path));

        var result = await runner.RunAsync(["nuget", "list", "source"], CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("nuget.org", result.Output);
        Assert.DoesNotContain("hostile", result.Output);
        Assert.DoesNotContain("evil.example", result.Output);
    }

    [NonWindowsFact]
    public async Task ASymbolicLinkPlantedAsTheNuGetConfig_IsReplaced_NotWrittenThrough()
    {
        using var data = new TempDirectory();
        using var privileged = new TempDirectory();
        var precious = Path.Combine(data.Path, "precious.txt");
        File.WriteAllText(precious, "do not touch");
        File.CreateSymbolicLink(Path.Combine(privileged.Path, "nuget.config"), precious);
        var runner = new ProcessToolCommandRunner(new UpdateWorkspace(data.Path, privileged.Path));

        await runner.RunAsync(["--version"], CancellationToken.None);

        Assert.Equal("do not touch", File.ReadAllText(precious));
        Assert.Contains("api.nuget.org", File.ReadAllText(Path.Combine(privileged.Path, "nuget.config")));
    }
}
