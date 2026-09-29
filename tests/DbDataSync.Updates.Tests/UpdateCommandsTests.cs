namespace DbDataSync.Updates.Tests;

public class UpdateCommandsTests
{
    private static UpdatePlan Plan(string? installed, string target, ReleaseChannel channel, InstallLocation location, string? source = null) =>
        UpdatePlanner.Build(
            installed is null ? null : ReleaseVersion.Parse(installed),
            new ReleaseInfo(ReleaseVersion.Parse(target), channel), location, source, ServiceSituation.None, false);

    private static readonly InstallLocation ToolPath = new(InstallKind.ToolPath, "/opt/dbdatasync");
    private static readonly InstallLocation Global = new(InstallKind.Global, "/home/dan/.dotnet/tools");

    private static string[] Lines(IReadOnlyList<ToolInvocation> steps) => steps.Select(s => $"{s.Purpose}: {string.Join(' ', s.Arguments)}").ToArray();

    [Fact]
    public void AnUpdate_IsOneCommand()
    {
        Assert.Equal(
            ["update: tool update --tool-path /opt/dbdatasync DbDataSync --version 2026.9.18.1918"],
            Lines(UpdateCommands.Install(Plan("2026.9.16.1005", "2026.9.18.1918", ReleaseChannel.Stable, ToolPath))));
    }

    [Fact]
    public void AGlobalInstall_UsesTheGlobalFlag()
    {
        Assert.Equal(
            ["update: tool update --global DbDataSync --version 2026.9.18.1918"],
            Lines(UpdateCommands.Install(Plan("2026.9.16.1005", "2026.9.18.1918", ReleaseChannel.Stable, Global))));
    }

    [Fact]
    public void ASnapshot_AddsItsStagedFolderAsASource()
    {
        Assert.Equal(
            ["update: tool update --tool-path /opt/dbdatasync DbDataSync --add-source /stage/x --version 2026.9.19.1432-snapshot.g65615e7"],
            Lines(UpdateCommands.Install(Plan("2026.9.18.1918", "2026.9.19.1432-snapshot.g65615e7", ReleaseChannel.Snapshot, ToolPath, "/stage/x"))));
    }

    [Fact]
    public void ALowerVersion_IsAnUninstallThenAnInstall()
    {
        Assert.Equal(
            [
                "remove: tool uninstall --tool-path /opt/dbdatasync DbDataSync",
                "install: tool install --tool-path /opt/dbdatasync DbDataSync --version 2026.9.11.532",
            ],
            Lines(UpdateCommands.Install(Plan("2026.9.16.1005", "2026.9.11.532", ReleaseChannel.Stable, ToolPath))));
    }

    [Fact]
    public void TheVersionAlreadyInstalled_IsNoCommands()
    {
        Assert.Empty(UpdateCommands.Install(Plan("2026.9.18.1918", "2026.9.18.1918", ReleaseChannel.Stable, ToolPath)));
    }

    // --- phase 196L: into a slot that is not running ----------------------------------------------------------

    [Fact]
    public void IntoAnEmptySlot_IsOneInstall_AtTheSlotsToolPath()
    {
        var step = UpdateCommands.IntoEmptySlot("/opt/dbdatasync/versions/b", "2026.9.18.1918", null);

        Assert.Equal("install: tool install --tool-path /opt/dbdatasync/versions/b DbDataSync --version 2026.9.18.1918", Lines([step])[0]);
    }

    [Fact]
    public void IntoAnEmptySlot_ASnapshotOrAKeptPackage_AddsItsFolderAsASource()
    {
        var step = UpdateCommands.IntoEmptySlot("/opt/dbdatasync/versions/a", "2026.9.19.1432-snapshot.g65615e7", "/stage/x");

        Assert.Equal(
            "install: tool install --tool-path /opt/dbdatasync/versions/a DbDataSync --add-source /stage/x --version 2026.9.19.1432-snapshot.g65615e7",
            Lines([step])[0]);
    }

    [Theory]
    [InlineData("2026.9.18.1918", "2026.9.18.1918", true)]
    [InlineData("2026.9.19.1432-snapshot.g65615e7", "2026.9.19.1432-snapshot.g65615e7", true)]
    [InlineData("2026.9.16.1005", "2026.9.18.1918", false)]
    [InlineData(null, "2026.9.18.1918", false)]
    [InlineData("not-a-version", "2026.9.18.1918", false)]
    public void SlotHolds_ComparesVersions_NotStrings(string? slot, string wanted, bool expected) =>
        Assert.Equal(expected, UpdateCommands.SlotHolds(slot, wanted));
}
