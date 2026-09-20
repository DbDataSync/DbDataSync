using System.Net;

namespace DbDataSync.Updates.Tests;

/// <summary>
/// The step the systemd unit runs as root before every start, and the trust boundary it sits on.
/// <para>
/// The service that wrote the request runs as its own user and may be compromised, so these tests are written from
/// the point of view of an attacker who controls everything in the service's directory — and assert that none of
/// it can steer what root installs. The version named in a request is the only thing read from it.
/// </para>
/// </summary>
public class PrivilegedApplyTests
{
    private const string Running = ApplierWorld.Running;

    // --- the ordinary path ----------------------------------------------------------------------------------

    [Fact]
    public async Task NothingPending_IsNothingToDo_AndRunsNothing()
    {
        using var world = new ApplierWorld();

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.NothingToDo, result.Outcome);
        Assert.Empty(world.Runner.Commands);
    }

    [Fact]
    public async Task ARequestedStableVersion_IsInstalled_KeepingTheOldPackage_AndRecordedAsOnTrialInRootsDirectory()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.19.100");

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Equal([$"tool update --tool-path {world.Tools} DbDataSync --version 2026.9.19.100"], world.Runner.Commands);
        Assert.Null(world.Store.ReadPending());

        // The record and the spare copy are where the service cannot write, and not where it can.
        Assert.True(File.Exists(world.Workspace.AppliedPath));
        Assert.StartsWith(world.Privileged, world.Workspace.AppliedPath);
        Assert.False(File.Exists(Path.Combine(world.Data, "updates", "applied-update.json")));
        Assert.Equal($"package of {Running}", File.ReadAllText(Path.Combine(world.Privileged, "rollback", $"DbDataSync.{Running}.nupkg")));

        var applied = world.Store.ReadApplied()!;
        Assert.Equal("2026.9.19.100", applied.TargetVersion);
        Assert.Equal(Running, applied.PreviousVersion);

        var state = world.Store.ReadState().Current!;
        Assert.Equal(UpdatePhase.Restarting, state.Phase);
        Assert.Equal("dan", state.RequestedBy);
    }

    [Fact]
    public async Task ARequestedDowngrade_IsAnUninstallAndAnInstall()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.11.532");

        await world.ApplyPending();

        Assert.Equal(
            [
                $"tool uninstall --tool-path {world.Tools} DbDataSync",
                $"tool install --tool-path {world.Tools} DbDataSync --version 2026.9.11.532",
            ],
            world.Runner.Commands);
    }

    [Fact]
    public async Task ABetaVersion_IsInstalledByExactVersion()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.12.721-beta");

        var result = await world.ApplyPending();

        // Lower than the running stable of the same date, so it is the uninstall + install path.
        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Contains(world.Runner.Commands, c => c.EndsWith("--version 2026.9.12.721-beta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheVersionAlreadyRunning_ChangesNothing_AndClearsTheRequest()
    {
        using var world = new ApplierWorld();
        world.Request(Running);

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.NothingToDo, result.Outcome);
        Assert.Empty(world.Runner.Commands);
        Assert.Null(world.Store.ReadPending());
        Assert.Equal(UpdatePhase.Succeeded, world.Store.ReadState().Current!.Phase);
    }

    // --- a request cannot steer what root installs -----------------------------------------------------------

    [Fact]
    public async Task ExtraFieldsInARequest_AreIgnored_ThereIsNowhereToNameASourceOrAFolder()
    {
        using var world = new ApplierWorld();
        Directory.CreateDirectory(world.Workspace.Directory);
        File.WriteAllText(world.Workspace.PendingPath, """
            {
              "targetVersion": "2026.9.19.100",
              "requestedUtc": "2026-09-19T00:00:00Z",
              "requestedBy": "mallory",
              "sourceDirectory": "/tmp/evil",
              "toolRoot": "/etc",
              "installKind": "global",
              "previousVersion": "0.0.1",
              "targetChannel": "snapshot",
              "packageUrl": "https://example.com/evil.nupkg"
            }
            """);

        await world.ApplyPending();

        // The install location is the step's own, the source is nuget.org's default, the channel is the version's.
        Assert.Equal([$"tool update --tool-path {world.Tools} DbDataSync --version 2026.9.19.100"], world.Runner.Commands);
        var applied = world.Store.ReadApplied()!;
        Assert.Equal(world.Tools, applied.ToolRoot);
        Assert.Equal(Running, applied.PreviousVersion);
        Assert.Equal(ReleaseChannel.Stable, applied.TargetChannel);
        Assert.Equal(InstallKind.ToolPath, applied.InstallKind);
        Assert.Null(applied.SourceDirectory);
    }

    [Fact]
    public async Task AVersionThatIsNotInThePinnedSources_IsRefused_WhateverTheServiceSays()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.17.1");

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("is not a stable release that can be found", result.Message);
        Assert.Empty(world.Runner.Commands);
        Assert.Null(world.Store.ReadPending());
        Assert.Equal(UpdatePhase.Failed, world.Store.ReadState().Current!.Phase);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData("2026.9.19.1432-alpha.1")]
    [InlineData("2026.9.19.100/../../x")]
    public async Task ARequestThatIsNotAVersionOfThisProduct_IsRefused(string version)
    {
        using var world = new ApplierWorld();
        world.Request(version);

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Empty(world.Runner.Commands);
        Assert.Null(world.Store.ReadPending());
    }

    [Fact]
    public async Task AnInstallationThatIsNotADotnetTool_IsRefused()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.19.100");

        var result = await world.ApplyPending(world.Context(new InstallLocation(InstallKind.NotAToolInstall, null)));

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("not a dotnet tool install", result.Message);
        Assert.Empty(world.Runner.Commands);
    }

    [Fact]
    public async Task SourcesThatCannotBeReached_MeanNothingIsInstalled_AndTheServiceStartsOnWhatItHas()
    {
        using var world = new ApplierWorld(nugetStatus: HttpStatusCode.ServiceUnavailable);
        world.Request("2026.9.19.100");

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("Could not read the release sources", result.Message);
        Assert.Empty(world.Runner.Commands);
    }

    [Fact]
    public async Task TheRunningVersion_IsTheStepsOwn_NotWhatTheRequestClaims()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.16.1005");

        // The request asks for exactly what the step's own version says is running, so it is already there.
        Assert.Equal(ApplyOutcome.NothingToDo, (await world.ApplyPending()).Outcome);

        world.Request("2026.9.19.100");
        var result = await world.ApplyPending(world.Context(running: "2026.9.19.100"));
        Assert.Equal(ApplyOutcome.NothingToDo, result.Outcome);
    }

    // --- a snapshot is downloaded by root, from the pinned source ---------------------------------------------

    [Fact]
    public async Task ASnapshot_IsDownloadedByTheStep_IntoADirectoryOnlyItUses_AndThatDirectoryIsGoneAfterwards()
    {
        using var world = new ApplierWorld();
        world.Request(ApplierWorld.Snapshot);

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        var command = Assert.Single(world.Runner.Commands);
        var source = command.Split("--add-source ")[1].Split(' ')[0];
        Assert.StartsWith(Path.GetTempPath().TrimEnd('/'), source);
        Assert.Contains("dbdatasync-update-", source);
        Assert.False(Directory.Exists(source), "the download is cleaned up");
        Assert.Null(world.Store.ReadApplied()!.SourceDirectory);
    }

    [Fact]
    public async Task APackageTheServiceStaged_IsNeverUsed()
    {
        using var world = new ApplierWorld();
        var planted = Path.Combine(world.Data, "updates", "staged", ApplierWorld.Snapshot);
        Directory.CreateDirectory(planted);
        File.WriteAllBytes(Path.Combine(planted, $"DbDataSync.{ApplierWorld.Snapshot}.nupkg"), [0xBA, 0xD0]);
        world.Request(ApplierWorld.Snapshot);

        await world.ApplyPending();

        var command = Assert.Single(world.Runner.Commands);
        Assert.DoesNotContain(planted, command);
        Assert.DoesNotContain(world.Data, command);
    }

    [Fact]
    public async Task ASnapshotThatFailsItsChecksum_IsRefused_AndNothingIsInstalled()
    {
        using var world = new ApplierWorld(snapshotServed: ApplierWorld.SnapshotPackage[..100]);
        world.Request(ApplierWorld.Snapshot);

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("SHA-512", result.Message);
        Assert.Empty(world.Runner.Commands);
    }

    // --- when the install itself goes wrong -------------------------------------------------------------------

    [Fact]
    public async Task AFailedInstall_IsRecorded_ClearsTheRequest_AndNeverMarksAnythingApplied()
    {
        using var world = new ApplierWorld(dotnetExit: _ => 1);
        world.Request("2026.9.19.100");

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("left as it was", result.Message);
        Assert.Null(world.Store.ReadPending());
        Assert.Null(world.Store.ReadApplied());
    }

    [Fact]
    public async Task ASnapshotBeingReplaced_WithNoPackageToKeep_IsRefusedBeforeAnythingChanges()
    {
        using var world = new ApplierWorld(installed: null);
        world.Request("2026.9.19.100");

        var result = await world.ApplyPending(world.Context(running: "2026.9.17.900-snapshot.gabc1234"));

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("could not be found to keep for a rollback", result.Message);
        Assert.Empty(world.Runner.Commands);
    }

    [Fact]
    public async Task TheRequestersName_IsDisplayText_StrippedAndBounded()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.19.100", requestedBy: "dan\n\u001b[31m" + new string('x', 900));

        await world.ApplyPending();

        var requestedBy = world.Store.ReadState().Current!.RequestedBy!;
        Assert.Equal(500, requestedBy.Length);
        Assert.DoesNotContain('\n', requestedBy);
        Assert.DoesNotContain('\u001b', requestedBy);
    }

    // --- the next start: confirmed, or rolled back -------------------------------------------------------------

    [Fact]
    public async Task TheNextStartAfterAnUnconfirmedUpdate_RollsBack_FromRootsOwnCopy()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.19.100");
        await world.ApplyPending();
        world.Runner.Commands.Clear();

        // The service restarted and this ran again; the new version never confirmed itself.
        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(
            [
                $"tool uninstall --tool-path {world.Tools} DbDataSync",
                $"tool install --tool-path {world.Tools} DbDataSync --add-source {Path.Combine(world.Privileged, "rollback")} --version {Running}",
            ],
            world.Runner.Commands);
        Assert.Null(world.Store.ReadApplied());
        Assert.False(Directory.Exists(Path.Combine(world.Privileged, "rollback")));
        Assert.Equal(UpdatePhase.RolledBack, world.Store.ReadState().Current!.Phase);
    }

    [Fact]
    public async Task ARollbackNeverUsesAPackageTheServiceCouldHavePlanted()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.19.100");
        await world.ApplyPending();
        world.Runner.Commands.Clear();

        // A compromised service plants a "rollback" package in every place it can write.
        foreach (var directory in new[] { Path.Combine(world.Data, "updates", "rollback"), Path.Combine(world.Data, "rollback") })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"DbDataSync.{Running}.nupkg"), [0xBA, 0xD0]);
        }

        await world.ApplyPending();

        var install = world.Runner.Commands[^1];
        Assert.Contains(Path.Combine(world.Privileged, "rollback"), install);
        Assert.DoesNotContain(world.Data, install);
    }

    [Fact]
    public async Task AConfirmedUpdate_IsSettledAtTheNextStart_NotRolledBack()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.19.100");
        await world.ApplyPending();
        world.Runner.Commands.Clear();

        // The new version served long enough, and said so. (The service does this itself; see ConfirmAsync.)
        Assert.True(await world.Applier.ConfirmAsync("2026.9.19.100", CancellationToken.None));

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.NothingToDo, result.Outcome);
        Assert.Empty(world.Runner.Commands);
        Assert.Null(world.Store.ReadApplied());
        Assert.Null(world.Store.ReadConfirmed());
        Assert.False(Directory.Exists(Path.Combine(world.Privileged, "rollback")));
        Assert.Equal(UpdatePhase.Succeeded, world.Store.ReadState().Current!.Phase);
    }

    [Fact]
    public async Task ANoteConfirmingADifferentVersion_DoesNotSaveAnUpdate()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.19.100");
        await world.ApplyPending();
        world.Runner.Commands.Clear();
        world.Store.WriteConfirmed("2026.9.18.1918");

        var result = await world.ApplyPending();

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
    }

    [Fact]
    public async Task ANewRequest_IsAppliedTheSameStartThatSettlesTheLastOne()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.18.1918");
        await world.ApplyPending();
        await world.Applier.ConfirmAsync("2026.9.18.1918", CancellationToken.None);
        world.Runner.Commands.Clear();

        world.Request("2026.9.19.100");
        var result = await world.ApplyPending(world.Context(running: "2026.9.18.1918"));

        Assert.Equal(ApplyOutcome.Applied, result.Outcome);
        Assert.Equal([$"tool update --tool-path {world.Tools} DbDataSync --version 2026.9.19.100"], world.Runner.Commands);
        Assert.Equal("2026.9.19.100", world.Store.ReadApplied()!.TargetVersion);
    }

    [Fact]
    public async Task ARollbackThatFails_IsRecorded_AndDoesNotRepeatOnEveryStart()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.19.100");
        await world.ApplyPending();
        var failing = new UpdateApplier(world.Store, new FakeToolRunner(_ => 1));

        var result = await failing.ApplyPendingAsync(world.Context(), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Null(world.Store.ReadApplied());
        Assert.Contains("rolling back failed", world.Store.ReadState().Current!.Message);
    }

    // --- confirming, from the service's side -------------------------------------------------------------------

    [Fact]
    public async Task Confirm_IsTheServicesNote_AndCannotTouchRootsRecordOrItsSpareCopies()
    {
        using var world = new ApplierWorld();
        world.Request("2026.9.19.100");
        await world.ApplyPending();

        var confirmed = await world.Applier.ConfirmAsync("2026.9.19.100+abcdef", CancellationToken.None);

        Assert.True(confirmed);
        Assert.Equal("2026.9.19.100", world.Store.ReadConfirmed()!.TargetVersion);
        Assert.Equal(UpdatePhase.Succeeded, world.Store.ReadState().Current!.Phase);
        // Root's record and the spare package are untouched until root's own next start.
        Assert.NotNull(world.Store.ReadApplied());
        Assert.True(Directory.Exists(Path.Combine(world.Privileged, "rollback")));
    }

    [Fact]
    public async Task Confirm_IsFalse_ForAnOlderVersion_ForNothingOnTrial_AndAfterItHasAlreadyHappened()
    {
        using var world = new ApplierWorld();
        Assert.False(await world.Applier.ConfirmAsync("2026.9.19.100", CancellationToken.None));

        world.Request("2026.9.19.100");
        await world.ApplyPending();

        Assert.False(await world.Applier.ConfirmAsync(Running, CancellationToken.None));
        Assert.False(await world.Applier.ConfirmAsync("not a version", CancellationToken.None));
        Assert.True(await world.Applier.ConfirmAsync("2026.9.19.100", CancellationToken.None));
        Assert.False(await world.Applier.ConfirmAsync("2026.9.19.100", CancellationToken.None));
    }
}
